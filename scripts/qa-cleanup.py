#!/usr/bin/env python3
"""Remove or inspect only resources belonging to one isolated Acropolis QA run."""
import argparse
import json
import re
import subprocess
import sys
import time
from dataclasses import dataclass

PROJECT_PATTERN = re.compile(r"acropolis_test_[a-z0-9_]+")
TOTAL_TIMEOUT_SECONDS = 300
COMMAND_TIMEOUT_SECONDS = 30
MAX_CONTAINERS = 256


class CleanupProblem(Exception):
    def __init__(self, kind, name, unknown=True):
        super().__init__(kind)
        self.kind = kind
        self.name = name
        self.unknown = unknown

    def safe(self):
        return {"type": self.kind, "name": self.name}


@dataclass(frozen=True)
class Resource:
    kind: str
    name: str
    identifier: str = ""


class Cleanup:
    def __init__(self, project, runner=subprocess.run, clock=time.monotonic):
        if not PROJECT_PATTERN.fullmatch(project):
            raise ValueError("Unsafe QA project")
        self.project = project
        self.runner = runner
        self.clock = clock
        self.deadline = clock() + TOTAL_TIMEOUT_SECONDS

    def _command(self, arguments, name, purpose, allow_absent=False, resource_kind=None):
        remaining = self.deadline - self.clock()
        if remaining <= 0:
            raise CleanupProblem("total_timeout", name)
        try:
            result = self.runner(
                ["docker", *arguments], capture_output=True, text=True,
                timeout=min(COMMAND_TIMEOUT_SECONDS, remaining),
            )
        except FileNotFoundError:
            raise CleanupProblem("docker_unavailable", name) from None
        except subprocess.TimeoutExpired:
            raise CleanupProblem("command_timeout", name) from None
        except OSError:
            raise CleanupProblem("docker_command_error", name) from None
        if result.returncode != 0:
            # Generic inspect supplies a name-bearing error. Never infer absence
            # from a daemon/transport failure, an empty list or a partial name.
            patterns = [r"(?i:(?:error(?: response from daemon)?:\s*)?no such object:\s*)" + re.escape(name)]
            if resource_kind == "container":
                patterns.append(r"(?i:(?:error(?: response from daemon)?:\s*)?no such container:\s*)" + re.escape(name))
            if resource_kind in ("volume", "network"):
                patterns.append(r"(?i:error response from daemon:\s*" + resource_kind + " )" + re.escape(name) + r"(?i: not found)")
            absence = result.returncode == 1 and any(re.fullmatch(pattern, result.stderr.strip()) for pattern in patterns)
            if allow_absent and absence:
                return None
            raise CleanupProblem(purpose + "_failed", name)
        return result.stdout

    def _container_name(self, name):
        return bool(re.fullmatch(re.escape(self.project) + r"[-_][a-z0-9][a-z0-9_.-]*", name))

    def _fields(self, output, count, name):
        try:
            fields = [json.loads(line) for line in output.strip().splitlines()]
            if len(fields) != count or any(value is not None and not isinstance(value, str) for value in fields):
                raise ValueError()
            return [value or "" for value in fields]
        except (ValueError, TypeError):
            raise CleanupProblem("metadata_invalid", name) from None

    def inventory(self):
        candidates = {}
        for label in ("com.docker.compose.project", "acropolis.qa.run"):
            output = self._command([
                "ps", "-a", "--filter", f"label={label}={self.project}",
                "--format", "{{.ID}}\t{{.Names}}",
            ], self.project, "inventory")
            for line in output.splitlines():
                parts = line.split("\t")
                if len(parts) != 2 or not re.fullmatch(r"[a-f0-9]{12,64}", parts[0]) or not self._container_name(parts[1]):
                    raise CleanupProblem("inventory_invalid", self.project)
                identifier, name = parts
                if name in candidates and candidates[name] != identifier:
                    raise CleanupProblem("inventory_changed", name)
                candidates[name] = identifier
                if len(candidates) > MAX_CONTAINERS:
                    raise CleanupProblem("inventory_limit", self.project)

        resources = []
        container_format = "\n".join((
            "{{json .Id}}", "{{json .Name}}",
            '{{json (index .Config.Labels "com.docker.compose.project")}}',
            '{{json (index .Config.Labels "acropolis.qa.run")}}',
        ))
        for name, identifier in sorted(candidates.items()):
            output = self._command(
                ["inspect", "--type", "container", "--format", container_format, name],
                name, "inspection", allow_absent=True, resource_kind="container",
            )
            if output is None:
                continue
            current_id, current_name, compose_project, run_project = self._fields(output, 4, name)
            own = (compose_project == self.project and run_project in ("", self.project)) or (not compose_project and run_project == self.project)
            if current_name != "/" + name or not re.fullmatch(r"[a-f0-9]{64}", current_id) or not current_id.startswith(identifier):
                raise CleanupProblem("inventory_changed", name)
            if not own:
                raise CleanupProblem("ownership_mismatch", name, unknown=False)
            resources.append(Resource("container", name, current_id))

        # These are all exact named volumes/network defined by compose.qa.yml.
        for kind, names in (
            ("volume", [self.project + suffix for suffix in ("_postgres", "_keyring", "_caddy")]),
            ("network", [self.project + "_network"]),
        ):
            template = '\n'.join(("{{json .Name}}", '{{json (index .Labels "acropolis.qa.run")}}'))
            for name in names:
                output = self._command(
                    ["inspect", "--type", kind, "--format", template, name],
                    name, "inspection", allow_absent=True, resource_kind=kind,
                )
                if output is None:
                    continue
                current_name, owner = self._fields(output, 2, name)
                if current_name != name or owner != self.project:
                    raise CleanupProblem("ownership_mismatch", name, unknown=False)
                resources.append(Resource(kind, name))
        return resources

    def run(self, mode):
        result = {
            "mode": mode, "project": self.project, "ownershipVerified": False,
            "cleanupComplete": False, "cleanupUnknown": False, "errors": [],
        }
        try:
            # Validate every resource before the first removal.
            resources = self.inventory()
            result["ownershipVerified"] = True
            if mode == "check-ownership":
                result["cleanupComplete"] = not resources
                return result, 0
            if mode == "remove-owned":
                for resource in resources:
                    arguments = (
                        ["rm", "--force", resource.identifier] if resource.kind == "container"
                        else [resource.kind, "rm", resource.name]
                    )
                    try:
                        self._command(arguments, resource.name, "removal")
                    except CleanupProblem as error:
                        result["errors"].append(error.safe())
                        result["cleanupUnknown"] |= error.unknown
                resources = self.inventory()
            if resources:
                result["errors"].extend(
                    {"type": "resource_remaining", "name": resource.name} for resource in resources
                )
            result["cleanupComplete"] = not resources and not result["errors"]
            return result, 0 if result["cleanupComplete"] else 1
        except CleanupProblem as error:
            result["errors"].append(error.safe())
            result["cleanupUnknown"] |= error.unknown
            return result, 1


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True)
    modes = parser.add_mutually_exclusive_group(required=True)
    for mode in ("check-ownership", "verify-absent", "remove-owned"):
        modes.add_argument("--" + mode, dest="mode", action="store_const", const=mode)
    arguments = parser.parse_args(argv)
    try:
        cleanup = Cleanup(arguments.project)
    except ValueError:
        print(json.dumps({
            "cleanupComplete": False, "cleanupUnknown": True,
            "errors": [{"type": "unsafe_project", "name": "invalid_project"}],
        }, sort_keys=True))
        return 2
    result, code = cleanup.run(arguments.mode)
    print(json.dumps(result, sort_keys=True))
    return code


if __name__ == "__main__":
    sys.exit(main())
