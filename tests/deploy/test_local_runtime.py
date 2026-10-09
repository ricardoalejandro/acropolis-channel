import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("acropolis_local_runtime", ROOT / "scripts" / "local-runtime.py")
RUNTIME = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNTIME)


def private_values():
    values = {key: f"{index + 1:048x}" for index, key in enumerate(RUNTIME.PASSWORD_KEYS)}
    return values | RUNTIME.IMAGES | {"LOCAL_PROXY_IP": "127.0.0.1", "LOCAL_APP_UID": "1654"}


def local_model():
    values = private_values()
    services = {name: {"ports": [], "labels": {"acropolis.environment": "local"}, "volumes": []}
                for name in ("db", "pki", "crl", "mailpit", "proxy", "migrations", "web")}
    binds = {
        "db": [(ROOT / "infra/postgres/init-roles.sh", "/docker-entrypoint-initdb.d/10-roles.sh", True)],
        "pki": [(ROOT / "infra/local", "/local-infra", True), (RUNTIME.STATE / "export", "/export", False)],
        "crl": [(ROOT / "infra/local/crl-server.mjs", "/local-infra/crl-server.mjs", True)],
        "proxy": [(ROOT / "infra/local/Caddyfile", "/etc/caddy/Caddyfile", True)],
    }
    for service, mounts in binds.items():
        services[service]["volumes"] = [{"type": "bind", "source": str(source), "target": target, "read_only": read_only}
                                        for source, target, read_only in mounts]
    identity = {
        "Identity__PublicOrigin": "https://localhost:17443", "Identity__Smtp__Host": "mailpit",
        "Identity__Smtp__Port": "465", "Identity__Smtp__Security": "ssl", "Identity__EmailEnabled": "true",
        "Catalog__Consumption__RecordingEnabled": "false", "Subscriptions__Notifications__Enabled": "false",
        "Identity__Smtp__Username": "local", "Identity__Smtp__Password": values["LOCAL_SMTP_PASSWORD"],
        "Identity__DataProtection__CertificatePassword": values["LOCAL_DP_PASSWORD"],
        "Identity__KnownProxies": values["LOCAL_PROXY_IP"],
    }
    for service, role in (("web", "acropolis_app"), ("migrations", "acropolis_migrator")):
        password_key = "LOCAL_DB_APP_PASSWORD" if service == "web" else "LOCAL_DB_MIGRATION_PASSWORD"
        image_key = "LOCAL_WEB_IMAGE" if service == "web" else "LOCAL_MIGRATION_IMAGE"
        services[service]["image"] = values[image_key]
        services[service]["environment"] = identity | {
            "ConnectionStrings__Database": f"Host=db;Port=5432;Database=acropolis_local;Username={role};Password=" + values[password_key],
            "ASPNETCORE_ENVIRONMENT" if service == "web" else "DOTNET_ENVIRONMENT": "Production",
        }
    services["pki"]["image"] = values["LOCAL_PKI_IMAGE"]
    services["crl"]["image"] = values["LOCAL_PKI_IMAGE"]
    services["db"]["image"] = "postgres:18-bookworm"
    services["proxy"]["image"] = "caddy:2.11.6-alpine"
    services["mailpit"]["image"] = "axllent/mailpit:v1.31.4"
    services["proxy"]["ports"] = [
        {"host_ip": "127.0.0.1", "published": "17480", "target": 8080},
        {"host_ip": "127.0.0.1", "published": "17443", "target": 8443},
    ]
    services["mailpit"]["ports"] = [{"host_ip": "127.0.0.1", "published": "17425", "target": 8025}]
    return {
        "name": RUNTIME.PROJECT,
        "services": services,
        "networks": {
            "private": {"name": "acropolis_channel_local_private", "internal": True, "labels": {"acropolis.environment": "local"}},
            "publication": {"name": "acropolis_channel_local_publication", "labels": {"acropolis.environment": "local"}},
        },
        "volumes": {name: {"name": "acropolis_channel_local_" + name, "labels": {"acropolis.environment": "local"}}
                    for name in ("database", "keyring", "pki")},
    }


class LocalDaemonGuardTests(unittest.TestCase):
    def guard_with(self, release="6.6.87.2-microsoft-standard-WSL2", uid=1000, endpoint=None, security=None):
        endpoint = endpoint or f"unix:///run/user/{uid}/docker.sock"
        context = json.dumps([{"Endpoints": {"docker": {"Host": endpoint}}}])
        security = security if security is not None else ["name=rootless", "name=seccomp,profile=builtin"]
        with patch.object(RUNTIME.platform, "release", return_value=release), \
                patch.object(RUNTIME.os, "getuid", return_value=uid, create=True), \
                patch.object(RUNTIME, "docker", side_effect=[context, json.dumps(security)]) as run:
            RUNTIME.guard()
            return run.call_args_list

    def test_guard_accepts_only_wsl_nonroot_user_socket_and_rootless_daemon(self):
        calls = self.guard_with()
        self.assertEqual(calls[0].args[:3], ("context", "inspect", "rootless"))
        self.assertEqual(calls[1].args[0], "info")

    def test_vps_or_root_is_rejected_before_any_docker_operation(self):
        for release, uid in [("6.8.0-generic", 1000), ("6.6-microsoft-standard-WSL2", 0)]:
            with self.subTest(release=release, uid=uid), \
                    patch.object(RUNTIME.platform, "release", return_value=release), \
                    patch.object(RUNTIME.os, "getuid", return_value=uid, create=True), \
                    patch.object(RUNTIME, "docker") as run:
                with self.assertRaises(RUNTIME.LocalProblem):
                    RUNTIME.guard()
                run.assert_not_called()

    def test_remote_or_another_users_socket_cannot_be_used(self):
        for endpoint in ("tcp://192.0.2.10:2375", "ssh://vps", "unix:///var/run/docker.sock", "unix:///run/user/1001/docker.sock"):
            with self.subTest(endpoint=endpoint), self.assertRaisesRegex(RUNTIME.LocalProblem, "Unix socket"):
                self.guard_with(endpoint=endpoint)

    def test_rootful_daemon_cannot_impersonate_rootless_context(self):
        with self.assertRaisesRegex(RUNTIME.LocalProblem, "rootless Docker"):
            self.guard_with(security=["name=seccomp,profile=builtin"])

    def test_ambient_compose_and_docker_overrides_cannot_change_the_target(self):
        ambient = {
            "PATH": "/usr/bin", "LOCAL_WEB_IMAGE": "production:web", "COMPOSE_FILE": "compose.yml",
            "COMPOSE_PROJECT_NAME": "acropolis-channel", "DOCKER_HOST": "ssh://vps", "DOCKER_CONTEXT": "remote",
        }
        with patch.dict(RUNTIME.os.environ, ambient, clear=True):
            effective = RUNTIME.command_env(private_values())
        self.assertEqual(effective["LOCAL_WEB_IMAGE"], RUNTIME.IMAGES["LOCAL_WEB_IMAGE"])
        self.assertEqual(effective["PATH"], "/usr/bin")
        for key in ("COMPOSE_FILE", "COMPOSE_PROJECT_NAME", "DOCKER_HOST", "DOCKER_CONTEXT"):
            self.assertNotIn(key, effective)


class LocalPrivateStateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.state = Path(self.temporary.name) / "runtime"
        self.environment_file = self.state / ".env"
        self.state_patch = patch.object(RUNTIME, "STATE", self.state)
        self.env_patch = patch.object(RUNTIME, "ENV_FILE", self.environment_file)
        self.state_patch.start()
        self.env_patch.start()
        self.addCleanup(self.state_patch.stop)
        self.addCleanup(self.env_patch.stop)

    def test_missing_environment_with_persisted_volume_requires_recovery(self):
        for volume in RUNTIME.VOLUMES:
            with self.subTest(volume=volume), patch.object(RUNTIME, "volume_names", return_value={volume}), \
                    patch.object(RUNTIME.secrets, "token_hex") as generate:
                with self.assertRaisesRegex(RUNTIME.LocalProblem, "original environment is missing"):
                    RUNTIME.environment(create=True)
                generate.assert_not_called()
                self.assertFalse(self.environment_file.exists())

    def test_existing_environment_is_reused_and_proxy_update_preserves_secrets(self):
        original = private_values()
        RUNTIME.write_environment(original)
        with patch.object(RUNTIME, "volume_names", side_effect=AssertionError("Existing state must not initialize resources")), \
                patch.object(RUNTIME.secrets, "token_hex", side_effect=AssertionError("Existing passwords must survive")):
            recovered = RUNTIME.environment(create=True)
        self.assertEqual(recovered, original)
        recovered["LOCAL_PROXY_IP"] = "172.29.0.10"
        RUNTIME.write_environment(recovered)
        reread = RUNTIME.environment()
        for key in RUNTIME.PASSWORD_KEYS:
            self.assertEqual(reread[key], original[key])
        self.assertEqual(reread["LOCAL_PROXY_IP"], "172.29.0.10")

    def test_production_configuration_or_image_is_rejected_without_rewrite(self):
        for addition in ({"IDENTITY_SMTP_HOST": "mail.naperu.cloud"}, {"LOCAL_WEB_IMAGE": "acropolis-channel:production"}):
            with self.subTest(addition=addition):
                RUNTIME.write_environment(private_values() | addition)
                before = self.environment_file.read_bytes()
                with self.assertRaisesRegex(RUNTIME.LocalProblem, "local-only contract"):
                    RUNTIME.environment()
                self.assertEqual(self.environment_file.read_bytes(), before)

    @unittest.skipUnless(os.name == "posix", "Linux privacy permissions are part of the WSL runtime")
    def test_group_or_public_readable_environment_is_rejected(self):
        RUNTIME.write_environment(private_values())
        self.environment_file.chmod(0o640)
        with self.assertRaisesRegex(RUNTIME.LocalProblem, "mode 600"):
            RUNTIME.environment()

    @unittest.skipUnless(os.name == "posix", "Linux symlink guard is validated in WSL")
    def test_environment_symlink_is_rejected_without_touching_its_target(self):
        self.state.mkdir()
        target = Path(self.temporary.name) / "original.env"
        target.write_text("preserve this private state")
        self.environment_file.symlink_to(target)
        with self.assertRaisesRegex(RUNTIME.LocalProblem, "symlink"):
            RUNTIME.environment(create=True)
        self.assertEqual(target.read_text(), "preserve this private state")

    @unittest.skipUnless(os.name == "posix", "Linux symlink guard is validated in WSL")
    def test_symlink_state_directory_is_rejected_on_read(self):
        actual = Path(self.temporary.name) / "real-state"
        actual.mkdir(mode=0o700)
        target = actual / ".env"
        target.write_text("".join(f"{key}={value}\n" for key, value in private_values().items()))
        target.chmod(0o600)
        self.state.symlink_to(actual, target_is_directory=True)
        with self.assertRaisesRegex(RUNTIME.LocalProblem, "symlink"):
            RUNTIME.environment()

    @unittest.skipUnless(os.name == "posix", "WSL orchestration uses a real Linux file lock")
    def test_competing_start_or_stop_is_rejected_before_secret_initialization(self):
        with RUNTIME.mutation_lock():
            for command in ("up", "stop"):
                with self.subTest(command=command), patch.object(RUNTIME, "guard"), \
                        patch.object(RUNTIME, "environment") as load, \
                        self.assertRaisesRegex(RUNTIME.LocalProblem, "already running"):
                    RUNTIME.main([command])
                load.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "WSL orchestration uses a real Linux file lock")
    def test_lock_is_released_after_failure_and_its_inode_is_preserved(self):
        with self.assertRaisesRegex(RuntimeError, "synthetic startup failure"):
            with RUNTIME.mutation_lock():
                inode = (self.state / ".lock").stat().st_ino
                raise RuntimeError("synthetic startup failure")
        with RUNTIME.mutation_lock():
            self.assertEqual((self.state / ".lock").stat().st_ino, inode)


class LocalPublicationContractTests(unittest.TestCase):
    def validate(self, model):
        with patch.object(RUNTIME, "compose", return_value=json.dumps(model)):
            RUNTIME.validate_model(private_values())

    def test_exact_fixed_loopback_publications_are_accepted(self):
        self.validate(local_model())

    def test_public_bind_or_port_change_or_database_publication_is_rejected(self):
        variants = []
        public = local_model()
        public["services"]["proxy"]["ports"][0]["host_ip"] = "0.0.0.0"
        variants.append(public)
        alternate = local_model()
        alternate["services"]["proxy"]["ports"][0]["published"] = "17481"
        variants.append(alternate)
        database = local_model()
        database["services"]["db"]["ports"] = [{"host_ip": "127.0.0.1", "published": "17432", "target": 5432}]
        variants.append(database)
        for index, model in enumerate(variants):
            with self.subTest(index=index), self.assertRaises(RUNTIME.LocalProblem):
                self.validate(model)

    def test_external_network_cannot_join_a_production_network(self):
        model = local_model()
        model["networks"]["private"] = {"name": "dokploy-network", "external": True}
        with self.assertRaises(RUNTIME.LocalProblem):
            self.validate(model)

    def test_missing_application_or_front_door_is_not_a_valid_local_runtime(self):
        for service in ("db", "web", "proxy", "mailpit"):
            with self.subTest(service=service):
                model = local_model()
                del model["services"][service]
                with self.assertRaises(RUNTIME.LocalProblem):
                    self.validate(model)

    def test_external_persistent_volume_cannot_mount_production_data(self):
        model = local_model()
        model["volumes"]["database"] = {"name": "acropolis-channel_database", "external": True}
        with self.assertRaises(RUNTIME.LocalProblem):
            self.validate(model)

    def test_disabled_authentication_guard_or_real_smtp_endpoint_is_rejected(self):
        variants = [
            ("web", "ASPNETCORE_ENVIRONMENT", "Development"),
            ("web", "Identity__PublicOrigin", "https://acropolischannel.naperu.cloud"),
            ("web", "Identity__Smtp__Host", "mail.naperu.cloud"),
            ("migrations", "Identity__Smtp__Security", "none"),
            ("web", "ConnectionStrings__Database", "Host=db;Port=5432;Database=acropolis_local;Username=acropolis_admin;Password=synthetic"),
            ("web", "ConnectionStrings__Database", "Host=production.example;Port=5432;Database=acropolis_local;Username=acropolis_app;Password=synthetic"),
            ("migrations", "Identity__Smtp__Password", "unrelated-production-secret"),
        ]
        for service, setting, value in variants:
            with self.subTest(service=service, setting=setting):
                model = local_model()
                model["services"][service]["environment"][setting] = value
                with self.assertRaises(RUNTIME.LocalProblem):
                    self.validate(model)

    def test_production_or_unexpected_image_cannot_be_started_locally(self):
        for service in ("web", "migrations", "pki", "db"):
            with self.subTest(service=service):
                model = local_model()
                model["services"][service]["image"] = "acropolis-channel:production"
                with self.assertRaisesRegex(RUNTIME.LocalProblem, "Unexpected image"):
                    self.validate(model)

    def test_added_host_mount_cannot_access_production_secrets(self):
        model = local_model()
        model["services"]["web"]["volumes"].append({
            "type": "bind", "source": "/root/proyect/acropolis-channel/.env", "target": "/production.env", "read_only": True,
        })
        with self.assertRaises(RUNTIME.LocalProblem):
            self.validate(model)

    def test_fixed_port_collision_fails_without_selecting_an_alternative(self):
        probe = MagicMock()
        probe.__enter__.return_value.bind.side_effect = OSError("occupied")
        with patch.object(RUNTIME, "docker", return_value=""), \
                patch.object(RUNTIME.socket, "socket", return_value=probe), \
                self.assertRaisesRegex(RUNTIME.LocalProblem, "17480 is occupied"):
            RUNTIME.check_ports()
        probe.__enter__.return_value.bind.assert_called_once_with(("127.0.0.1", 17480))

    def test_existing_owned_port_with_public_binding_is_rejected(self):
        container = {"NetworkSettings": {"Ports": {"8443/tcp": [{"HostIp": "0.0.0.0", "HostPort": "17443"}]}}}
        with patch.object(RUNTIME, "docker", side_effect=["local-id\n", json.dumps([container])]), \
                self.assertRaisesRegex(RUNTIME.LocalProblem, "only on loopback"):
            RUNTIME.check_ports()

    def test_unrelated_persistent_volume_cannot_be_reconciled(self):
        metadata = [{"Labels": {"com.docker.compose.project": "another-project", "acropolis.environment": "local"}}]
        with patch.object(RUNTIME, "volume_names", return_value={RUNTIME.VOLUMES[0]}), \
                patch.object(RUNTIME, "docker", return_value=json.dumps(metadata)), \
                self.assertRaisesRegex(RUNTIME.LocalProblem, "not owned"):
            RUNTIME.verify_ownership()

    def test_unrelated_named_network_is_rejected_before_any_container_reconcile(self):
        metadata = [{"Labels": {"acropolis.environment": "local", "com.docker.compose.project": "another-project"}}]
        with patch.object(RUNTIME, "volume_names", return_value=set()), \
                patch.object(RUNTIME, "docker", side_effect=[RUNTIME.NETWORK + "\n", json.dumps(metadata)]) as run, \
                self.assertRaisesRegex(RUNTIME.LocalProblem, "network is not owned"):
            RUNTIME.verify_ownership()
        self.assertEqual([call.args[0] for call in run.call_args_list], ["network", "network"])


if __name__ == "__main__":
    unittest.main()
