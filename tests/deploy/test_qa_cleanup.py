import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("acropolis_qa_cleanup", ROOT / "scripts/qa-cleanup.py")
cleanup = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = cleanup
SPEC.loader.exec_module(cleanup)
PROJECT = "acropolis_test_cleanup_123"


class FakeDocker:
    def __init__(self, populated=False):
        self.calls = []
        self.removals = []
        self.fault = None
        self.containers = {}
        self.volumes = {}
        self.networks = {}
        if populated:
            self.containers[PROJECT + "-web-1"] = ("a" * 64, PROJECT, "")
            self.containers[PROJECT + "_runtime_uid"] = ("b" * 64, "", PROJECT)
            self.volumes = {PROJECT + suffix: PROJECT for suffix in ("_postgres", "_keyring", "_caddy")}
            self.networks[PROJECT + "_network"] = PROJECT

    def __call__(self, arguments, **options):
        self.calls.append((arguments, options))
        if self.fault:
            failed = self.fault(arguments, options)
            if failed is not None:
                return failed
        command = arguments[1:]
        if command[0] == "ps":
            label = command[command.index("--filter") + 1]
            position = 1 if label.startswith("label=com.docker.compose.project=") else 2
            value = label.split("=", 2)[2]
            output = "".join(identifier + "\t" + name + "\n" for name, (identifier, compose, run) in self.containers.items() if (compose, run)[position - 1] == value)
            return subprocess.CompletedProcess(arguments, 0, output, "")
        if command[0] == "inspect":
            kind = command[command.index("--type") + 1]
            name = command[-1]
            if kind == "container" and name in self.containers:
                identifier, compose, run = self.containers[name]
                values = (identifier, "/" + name, compose, run)
            elif kind in ("volume", "network") and name in (self.volumes if kind == "volume" else self.networks):
                values = (name, (self.volumes if kind == "volume" else self.networks)[name])
            else:
                return subprocess.CompletedProcess(arguments, 1, "", "Error: No such object: " + name)
            return subprocess.CompletedProcess(arguments, 0, "\n".join(json.dumps(value) for value in values) + "\n", "")
        if command[0] == "rm":
            identifier = command[-1]
            name = next(name for name, fields in self.containers.items() if fields[0] == identifier)
            del self.containers[name]
        elif command[:2] == ["volume", "rm"]:
            name = command[-1]
            del self.volumes[name]
        elif command[:2] == ["network", "rm"]:
            name = command[-1]
            del self.networks[name]
        else:
            raise AssertionError("Unexpected Docker operation")
        self.removals.append(name)
        return subprocess.CompletedProcess(arguments, 0, name + "\n", "")


class QaCleanupTests(unittest.TestCase):
    def execute(self, docker, mode="verify-absent", clock=lambda: 0.0):
        return cleanup.Cleanup(PROJECT, runner=docker, clock=clock).run(mode)

    def test_explicit_absence_is_verified_and_timeouts_are_bounded(self):
        docker = FakeDocker()
        result, code = self.execute(docker)
        self.assertEqual(0, code)
        self.assertTrue(result["cleanupComplete"])
        self.assertFalse(result["cleanupUnknown"])
        self.assertEqual(6, len(docker.calls))
        self.assertTrue(all(0 < options["timeout"] <= 30 for _, options in docker.calls))
        self.assertTrue(all(".Config.Env" not in " ".join(arguments) for arguments, _ in docker.calls))

    def test_absence_error_case_is_ignored_only_for_exact_name(self):
        docker = FakeDocker()
        docker.fault = lambda arguments, options: subprocess.CompletedProcess(arguments, 1, "", "ERROR: NO SUCH OBJECT: " + arguments[-1]) if arguments[1] == "inspect" else None
        result, code = self.execute(docker)
        self.assertEqual(0, code)
        self.assertTrue(result["cleanupComplete"])

    def test_absence_with_different_name_case_is_unknown(self):
        docker = FakeDocker()
        docker.fault = lambda arguments, options: subprocess.CompletedProcess(arguments, 1, "", "Error: No such object: " + arguments[-1].upper()) if arguments[1] == "inspect" else None
        result, code = self.execute(docker)
        self.assertEqual(1, code)
        self.assertTrue(result["cleanupUnknown"])
        self.assertFalse(result["cleanupComplete"])

    def test_absence_for_a_different_name_is_unknown(self):
        docker = FakeDocker()
        docker.fault = lambda arguments, options: subprocess.CompletedProcess(arguments, 1, "", "Error: No such object: " + arguments[-1] + "_other") if arguments[1] == "inspect" else None
        result, code = self.execute(docker)
        self.assertEqual(1, code)
        self.assertTrue(result["cleanupUnknown"])

    def test_docker29_typed_absence_requires_matching_type_and_exact_name(self):
        for expected_type in ("volume", "network"):
            with self.subTest(kind=expected_type):
                docker = FakeDocker()
                def typed_absence(arguments, options):
                    if arguments[1] == "inspect" and arguments[arguments.index("--type") + 1] == expected_type:
                        return subprocess.CompletedProcess(arguments, 1, "", "Error response from daemon: " + expected_type + " " + arguments[-1] + " not found")
                    return None
                docker.fault = typed_absence
                result, code = self.execute(docker)
                self.assertEqual(0, code)
                self.assertTrue(result["cleanupComplete"])
                self.assertFalse(result["cleanupUnknown"])

    def test_typed_absence_for_wrong_type_name_or_case_is_unknown(self):
        for kind, name_change in (("network", lambda name: name), ("volume", lambda name: name + "_other"), ("volume", lambda name: name.upper())):
            with self.subTest(kind=kind, name_change=name_change):
                docker = FakeDocker()
                docker.fault = lambda arguments, options: subprocess.CompletedProcess(arguments, 1, "", "Error response from daemon: " + kind + " " + name_change(arguments[-1]) + " not found") if arguments[1] == "inspect" and arguments[arguments.index("--type") + 1] == "volume" else None
                result, code = self.execute(docker)
                self.assertEqual(1, code)
                self.assertTrue(result["cleanupUnknown"])
                self.assertFalse(result["cleanupComplete"])

    def test_docker29_get_volume_absence_is_verified(self):
        docker = FakeDocker()
        def missing_volume(arguments, options):
            if arguments[1] == "inspect" and arguments[arguments.index("--type") + 1] == "volume":
                return subprocess.CompletedProcess(arguments, 1, "", "Error response from daemon: get " + arguments[-1] + ": no such volume\n")
            return None
        docker.fault = missing_volume
        result, code = self.execute(docker)
        self.assertEqual(0, code)
        self.assertTrue(result["ownershipVerified"])
        self.assertTrue(result["cleanupComplete"])
        self.assertFalse(result["cleanupUnknown"])

    def test_get_volume_absence_requires_exact_name_and_error(self):
        errors = {
            "different_name": lambda name: "Error response from daemon: get " + name + "_other: no such volume",
            "partial_name": lambda name: "Error response from daemon: get " + name[:-1] + ": no such volume",
            "name_case": lambda name: "Error response from daemon: get " + name.upper() + ": no such volume",
            "wrong_type": lambda name: "Error response from daemon: get " + name + ": no such network",
            "error_case": lambda name: "ERROR RESPONSE FROM DAEMON: GET " + name + ": NO SUCH VOLUME",
        }
        for label, error in errors.items():
            with self.subTest(error=label):
                docker = FakeDocker(populated=True)
                def invalid_absence(arguments, options):
                    if arguments[1] == "inspect" and arguments[arguments.index("--type") + 1] == "volume":
                        return subprocess.CompletedProcess(arguments, 1, "", error(arguments[-1]))
                    return None
                docker.fault = invalid_absence
                result, code = self.execute(docker, "remove-owned")
                self.assertEqual(1, code)
                self.assertEqual("inspection_failed", result["errors"][0]["type"])
                self.assertTrue(result["cleanupUnknown"])
                self.assertFalse(result["cleanupComplete"])
                self.assertFalse(docker.removals)

    def test_get_volume_absence_is_rejected_for_other_resource_kinds(self):
        for kind in ("container", "network"):
            with self.subTest(kind=kind):
                docker = FakeDocker(populated=True)
                def wrong_inspection_kind(arguments, options):
                    if arguments[1] == "inspect" and arguments[arguments.index("--type") + 1] == kind:
                        return subprocess.CompletedProcess(arguments, 1, "", "Error response from daemon: get " + arguments[-1] + ": no such volume")
                    return None
                docker.fault = wrong_inspection_kind
                result, code = self.execute(docker, "remove-owned")
                self.assertEqual(1, code)
                self.assertTrue(result["cleanupUnknown"])
                self.assertFalse(result["cleanupComplete"])
                self.assertFalse(docker.removals)

    def test_get_volume_absence_requires_exit_code_one(self):
        for code in (2, 125, -15):
            with self.subTest(code=code):
                docker = FakeDocker(populated=True)
                def wrong_exit(arguments, options):
                    if arguments[1] == "inspect" and arguments[arguments.index("--type") + 1] == "volume":
                        return subprocess.CompletedProcess(arguments, code, "", "Error response from daemon: get " + arguments[-1] + ": no such volume")
                    return None
                docker.fault = wrong_exit
                result, status = self.execute(docker, "remove-owned")
                self.assertEqual(1, status)
                self.assertTrue(result["cleanupUnknown"])
                self.assertFalse(result["cleanupComplete"])
                self.assertFalse(docker.removals)

    def test_get_volume_absence_does_not_hide_daemon_or_multiple_errors(self):
        errors = {
            "daemon_prefix": lambda message: "Cannot connect to the Docker daemon: " + message,
            "daemon_suffix": lambda message: message + ": Docker daemon unavailable",
            "multiline_prefix": lambda message: message.replace(": get ", ":\nget "),
            "multiple_lines": lambda message: message + "\nCannot connect to the Docker daemon",
            "repeated_error": lambda message: message + "\n" + message,
        }
        for label, error in errors.items():
            with self.subTest(error=label):
                docker = FakeDocker(populated=True)
                def misleading_error(arguments, options):
                    if arguments[1] == "inspect" and arguments[arguments.index("--type") + 1] == "volume":
                        message = "Error response from daemon: get " + arguments[-1] + ": no such volume"
                        return subprocess.CompletedProcess(arguments, 1, "", error(message))
                    return None
                docker.fault = misleading_error
                result, code = self.execute(docker, "remove-owned")
                self.assertEqual(1, code)
                self.assertTrue(result["cleanupUnknown"])
                self.assertFalse(result["cleanupComplete"])
                self.assertFalse(docker.removals)

    def test_absence_requires_exit_code_one(self):
        for code in (2, 125, -15):
            for typed in (False, True):
                with self.subTest(code=code, typed=typed):
                    docker = FakeDocker()
                    def wrong_exit(arguments, options):
                        if arguments[1] == "inspect":
                            kind = arguments[arguments.index("--type") + 1]
                            error = "Error response from daemon: " + kind + " " + arguments[-1] + " not found" if typed else "Error: No such object: " + arguments[-1]
                            return subprocess.CompletedProcess(arguments, code, "", error)
                        return None
                    docker.fault = wrong_exit
                    result, status = self.execute(docker)
                    self.assertEqual(1, status)
                    self.assertTrue(result["cleanupUnknown"])
                    self.assertFalse(result["cleanupComplete"])

    def test_docker_missing_is_unknown_without_mutation(self):
        def missing(*args, **kwargs):
            raise FileNotFoundError()
        result, code = self.execute(missing, "remove-owned")
        self.assertEqual(1, code)
        self.assertEqual("docker_unavailable", result["errors"][0]["type"])
        self.assertTrue(result["cleanupUnknown"])

    def test_daemon_failure_is_not_absence(self):
        docker = FakeDocker()
        docker.fault = lambda arguments, options: subprocess.CompletedProcess(arguments, 1, "", "Cannot connect to the Docker daemon") if arguments[1] == "inspect" else None
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(1, code)
        self.assertTrue(result["cleanupUnknown"])
        self.assertFalse(docker.removals)

    def test_failed_listing_is_unknown_not_empty(self):
        docker = FakeDocker()
        docker.fault = lambda arguments, options: subprocess.CompletedProcess(arguments, 1, "", "list failure")
        result, code = self.execute(docker)
        self.assertEqual(1, code)
        self.assertEqual("inventory_failed", result["errors"][0]["type"])
        self.assertFalse(result["cleanupComplete"])

    def test_command_timeout_is_unknown(self):
        docker = FakeDocker()
        def timed_out(arguments, options):
            raise subprocess.TimeoutExpired(arguments, options["timeout"])
        docker.fault = timed_out
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(1, code)
        self.assertTrue(result["cleanupUnknown"])
        self.assertEqual("command_timeout", result["errors"][0]["type"])
        self.assertFalse(docker.removals)

    def test_total_deadline_prevents_more_docker_calls(self):
        docker = FakeDocker()
        ticks = iter((0.0, 301.0))
        result, code = self.execute(docker, clock=lambda: next(ticks))
        self.assertEqual(1, code)
        self.assertEqual("total_timeout", result["errors"][0]["type"])
        self.assertFalse(docker.calls)

    def test_check_ownership_does_not_delete_or_claim_absence(self):
        docker = FakeDocker(populated=True)
        result, code = self.execute(docker, "check-ownership")
        self.assertEqual(0, code)
        self.assertTrue(result["ownershipVerified"])
        self.assertFalse(result["cleanupComplete"])
        self.assertFalse(docker.removals)

    def test_all_ownership_is_checked_before_first_mutation(self):
        docker = FakeDocker(populated=True)
        docker.volumes[PROJECT + "_caddy"] = "another-project"
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(1, code)
        self.assertEqual("ownership_mismatch", result["errors"][0]["type"])
        self.assertFalse(docker.removals)
        self.assertFalse(result["ownershipVerified"])

    def test_container_project_mismatch_is_rejected_before_mutation(self):
        docker = FakeDocker(populated=True)
        docker.containers[PROJECT + "_runtime_uid"] = ("b" * 64, "another-project", PROJECT)
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(1, code)
        self.assertEqual("ownership_mismatch", result["errors"][0]["type"])
        self.assertFalse(docker.removals)

    def test_compose_container_with_contradictory_qa_label_is_rejected(self):
        docker = FakeDocker(populated=True)
        docker.containers[PROJECT + "-web-1"] = ("a" * 64, PROJECT, "another-project")
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(1, code)
        self.assertEqual("ownership_mismatch", result["errors"][0]["type"])
        self.assertFalse(docker.removals)
        self.assertFalse(result["ownershipVerified"])

    def test_unowned_name_in_filtered_inventory_is_rejected(self):
        docker = FakeDocker(populated=True)
        docker.containers["foreign-container"] = ("c" * 64, PROJECT, "")
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(1, code)
        self.assertFalse(docker.removals)
        self.assertNotIn("foreign-container", json.dumps(result))

    def test_remove_owned_removes_only_exact_resources_and_verifies_absence(self):
        docker = FakeDocker(populated=True)
        docker.volumes["unrelated-volume"] = "unrelated-project"
        docker.networks["unrelated-network"] = "unrelated-project"
        docker.containers["unrelated-web-1"] = ("c" * 64, "unrelated-project", "")
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(0, code)
        self.assertTrue(result["cleanupComplete"])
        self.assertEqual(6, len(docker.removals))
        self.assertEqual({"unrelated-volume": "unrelated-project"}, docker.volumes)
        self.assertEqual({"unrelated-network": "unrelated-project"}, docker.networks)
        self.assertEqual(["unrelated-web-1"], list(docker.containers))
        self.assertTrue(all(name.startswith(PROJECT) for name in docker.removals))

    def test_final_read_failure_cannot_certify_cleanup(self):
        docker = FakeDocker(populated=True)
        def fail_final(arguments, options):
            if docker.removals and arguments[1] == "ps":
                return subprocess.CompletedProcess(arguments, 1, "", "Cannot connect to the Docker daemon")
            return None
        docker.fault = fail_final
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(1, code)
        self.assertTrue(result["cleanupUnknown"])
        self.assertFalse(result["cleanupComplete"])

    def test_known_remaining_resources_fail_absence_check(self):
        docker = FakeDocker(populated=True)
        result, code = self.execute(docker)
        self.assertEqual(1, code)
        self.assertFalse(result["cleanupUnknown"])
        self.assertTrue(all(error["type"] == "resource_remaining" for error in result["errors"]))

    def test_malformed_metadata_is_unknown(self):
        docker = FakeDocker(populated=True)
        docker.fault = lambda arguments, options: subprocess.CompletedProcess(arguments, 0, "not-json", "") if arguments[1] == "inspect" else None
        result, code = self.execute(docker, "remove-owned")
        self.assertEqual(1, code)
        self.assertEqual("metadata_invalid", result["errors"][0]["type"])
        self.assertFalse(docker.removals)

    def test_production_or_unsafe_project_is_rejected(self):
        for project in ("acropolis-channel", "acropolis_test_x;rm", "ACROPOLIS_TEST_x", "acropolis_test_"):
            with self.subTest(project=project), self.assertRaises(ValueError):
                cleanup.Cleanup(project)


if __name__ == "__main__":
    unittest.main()
