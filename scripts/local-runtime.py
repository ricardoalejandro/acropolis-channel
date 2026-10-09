#!/usr/bin/env python3
"""Run only the persistent WSL development environment; never deploy the VPS."""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import platform
import secrets
import socket
import ssl
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
STATE = ROOT / ".local" / "runtime"
ENV_FILE = STATE / ".env"
PROJECT = "acropolis-channel-local"
CONTEXT = "rootless"
NETWORK = "acropolis_channel_local_private"
PORTS = (17480, 17443, 17425)
VOLUMES = tuple("acropolis_channel_local_" + name for name in ("database", "keyring", "pki"))
IMAGES = {"LOCAL_WEB_IMAGE": "acropolis-channel-local:web", "LOCAL_MIGRATION_IMAGE": "acropolis-channel-local:migrations", "LOCAL_PKI_IMAGE": "acropolis-channel-local:pki"}
PASSWORD_KEYS = ("LOCAL_DB_ADMIN_PASSWORD", "LOCAL_DB_APP_PASSWORD", "LOCAL_DB_MIGRATION_PASSWORD", "LOCAL_SMTP_PASSWORD", "LOCAL_DP_PASSWORD")


class LocalProblem(Exception):
    pass


def command_env(values=None):
    env = {key: value for key, value in os.environ.items() if not key.startswith(("LOCAL_", "COMPOSE_")) and key not in ("DOCKER_HOST", "DOCKER_CONTEXT")}
    env.update(values or {})
    return env


def docker(*args, capture=False, values=None):
    result = subprocess.run(["docker", "--context", CONTEXT, *args], cwd=ROOT, env=command_env(values), text=True, capture_output=capture)
    if result.returncode:
        # Docker/Compose model output can contain passwords. Do not echo captured output.
        raise LocalProblem("Local Docker operation failed: " + " ".join(args[:2]))
    return result.stdout if capture else ""


def guard():
    if "microsoft" not in platform.release().lower() or os.getuid() == 0:
        raise LocalProblem("This command is for a non-root WSL user, never for the VPS.")
    context = json.loads(docker("context", "inspect", CONTEXT, capture=True))[0]
    endpoint = context["Endpoints"]["docker"]["Host"]
    if endpoint != f"unix:///run/user/{os.getuid()}/docker.sock":
        raise LocalProblem("The rootless context must use this WSL user's local Unix socket.")
    security = json.loads(docker("info", "--format", "{{json .SecurityOptions}}", capture=True))
    if "name=rootless" not in security:
        raise LocalProblem("Local runtime requires the rootless Docker daemon.")


def volume_names():
    return set(docker("volume", "ls", "--format", "{{.Name}}", capture=True).splitlines())


def verify_ownership():
    existing = volume_names()
    for name in VOLUMES:
        if name not in existing:
            continue
        metadata = json.loads(docker("volume", "inspect", name, capture=True))[0]
        labels = metadata.get("Labels") or {}
        if labels.get("acropolis.environment") != "local" or labels.get("com.docker.compose.project") != PROJECT:
            raise LocalProblem("Existing named volume is not owned by this local project: " + name)
    networks = set(docker("network", "ls", "--format", "{{.Name}}", capture=True).splitlines())
    for name in (NETWORK, "acropolis_channel_local_publication"):
        if name in networks:
            labels = json.loads(docker("network", "inspect", name, capture=True))[0].get("Labels") or {}
            if labels.get("acropolis.environment") != "local" or labels.get("com.docker.compose.project") != PROJECT:
                raise LocalProblem("Existing network is not owned by this local project: " + name)
    identifiers = docker("ps", "-aq", "--filter", "label=com.docker.compose.project=" + PROJECT, capture=True).splitlines()
    if identifiers:
        for item in json.loads(docker("inspect", *identifiers, capture=True)):
            labels = item["Config"].get("Labels") or {}
            if labels.get("acropolis.environment") != "local":
                raise LocalProblem("Existing container has no local ownership contract.")


def write_environment(values):
    STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    if (ROOT / ".local").is_symlink() or STATE.is_symlink() or ENV_FILE.is_symlink():
        raise LocalProblem("Local state must not be a symlink.")
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=STATE, prefix=".env-", delete=False) as stream:
        temporary = Path(stream.name)
        try:
            stream.write("# Private WSL development values. Never use this file on the VPS.\n")
            for key, value in values.items():
                stream.write(f"{key}={value}\n")
            stream.flush()
            os.fsync(stream.fileno())
        except BaseException:
            temporary.unlink(missing_ok=True)
            raise
    temporary.replace(ENV_FILE)


def environment(create=False):
    if (ROOT / ".local").is_symlink() or STATE.is_symlink() or ENV_FILE.is_symlink():
        raise LocalProblem("Local environment must not be a symlink.")
    if not ENV_FILE.exists():
        if not create:
            raise LocalProblem("Local environment is not initialized; run up first.")
        if set(VOLUMES) & volume_names():
            raise LocalProblem("Local volumes exist but their original environment is missing; recover it without replacing secrets.")
        values = {key: secrets.token_hex(24) for key in PASSWORD_KEYS}
        values.update(IMAGES)
        values.update({"LOCAL_PROXY_IP": "127.0.0.1", "LOCAL_APP_UID": "1654"})
        write_environment(values)
    if stat.S_IMODE(ENV_FILE.stat().st_mode) & 0o077:
        raise LocalProblem("Local environment must have mode 600.")
    values = {}
    for line in ENV_FILE.read_text().splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        key, separator, value = line.partition("=")
        if not separator or key in values:
            raise LocalProblem("Invalid local environment format.")
        values[key] = value
    expected = set(PASSWORD_KEYS) | set(IMAGES) | {"LOCAL_PROXY_IP", "LOCAL_APP_UID"}
    if set(values) != expected or any(values[key] != value for key, value in IMAGES.items()):
        raise LocalProblem("The private environment does not match the local-only contract.")
    for key in PASSWORD_KEYS:
        if len(values[key]) != 48 or any(character not in "0123456789abcdef" for character in values[key]):
            raise LocalProblem("Local password is not a generated independent value: " + key)
    socket.inet_aton(values["LOCAL_PROXY_IP"])
    if values["LOCAL_APP_UID"] != "1654":
        raise LocalProblem("Unexpected local application UID.")
    return values


def compose(values, *args, capture=False):
    return docker("compose", "--env-file", str(ENV_FILE), "-f", str(ROOT / "compose.local.yml"), "-p", PROJECT, *args, values=values, capture=capture)


def check_ports():
    ids = docker("ps", "-q", "--filter", "label=com.docker.compose.project=" + PROJECT, capture=True).splitlines()
    owned = set()
    if ids:
        for item in json.loads(docker("inspect", *ids, capture=True)):
            for bindings in (item["NetworkSettings"].get("Ports") or {}).values():
                for binding in bindings or []:
                    if binding["HostIp"] != "127.0.0.1":
                        raise LocalProblem("Local containers must publish only on loopback.")
                    owned.add(int(binding["HostPort"]))
    for port in PORTS:
        if port in owned:
            continue
        with socket.socket() as probe:
            try:
                probe.bind(("127.0.0.1", port))
            except OSError:
                raise LocalProblem(f"Fixed local port {port} is occupied; no alternative port will be selected.") from None


def validate_model(values):
    model = json.loads(compose(values, "--profile", "tools", "config", "--format", "json", capture=True))
    if set(model.get("services", {})) != {"db", "pki", "crl", "mailpit", "proxy", "migrations", "web"}:
        raise LocalProblem("The local service topology changed.")
    for section, expected_names in (("volumes", {"database": VOLUMES[0], "keyring": VOLUMES[1], "pki": VOLUMES[2]}), ("networks", {"private": NETWORK, "publication": "acropolis_channel_local_publication"})):
        if set(model.get(section, {})) != set(expected_names):
            raise LocalProblem("The local resource topology changed.")
        for key, name in expected_names.items():
            config = model[section][key]
            if config.get("external") or config.get("name") != name or config.get("labels", {}).get("acropolis.environment") != "local":
                raise LocalProblem("A resource is not confined to this local environment.")
    if model["networks"]["private"].get("internal") is not True:
        raise LocalProblem("The local database network must stay private.")
    allowed_binds = {
        "db": {(str(ROOT / "infra/postgres/init-roles.sh"), "/docker-entrypoint-initdb.d/10-roles.sh", True)},
        "pki": {(str(ROOT / "infra/local"), "/local-infra", True), (str(STATE / "export"), "/export", False)},
        "crl": {(str(ROOT / "infra/local/crl-server.mjs"), "/local-infra/crl-server.mjs", True)},
        "proxy": {(str(ROOT / "infra/local/Caddyfile"), "/etc/caddy/Caddyfile", True)},
    }
    for name, config in model["services"].items():
        if config.get("labels", {}).get("acropolis.environment") != "local" or config.get("privileged"):
            raise LocalProblem("Service ownership or privilege contract changed.")
        binds = set()
        for mount in config.get("volumes", []):
            if mount["type"] == "bind":
                source = Path(mount["source"])
                contract = (str(source), mount["target"], mount.get("read_only", False))
                if contract not in allowed_binds.get(name, set()):
                    raise LocalProblem("Unexpected bind mount in " + name)
                if source.is_symlink():
                    raise LocalProblem("Local runtime binds cannot be symlinks.")
                binds.add((str(source), mount["target"], mount.get("read_only", False)))
            elif mount["type"] != "volume" or mount["source"] not in model["volumes"]:
                raise LocalProblem("Unexpected local runtime mount.")
        if binds != allowed_binds.get(name, set()):
            raise LocalProblem("Unexpected bind mount in " + name)
    expected_images = {"web": values["LOCAL_WEB_IMAGE"], "migrations": values["LOCAL_MIGRATION_IMAGE"], "pki": values["LOCAL_PKI_IMAGE"], "crl": values["LOCAL_PKI_IMAGE"], "proxy": "caddy:2.11.6-alpine", "mailpit": "axllent/mailpit:v1.31.4", "db": "postgres:18-bookworm"}
    if any(model["services"][name].get("image") != image for name, image in expected_images.items()):
        raise LocalProblem("Unexpected image in the local runtime.")
    for service, role in (("web", "acropolis_app"), ("migrations", "acropolis_migrator")):
        env = model["services"][service]["environment"]
        expected_env = {"Identity__PublicOrigin": "https://localhost:17443", "Identity__Smtp__Host": "mailpit", "Identity__Smtp__Port": "465", "Identity__Smtp__Security": "ssl", "Identity__Smtp__Username": "local", "Identity__EmailEnabled": "true", "Catalog__Consumption__RecordingEnabled": "false", "Subscriptions__Notifications__Enabled": "false"}
        connection = env.get("ConnectionStrings__Database", "")
        if any(str(env.get(key)).lower() != value for key, value in expected_env.items()) or not connection.startswith(f"Host=db;Port=5432;Database=acropolis_local;Username={role};") or env.get("Identity__Smtp__Password") != values["LOCAL_SMTP_PASSWORD"] or env.get("Identity__DataProtection__CertificatePassword") != values["LOCAL_DP_PASSWORD"]:
            raise LocalProblem("The local identity, mail or database contract changed.")
    if model["services"]["web"]["environment"].get("ASPNETCORE_ENVIRONMENT") != "Production" or model["services"]["migrations"]["environment"].get("DOTNET_ENVIRONMENT") != "Production":
        raise LocalProblem("The local runtime must preserve application guardrails.")
    expected = {"proxy": {(17480, 8080), (17443, 8443)}, "mailpit": {(17425, 8025)}}
    for service, config in model["services"].items():
        actual = set()
        for port in config.get("ports", []):
            if port.get("host_ip") != "127.0.0.1":
                raise LocalProblem("A local port is not bound to loopback.")
            actual.add((int(port["published"]), int(port["target"])))
        if actual != expected.get(service, set()):
            raise LocalProblem("Unexpected published local ports for " + service)
    for config in model.get("networks", {}).values():
        if config.get("external"):
            raise LocalProblem("Local development cannot use external production networks.")


def request(path):
    context = ssl.create_default_context(cafile=str(STATE / "export" / "localhost.crt"))
    with urllib.request.urlopen("https://localhost:17443" + path, context=context, timeout=5) as response:
        return response.status, response.read()


def check(values):
    status, body = request("/health/ready")
    if status != 200:
        raise LocalProblem("Local application is not ready.")
    status, body = request("/api/v1/identity/capabilities")
    if status != 200 or json.loads(body).get("emailEnabled") is not True:
        raise LocalProblem("Local mail capability is not ready.")
    with urllib.request.urlopen("http://localhost:17425/api/v1/messages", timeout=5) as response:
        if response.status != 200:
            raise LocalProblem("Local mail UI is not ready.")
    result = {"status": "ready", "environment": "local", "application": "https://localhost:17443", "http": "http://localhost:17480", "mailbox": "http://localhost:17425", "deployment_eligible": False}
    STATE.mkdir(parents=True, exist_ok=True)
    (STATE / "status.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


def up(values, no_build=False):
    verify_ownership()
    check_ports()
    validate_model(values)
    if not no_build:
        revision = "local-" + subprocess.check_output(["git", "rev-parse", "--short=12", "HEAD"], cwd=ROOT, text=True).strip()
        for target, key in (("web", "LOCAL_WEB_IMAGE"), ("migrations", "LOCAL_MIGRATION_IMAGE"), ("qa-pki", "LOCAL_PKI_IMAGE")):
            docker("build", "--target", target, "--build-arg", "REVISION=" + revision, "--label", "acropolis.environment=local", "-t", values[key], ".")
    (STATE / "export").mkdir(parents=True, exist_ok=True)
    compose(values, "run", "--rm", "--no-deps", "pki")
    compose(values, "up", "-d", "--force-recreate", "--wait", "--wait-timeout", "120", "db", "crl", "mailpit", "proxy")
    proxy = compose(values, "ps", "-q", "proxy", capture=True).strip()
    metadata = json.loads(docker("inspect", proxy, capture=True))[0]
    values["LOCAL_PROXY_IP"] = metadata["NetworkSettings"]["Networks"][NETWORK]["IPAddress"]
    write_environment(values)
    compose(values, "run", "--rm", "--no-deps", "migrations")
    compose(values, "run", "--rm", "--no-deps", "migrations", "smtp-check")
    compose(values, "up", "-d", "--wait", "--wait-timeout", "120", "web")
    deadline = time.monotonic() + 60
    while True:
        try:
            check(values)
            break
        except (LocalProblem, OSError, urllib.error.URLError):
            if time.monotonic() >= deadline:
                raise LocalProblem("Local readiness failed; inspect local logs.") from None
            time.sleep(1)


@contextmanager
def mutation_lock():
    import fcntl
    if (ROOT / ".local").is_symlink() or STATE.is_symlink():
        raise LocalProblem("Local state must not be a symlink.")
    STATE.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor = os.open(STATE / ".lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise LocalProblem("Another local startup or stop is already running.") from None
        yield
    finally:
        os.close(descriptor)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("up", "stop", "status", "check", "logs"))
    parser.add_argument("--no-build", action="store_true", help="Reuse already built local images.")
    args = parser.parse_args(argv)
    guard()
    if args.command in ("up", "stop"):
        with mutation_lock():
            values = environment(create=args.command == "up")
            if args.command == "up":
                up(values, args.no_build)
            else:
                verify_ownership()
                compose(values, "stop")
        return 0
    values = environment()
    if args.command == "status":
        compose(values, "ps")
    elif args.command == "check":
        validate_model(values)
        verify_ownership()
        check(values)
    else:
        output = compose(values, "logs", "--no-color", "--tail", "100", capture=True)
        for key in PASSWORD_KEYS:
            output = output.replace(values[key], "[redacted]")
        import re
        output = re.sub(r"(?i)(token=|token%3D|password=|Cookie:|Set-Cookie:)\S+", r"\1[redacted]", output)
        print(output)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (LocalProblem, OSError, ValueError, KeyError, urllib.error.URLError) as error:
        # Exception messages from transport parsers can contain values; reveal only our own messages.
        print(str(error) if isinstance(error, LocalProblem) else "Local runtime operation failed; inspect private state and logs.", file=sys.stderr)
        raise SystemExit(1)
