"""Behavioral deployment tests: only temporary files and mocked external boundaries."""
from contextlib import ExitStack, redirect_stdout
import datetime
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import call, patch

SOURCE_ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("acropolis_deployment_under_test", SOURCE_ROOT / "scripts/deploy.py")
DEPLOY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DEPLOY)
REAL_DATETIME = datetime.datetime
SHA = "a" * 40
WEB_REF = "acropolis-channel:" + SHA
MIGRATION_REF = "acropolis-channel-migrations:" + SHA
WEB_ID = "sha256:qa-approved-web"
MIGRATION_ID = "sha256:qa-approved-migrations"
OLD_ID = "sha256:previous-web"
STOP_CONTAINER_ID = "d" * 64


class Clock(REAL_DATETIME):
    ticks = 0

    @classmethod
    def now(cls, tz=None):
        value = REAL_DATETIME(2026, 10, 4, 12, 0, 0, tzinfo=tz) + datetime.timedelta(seconds=cls.ticks)
        cls.ticks += 1
        return value


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.initial_cwd = Path.cwd()
        self.temporary = tempfile.TemporaryDirectory(prefix="acropolis-deployment-test-")
        self.root = Path(self.temporary.name) / "checkout"
        self.root.mkdir()
        self.route = Path(self.temporary.name) / "dynamic/acropolis-channel.yml"
        self.route.parent.mkdir()
        self.local = self.root / ".local"
        self.local.mkdir()
        self.environment = (
            "# Preserve unrelated settings and quoted values\n"
            "PUBLIC_VPS_IPV4=192.0.2.46\n"
            "TRAEFIK_NETWORK=dokploy-network\n"
            "UNCHANGED_SETTING='a value with spaces = retained'\n"
        )
        (self.root / ".env").write_text(self.environment)
        (self.root / ".env").chmod(0o600)
        self.compose_text = "services:\n  web:\n    image: candidate\n  db:\n    image: postgres:18-bookworm\n"
        (self.root / "compose.yml").write_text(self.compose_text)
        self.template = self.root / "infra/traefik/acropolis-channel.yml"
        self.template.parent.mkdir(parents=True)
        self.template.write_text(DEPLOY.yaml.safe_dump({
            "http": {
                "routers": {"acropolis-channel-https": {
                    "rule": "Host(`" + DEPLOY.DOMAIN + "`)",
                    "service": "acropolis-channel-web",
                }},
                "services": {"acropolis-channel-web": {
                    "loadBalancer": {"servers": [{"url": "http://acropolis-channel-web:8080"}]}
                }},
            }
        }))
        self.report_path = self.local / "qa" / SHA / "fixture/report.json"
        self.report_path.parent.mkdir(parents=True)
        self.report = {
            "sha": SHA, "status": "passed", "deployment_eligible": True,
            "image_id": WEB_ID, "migration_image_id": MIGRATION_ID,
        }
        self.write_report()
        self.dirty = ""
        self.branch = "main"
        self.origin = "https://github.com/ricardoalejandro/acropolis-channel.git"
        self.head = SHA
        self.remote_sha = SHA
        self.running_image = None
        self.web_container_exists = False
        self.images = {WEB_REF: WEB_ID, MIGRATION_REF: MIGRATION_ID}
        self.operations = []
        Clock.ticks = 0
        self.stack = ExitStack()
        self.stack.enter_context(patch.object(DEPLOY, "ROOT", self.root))
        self.stack.enter_context(patch.object(DEPLOY, "ROUTE", self.route))
        self.stack.enter_context(patch.object(DEPLOY.datetime, "datetime", Clock))
        self.stack.enter_context(patch.dict(os.environ, {}, clear=True))
        self.stack.enter_context(patch.object(DEPLOY.subprocess, "run", side_effect=AssertionError("Real subprocess forbidden")))
        self.stack.enter_context(patch.object(DEPLOY.urllib.request, "urlopen", side_effect=AssertionError("Real network forbidden")))
        self.command = self.stack.enter_context(patch.object(DEPLOY, "command", side_effect=self.fake_command))
        self.compose = self.stack.enter_context(patch.object(DEPLOY, "compose", side_effect=self.fake_compose))
        self.image_id = self.stack.enter_context(patch.object(DEPLOY, "image_id", side_effect=self.fake_image_id))
        self.original_ready = DEPLOY.ready
        self.ready = self.stack.enter_context(patch.object(DEPLOY, "ready", return_value=True))
        self.dns = self.stack.enter_context(patch.object(DEPLOY, "dns_ok", return_value=True))
        self.smoke = self.stack.enter_context(patch.object(DEPLOY, "public_smoke"))
        self.sleep = self.stack.enter_context(patch.object(DEPLOY.time, "sleep"))
        self.stdout = io.StringIO()

    def tearDown(self):
        os.chdir(self.initial_cwd)
        self.stack.close()
        self.temporary.cleanup()

    def write_report(self):
        self.report_path.write_text(json.dumps(self.report))

    def fake_image_id(self, reference):
        if reference not in self.images:
            raise subprocess.CalledProcessError(1, ["simulated", "image", "inspect"])
        return self.images[reference]

    def fake_command(self, args, capture=False, env=None, check=True, timeout=None):
        self.operations.append(("command", tuple(args)))
        if args[:3] == ["git", "status", "--porcelain"]:
            return self.dirty
        if args[:3] == ["git", "branch", "--show-current"]:
            return self.branch
        if args[:4] == ["git", "remote", "get-url", "origin"]:
            return self.origin
        if args[:2] == ["git", "rev-parse"]:
            return self.head if args[2] == "HEAD" else self.remote_sha
        if args in (["git", "fetch", "origin"], ["git", "pull", "--ff-only", "origin", "main"]):
            return 0
        if args[:2] == ["docker", "inspect"]:
            if args[-1] == "{{.State.Running}}":
                self.assertEqual(args[2], STOP_CONTAINER_ID)
                self.assertEqual(timeout, 30)
                return "true" if self.running_image else "false"
            self.assertEqual(args[2], "simulated-web-container")
            if args[-1] == "{{.Image}}|{{.State.Running}}":
                return str(self.running_image) + "|true"
            return self.running_image
        if args[:2] == ["docker", "tag"]:
            return 0
        if args == ["python3", "scripts/identity-runtime.py", "--check"]:
            return 0
        smtp_network = ["python3", str(self.root / "scripts/smtp-network.py")]
        if args in (smtp_network, smtp_network + ["--allow-empty"]):
            self.assertTrue(capture, "Topology helper output must stay private")
            return "{}"
        if args[:2] == ["bash", "scripts/backup-db.sh"]:
            output = Path(args[args.index("--output") + 1])
            self.assertTrue(output.is_relative_to(self.local / "backups"))
            self.assertEqual(args[args.index("--database") + 1], "acropolis")
            output.parent.mkdir(exist_ok=True)
            output.write_bytes(b"only a temporary mocked backup")
            return 0
        if args[:2] == ["docker", "compose"]:
            self.assertEqual(args[args.index("-p") + 1], "acropolis-channel")
            self.assertEqual(args[-1], "web")
            self.assertNotIn("db", args)
            self.assertEqual(timeout, 30)
            if args[-3:] == ["ps", "-q", "web"]:
                return "simulated-web-container" if self.running_image else ""
            if args[-4:] == ["ps", "-a", "-q", "web"]:
                return STOP_CONTAINER_ID if self.web_container_exists else ""
            if args[-2:] == ["stop", "web"]:
                self.running_image = None
                return ""
            self.assertIn("--no-deps", args)
            override_path = Path(args[args.index("-f", args.index("-f") + 1) + 1])
            override = DEPLOY.yaml.safe_load(override_path.read_text())
            self.running_image = override["services"]["web"]["image"]
            self.web_container_exists = True
            return ""
        raise AssertionError("Unexpected mocked command: " + repr(args))

    def fake_compose(self, *args, env=None, capture=False, check=True):
        self.operations.append(("compose", args))
        if args == ("ps", "-q", "web"):
            return "simulated-web-container" if self.running_image else ""
        if args == ("--profile", "migration", "run", "--rm", "--no-deps", "migrations", "smtp-check"):
            return 0
        if args == ("config", "--quiet"):
            return 0
        if args == ("up", "-d", "--no-build", "--wait", "--wait-timeout", "90", "db"):
            return 0
        if args == ("--profile", "migration", "run", "--rm", "migrations"):
            self.assertEqual(env["MIGRATION_IMAGE"], MIGRATION_ID if self.report.get("scope") == "frontend-low-risk" else MIGRATION_REF)
            return 0
        if args == ("up", "-d", "--no-build", "web"):
            self.assertEqual(env["APP_IMAGE"], WEB_REF)
            self.running_image = self.images[env["APP_IMAGE"]]
            self.web_container_exists = True
            return 0
        if args == ("stop", "web"):
            self.running_image = None
            return 0
        raise AssertionError("Unexpected mocked compose operation: " + repr(args))

    def execute(self, expected_sha=SHA):
        with patch.object(DEPLOY.sys, "argv", ["deploy.py", "--expected-sha", expected_sha]), redirect_stdout(self.stdout):
            return DEPLOY.main()

    def assert_not_activated(self):
        self.assertEqual([args for kind, args in self.operations if kind == "compose" and args != ("ps", "-q", "web")], [])
        self.assertFalse(any(args[:2] == ("bash", "scripts/backup-db.sh") for kind, args in self.operations if kind == "command"))
        self.ready.assert_not_called()
        self.smoke.assert_not_called()

    def seed_previous_deployment(self):
        prior = self.local / "deployments/previous"
        prior.mkdir(parents=True)
        previous_env = self.environment + "APP_IMAGE=acropolis-channel:previous\nMIGRATION_IMAGE=acropolis-channel-migrations:previous\n"
        previous_compose = "services:\n  web:\n    image: previous\n    environment:\n      ASPNETCORE_HTTP_PORTS: '8080'\n"
        (prior / "manifest.json").write_text(json.dumps({"image_id": OLD_ID, "status": "published"}))
        (prior / "active.env").write_text(previous_env)
        (prior / "active-compose.yml").write_text(previous_compose)
        (self.root / ".env").write_text(previous_env)
        self.previous_route = "http:\n  routers:\n    previous-acropolis: {}\n"
        self.route.write_text(self.previous_route)
        (self.local / "last-deployment").write_text(str(prior) + "\n")
        (self.local / "last-active-deployment").write_text(str(prior) + "\n")
        self.running_image = OLD_ID
        self.web_container_exists = True
        return prior, previous_env, previous_compose

    def deployment_manifest(self):
        directories = [p for p in (self.local / "deployments").iterdir() if p.name != "previous"]
        newest = max(directories, key=lambda p: p.name)
        return newest, json.loads((newest / "manifest.json").read_text())

    def test_scoped_certificate_is_validated_before_mutation_and_uses_original_runner(self):
        self.report.update(scope="frontend-low-risk", inherited_backend={"sha": "b" * 40},
                           migration_source_sha="b" * 40, scope_proof_sha256="c" * 64)
        self.write_report()
        self.images.pop(MIGRATION_REF)
        self.images[MIGRATION_ID] = MIGRATION_ID
        def validate(report, sha, web, migration):
            self.assertEqual((report, sha, web, migration), (self.report, SHA, WEB_ID, MIGRATION_ID))
            self.assert_not_activated()
            self.assertFalse(any(args == ("python3", "scripts/identity-runtime.py", "--check")
                                 for kind, args in self.operations if kind == "command"))
            self.assertFalse((self.local / "deployments").exists())
            return {"validated": True}
        with patch.object(DEPLOY, "validate_frontend_certificate", side_effect=validate) as guard:
            self.assertEqual(self.execute(), 0)
        guard.assert_called_once()
        _, manifest = self.deployment_manifest()
        self.assertEqual(manifest["qa_scope"], "frontend-low-risk")
        self.assertEqual(manifest["backend_base_sha"], "b" * 40)
        self.assertEqual(manifest["migration_source_sha"], "b" * 40)
        self.assertEqual(DEPLOY.env_values()["MIGRATION_IMAGE"], MIGRATION_ID)

    def test_invalid_scoped_certificate_blocks_all_runtime_changes(self):
        self.report.update(scope="frontend-low-risk")
        self.write_report()
        self.images[MIGRATION_ID] = MIGRATION_ID
        with patch.object(DEPLOY, "validate_frontend_certificate", side_effect=ValueError("Invalid scope proof")):
            with self.assertRaisesRegex(ValueError, "Invalid scope proof"):
                self.execute()
        self.assert_not_activated()
        self.assertFalse((self.local / "deployments").exists())
        self.assertEqual((self.root / ".env").read_text(), self.environment)

    def test_unknown_or_preflight_scope_cannot_certify_a_release(self):
        for scope in ("preflight", "module-unimplemented", "editor-only"):
            with self.subTest(scope=scope):
                self.report["scope"] = scope
                self.write_report()
                with self.assertRaisesRegex(RuntimeError, "No matching passed QA"):
                    self.execute()
                self.assert_not_activated()

    def test_full_gate_and_legacy_same_sha_certificates_keep_existing_deployment(self):
        for scope in ("full", "same-sha-tail"):
            with self.subTest(scope=scope):
                self.report["scope"] = scope
                self.write_report()
                with patch.object(DEPLOY, "validate_frontend_certificate") as guard:
                    self.assertEqual(self.execute(), 0)
                guard.assert_not_called()

    def test_identity_configuration_failure_prevents_database_or_application_change(self):
        original = self.command.side_effect

        def invalid_configuration(args, **kwargs):
            if args == ["python3", "scripts/identity-runtime.py", "--check"]:
                raise subprocess.CalledProcessError(1, args)
            return original(args, **kwargs)

        self.command.side_effect = invalid_configuration
        with self.assertRaises(subprocess.CalledProcessError):
            self.execute()
        self.assert_not_activated()

    def smtp_network_calls(self):
        expected = ("python3", str(self.root / "scripts/smtp-network.py"))
        return [args for kind, args in self.operations if kind == "command" and args[:len(expected)] == expected]

    def assert_network_verified_before_mutation(self):
        calls = self.smtp_network_calls()
        self.assertEqual(len(calls), 1)
        guard_index = self.operations.index(("command", calls[0]))
        mutations = [index for index, (kind, args) in enumerate(self.operations)
                     if (kind == "compose" and args[0] in ("up", "--profile", "stop"))
                     or (kind == "command" and args[:2] == ("bash", "scripts/backup-db.sh"))]
        self.assertTrue(mutations, "Fixture must exercise a real deployment sequence")
        self.assertTrue(all(index > guard_index for index in mutations))

    def test_owned_smtp_requires_attached_mailserver_before_backup_runner_and_web(self):
        with (self.root / ".env").open("a") as handle:
            handle.write("IDENTITY_EMAIL_ENABLED=true\nIDENTITY_SMTP_HOST=mail.naperu.cloud\n")
        self.assertEqual(self.execute(), 0)
        self.assertEqual(self.smtp_network_calls(), [
            ("python3", str(self.root / "scripts/smtp-network.py"))])
        self.assert_network_verified_before_mutation()
        self.assertTrue(any(kind == "compose" and "smtp-check" in args for kind, args in self.operations))

    def test_disabled_owned_smtp_still_validates_network_and_allows_no_mailserver(self):
        with (self.root / ".env").open("a") as handle:
            handle.write("IDENTITY_EMAIL_ENABLED=false\nIDENTITY_SMTP_HOST=mail.naperu.cloud\n")
        self.assertEqual(self.execute(), 0)
        self.assertEqual(self.smtp_network_calls(), [
            ("python3", str(self.root / "scripts/smtp-network.py"), "--allow-empty")])
        self.assert_network_verified_before_mutation()
        self.assertFalse(any(kind == "compose" and "smtp-check" in args for kind, args in self.operations))

    def test_external_smtp_still_validates_attached_network_before_activation(self):
        with (self.root / ".env").open("a") as handle:
            handle.write("IDENTITY_EMAIL_ENABLED=true\nIDENTITY_SMTP_HOST=smtp.acropolis.test\n")
        self.assertEqual(self.execute(), 0)
        self.assertEqual(self.smtp_network_calls(), [
            ("python3", str(self.root / "scripts/smtp-network.py"), "--allow-empty")])
        self.assert_network_verified_before_mutation()
        self.assertTrue(any(kind == "compose" and "smtp-check" in args for kind, args in self.operations))

    def test_invalid_private_network_blocks_enabled_and_disabled_deployments_without_mutation(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        original = self.command.side_effect
        network_prefix = ["python3", str(self.root / "scripts/smtp-network.py")]

        def unavailable_network(args, **kwargs):
            if args[:len(network_prefix)] == network_prefix:
                self.operations.append(("command", tuple(args)))
                self.assertTrue(kwargs.get("capture"))
                raise subprocess.CalledProcessError(1, args, output="synthetic-private-response")
            return original(args, **kwargs)

        self.command.side_effect = unavailable_network
        for mode in ("true", "false"):
            with self.subTest(email_enabled=mode):
                self.operations.clear()
                self.compose.reset_mock()
                self.ready.reset_mock()
                self.smoke.reset_mock()
                candidate = previous_env + "IDENTITY_EMAIL_ENABLED=" + mode + "\nIDENTITY_SMTP_HOST=mail.naperu.cloud\n"
                (self.root / ".env").write_text(candidate)
                with self.assertRaisesRegex(RuntimeError, "Private SMTP network.*private output suppressed") as error:
                    self.execute()
                expected = tuple(network_prefix + ([] if mode == "true" else ["--allow-empty"]))
                self.assertEqual(self.smtp_network_calls(), [expected])
                self.assert_not_activated()
                self.assertEqual(self.running_image, OLD_ID)
                self.assertEqual((self.root / ".env").read_text(), candidate)
                self.assertEqual(self.route.read_text(), self.previous_route)
                self.assertEqual((self.local / "last-active-deployment").read_text().strip(), str(previous))
                self.assertEqual((self.local / "last-deployment").read_text().strip(), str(previous))
                self.assertEqual([entry.name for entry in (self.local / "deployments").iterdir()], ["previous"])
                self.assertFalse((self.local / "backups").exists())
                self.assertNotIn("synthetic-private-response", str(error.exception))
                self.assertNotIn("synthetic-private-response", self.stdout.getvalue())

    def test_identity_backup_preserves_protector_and_keyring_in_private_directory(self):
        source = self.local / "identity/key-protector.pfx"
        source.parent.mkdir()
        source.write_bytes(b"synthetic protected key material")
        destination = self.local / "recovery-fixture"
        destination.mkdir()

        def copy_from_container(args, **kwargs):
            if args[:2] == ["docker", "exec"]:
                self.assertEqual(args[2], "own-web")
                return 0
            self.assertEqual(args[:2], ["docker", "cp"])
            self.assertEqual(args[2], "own-web:/var/acropolis/keys/.")
            Path(args[3], "key.xml").write_text("synthetic encrypted data key")
            return 0

        with patch.object(DEPLOY, "command", side_effect=copy_from_container):
            DEPLOY.backup_identity_material(destination, "own-web")
        self.assertEqual((destination / "identity/key-protector.pfx").read_bytes(), source.read_bytes())
        self.assertEqual((destination / "identity/keyring/key.xml").stat().st_mode & 0o777, 0o600)
        self.assertEqual((destination / "identity").stat().st_mode & 0o777, 0o700)

    def test_explicit_email_disabled_skips_smtp_but_runs_normal_verified_deployment(self):
        self.seed_previous_deployment()
        with (self.root / ".env").open("a") as handle:
            handle.write("IDENTITY_EMAIL_ENABLED=false\n")
        self.execute()
        self.assertEqual(self.running_image, WEB_ID)
        self.assertFalse(any(kind == "compose" and "smtp-check" in args for kind, args in self.operations))
        self.assertTrue(any(kind == "compose" and args == ("--profile", "migration", "run", "--rm", "migrations") for kind, args in self.operations))
        self.smoke.assert_called_once_with()

    def test_exported_email_mode_cannot_override_validated_candidate_or_recovery(self):
        self.seed_previous_deployment()
        with (self.root / ".env").open("a") as handle:
            handle.write("IDENTITY_EMAIL_ENABLED=false\n")
        self.smoke.side_effect = RuntimeError("simulated public failure")
        with patch.dict(os.environ, {"IDENTITY_EMAIL_ENABLED": "true"}):
            with self.assertRaisesRegex(RuntimeError, "Public HTTPS"):
                self.execute()
        mutations = [entry for entry in self.compose.call_args_list if entry.args[0] in ("up", "--profile", "config")]
        self.assertTrue(mutations)
        for entry in mutations:
            self.assertEqual(entry.kwargs["env"]["IDENTITY_EMAIL_ENABLED"], "false")
        rollback = [entry for entry in self.command.call_args_list if entry.args[0][:2] == ["docker", "compose"] and "up" in entry.args[0]]
        self.assertEqual(len(rollback), 1)
        self.assertEqual(rollback[0].kwargs["env"]["IDENTITY_EMAIL_ENABLED"], "true")
        self.assertEqual(self.running_image, OLD_ID)

    def test_failed_smtp_enabled_candidate_recovers_explicitly_disabled_runtime(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        disabled_env = previous_env + "IDENTITY_EMAIL_ENABLED=false\n"
        (previous / "active.env").write_text(disabled_env)
        prepared_env = previous_env + (
            "IDENTITY_EMAIL_ENABLED=true\n"
            "IDENTITY_SMTP_HOST=mail.example.test\n"
            "IDENTITY_SMTP_PORT=465\n"
            "IDENTITY_SMTP_SECURITY=ssl\n"
        )
        (self.root / ".env").write_text(prepared_env)
        self.smoke.side_effect = RuntimeError("simulated public failure")
        with patch.dict(os.environ, {"IDENTITY_EMAIL_ENABLED": "true"}):
            with self.assertRaisesRegex(RuntimeError, "Public HTTPS"):
                self.execute()
        self.assertTrue(any(kind == "compose" and "smtp-check" in args for kind, args in self.operations))
        rollback = [entry for entry in self.command.call_args_list if entry.args[0][:2] == ["docker", "compose"] and "up" in entry.args[0]]
        self.assertEqual(len(rollback), 1)
        self.assertEqual(rollback[0].kwargs["env"]["IDENTITY_EMAIL_ENABLED"], "false")
        directory, manifest = self.deployment_manifest()
        self.assertEqual((directory / "previous.env").read_text(), disabled_env)
        self.assertEqual((self.root / ".env").read_text(), prepared_env)
        self.assertEqual(self.running_image, OLD_ID)
        self.assertEqual(manifest["status"], "failed_recovery_applied")
        self.assertEqual((self.local / "last-active-deployment").read_text().strip(), str(previous))

    def test_invalid_email_mode_never_activates_candidate(self):
        self.seed_previous_deployment()
        with (self.root / ".env").open("a") as handle:
            handle.write("IDENTITY_EMAIL_ENABLED=disabled\n")
        with self.assertRaisesRegex(RuntimeError, "true or false"):
            self.execute()
        self.assertEqual(self.running_image, OLD_ID)
        self.assertFalse(any(kind == "compose" and args[0] == "up" for kind, args in self.operations))

    def test_smtp_failure_stops_before_database_or_web_activation(self):
        self.seed_previous_deployment()
        original = self.compose.side_effect

        def unavailable_smtp(*args, **kwargs):
            if args == ("--profile", "migration", "run", "--rm", "--no-deps", "migrations", "smtp-check"):
                raise RuntimeError("SMTP unavailable")
            return original(*args, **kwargs)

        self.compose.side_effect = unavailable_smtp
        with self.assertRaisesRegex(RuntimeError, "SMTP unavailable"):
            self.execute()
        self.assertEqual(self.running_image, OLD_ID)
        self.assertFalse(any(kind == "compose" and args[0] == "up" for kind, args in self.operations))
        self.assertFalse(any(args[:2] == ("bash", "scripts/backup-db.sh") for _, args in self.operations))
        self.ready.assert_called_once()
        self.assertIn("compose_args", self.ready.call_args.kwargs)
        self.assertFalse(any(kind == "command" and args[:2] == ("docker", "compose") and "up" in args for kind, args in self.operations))
        _, manifest = self.deployment_manifest()
        self.assertFalse(manifest["migration_attempted"])
        self.assertTrue(manifest["recovery"]["verified"])

    def test_recovery_preserves_new_private_configuration_and_uses_previous_runtime_snapshot(self):
        _, previous_env, _ = self.seed_previous_deployment()
        prepared_env = previous_env + "IDENTITY_DP_CERT_PASSWORD=synthetic-new-protector-password\n"
        (self.root / ".env").write_text(prepared_env)
        self.smoke.side_effect = RuntimeError("simulated upstream failure")
        with self.assertRaisesRegex(RuntimeError, "Public HTTPS"):
            self.execute()
        directory, _ = self.deployment_manifest()
        self.assertEqual((self.root / ".env").read_text(), prepared_env)
        self.assertEqual((directory / "candidate.env").read_text(), prepared_env)
        self.assertEqual((directory / "previous.env").read_text(), previous_env)
        self.assertEqual((directory / "candidate.env").stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.running_image, OLD_ID)

    def test_dirty_checkout_never_activates(self):
        self.dirty = " M frontend/src/App.tsx"
        with self.assertRaisesRegex(RuntimeError, "clean"):
            self.execute()
        self.assert_not_activated()

    def test_wrong_branch_or_origin_never_activates(self):
        for field, value in [("branch", "feature"), ("origin", "https://example.invalid/other.git")]:
            with self.subTest(field=field):
                setattr(self, field, value)
                with self.assertRaises(RuntimeError):
                    self.execute()
                self.assert_not_activated()
                setattr(self, field, "main" if field == "branch" else "https://github.com/ricardoalejandro/acropolis-channel.git")

    def test_changed_expected_sha_never_activates(self):
        with self.assertRaisesRegex(RuntimeError, "expected validated commit"):
            self.execute("b" * 40)
        self.assert_not_activated()

    def test_remote_sha_difference_never_activates(self):
        self.remote_sha = "b" * 40
        with self.assertRaisesRegex(RuntimeError, "expected validated commit"):
            self.execute()
        self.assert_not_activated()

    def test_missing_qa_certificate_never_activates(self):
        self.report_path.unlink()
        with self.assertRaisesRegex(RuntimeError, "QA report"):
            self.execute()
        self.assert_not_activated()

    def test_invalid_certificate_and_both_image_ids_never_activate(self):
        for key, value in [
            ("sha", "b" * 40), ("status", "failed"),
            ("deployment_eligible", False), ("deployment_eligible", "true"),
            ("image_id", "sha256:different-web"), ("migration_image_id", "sha256:different-migration"),
        ]:
            with self.subTest(field=key):
                original = self.report[key]
                self.report[key] = value
                self.write_report()
                with self.assertRaisesRegex(RuntimeError, "QA report"):
                    self.execute()
                self.assert_not_activated()
                self.report[key] = original
                self.write_report()

    def test_missing_migration_image_rejects_certificate(self):
        del self.images[MIGRATION_REF]
        with self.assertRaisesRegex(RuntimeError, "QA report"):
            self.execute()
        self.assert_not_activated()

    def test_fresh_publication_uses_exact_certified_images(self):
        self.assertEqual(self.execute(), 0)
        directory, manifest = self.deployment_manifest()
        self.assertEqual(manifest["status"], "published")
        self.assertEqual((manifest["sha"], manifest["image_id"], manifest["migration_image_id"]), (SHA, WEB_ID, MIGRATION_ID))
        self.assertEqual(self.running_image, WEB_ID)
        self.assertEqual(self.route.read_text(), self.template.read_text())
        self.assertEqual((self.local / "last-deployment").read_text().strip(), str(directory))
        self.assertEqual((self.local / "last-active-deployment").read_text().strip(), str(directory))
        self.assertEqual((directory / "active-compose.yml").read_text(), self.compose_text)
        self.assertEqual(DEPLOY.env_values()["APP_IMAGE"], WEB_REF)
        self.assertEqual(DEPLOY.env_values()["MIGRATION_IMAGE"], MIGRATION_REF)
        self.assertTrue(Path(manifest["database_backup"]).is_file())
        self.image_id.assert_any_call(WEB_REF)
        self.image_id.assert_any_call(MIGRATION_REF)
        self.smoke.assert_called_once_with()
        self.sleep.assert_not_called()
        self.assertNotIn("UNCHANGED_SETTING", self.stdout.getvalue())

    def test_dns_blocked_activation_can_be_published_on_retry(self):
        previous, _, _ = self.seed_previous_deployment()
        self.dns.return_value = False
        self.assertEqual(self.execute(), 2)
        blocked, manifest = self.deployment_manifest()
        self.assertEqual(manifest["status"], "internal_ready_dns_blocked")
        self.assertEqual(self.running_image, WEB_ID)
        self.assertEqual(self.route.read_text(), self.previous_route)
        self.assertEqual((self.local / "last-active-deployment").read_text().strip(), str(blocked))
        self.assertEqual((self.local / "last-deployment").read_text().strip(), str(previous))
        self.smoke.assert_not_called()
        self.dns.return_value = True
        self.assertEqual(self.execute(), 0)
        published, manifest = self.deployment_manifest()
        self.assertNotEqual(blocked, published)
        self.assertEqual(manifest["status"], "published")
        self.assertEqual(manifest["previous_image"], WEB_ID)
        self.assertEqual(self.route.read_text(), self.template.read_text())
        self.assertEqual((self.local / "last-deployment").read_text().strip(), str(published))
        self.assertEqual((self.local / "last-active-deployment").read_text().strip(), str(published))

    def test_readiness_failure_recovers_previous_image_configuration_and_pointers(self):
        self.assert_recovers_previous("readiness")

    def test_public_smoke_failure_recovers_previous_image_configuration_and_pointers(self):
        self.assert_recovers_previous("public")

    def assert_recovers_previous(self, phase):
        previous, previous_env, previous_compose = self.seed_previous_deployment()
        if phase == "readiness":
            self.ready.side_effect = lambda **kwargs: kwargs.get("compose_args") is not None
        else:
            self.smoke.side_effect = RuntimeError("simulated certificate failure")
        with self.assertRaisesRegex(RuntimeError, "Readiness failed|Public HTTPS verification failed"):
            self.execute()
        directory, manifest = self.deployment_manifest()
        self.assertEqual(manifest["status"], "failed_recovery_applied")
        self.assertEqual(self.running_image, OLD_ID)
        self.assertEqual((self.root / ".env").read_text(), previous_env)
        self.assertEqual(self.route.read_text(), self.previous_route)
        self.assertEqual((directory / "compose.yml").read_text(), previous_compose)
        self.assertEqual((self.local / "last-deployment").read_text().strip(), str(previous))
        self.assertEqual((self.local / "last-active-deployment").read_text().strip(), str(previous))
        rollback = [args for kind, args in self.operations if kind == "command" and args[:2] == ("docker", "compose")]
        self.assertEqual(len(rollback), 2)
        self.assertIn("--no-deps", rollback[0])
        self.assertEqual(rollback[0][rollback[0].index("--project-directory") + 1], str(self.root))
        self.assertEqual(rollback[0][-1], "web")
        self.assertFalse(any("restore-db" in str(args) or "down" in args for _, args in self.operations))
        self.assertEqual(self.ready.call_count, 31 if phase == "readiness" else 2)
        self.assertTrue(manifest["recovery"]["verified"])
        self.assertTrue(manifest["recovery"]["imageVerified"])
        self.assertTrue(manifest["recovery"]["readinessVerified"])
        self.assertFalse(manifest["recovery"]["maintenanceRequired"])
        self.assertEqual(self.smoke.call_count, 0 if phase == "readiness" else 36)

    def test_failed_fresh_publication_removes_only_its_route_and_stops_only_web(self):
        neighbor = self.route.parent / "neighbor.yml"
        neighbor.write_text("http:\n  routers:\n    unrelated: {}\n")
        self.smoke.side_effect = RuntimeError("simulated upstream failure")
        with self.assertRaisesRegex(RuntimeError, "Public HTTPS"):
            self.execute()
        self.assertFalse(self.route.exists())
        self.assertEqual(neighbor.read_text(), "http:\n  routers:\n    unrelated: {}\n")
        self.assertFalse((self.local / "last-active-deployment").exists())
        self.assertEqual((self.root / ".env").read_text(), self.environment)
        self.assertIsNone(self.running_image)
        self.assertTrue(any(kind == "command" and args[:2] == ("docker", "compose") and args[-2:] == ("stop", "web") for kind, args in self.operations))
        _, manifest = self.deployment_manifest()
        self.assertEqual(manifest["status"], "recovery_incomplete")
        self.assertEqual(manifest["recovery"]["reason"], "no_previous_image")
        self.assertTrue(manifest["recovery"]["webStopped"])

    def assert_incomplete_recovery(self, previous, previous_env):
        directory, manifest = self.deployment_manifest()
        self.assertEqual(manifest["status"], "recovery_incomplete")
        self.assertFalse(manifest["recovery"]["verified"])
        self.assertTrue(manifest["recovery"]["maintenanceRequired"])
        self.assertFalse((self.local / "last-active-deployment").exists())
        self.assertEqual((self.local / "last-deployment").read_text().strip(), str(previous))
        self.assertEqual((self.root / ".env").read_text(), previous_env)
        self.assertEqual(self.route.read_text(), self.previous_route)
        self.assertTrue(Path(manifest["database_backup"]).is_file())
        self.assertTrue((directory / "previous.env").is_file())
        self.assertTrue((previous / "active.env").is_file())
        self.assertFalse(any("restore-db" in str(args) or "down" in args for _, args in self.operations))
        self.assertNotIn("private-recovery-value", json.dumps(manifest))
        return manifest

    def fail_migration(self):
        original = self.compose.side_effect

        def partial(*args, **kwargs):
            if args == ("--profile", "migration", "run", "--rm", "migrations"):
                self.operations.append(("compose", args))
                raise RuntimeError("partial migration private-recovery-value")
            return original(*args, **kwargs)

        self.compose.side_effect = partial

    def test_partial_migration_failure_verifies_compatible_previous_runtime(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        _, manifest = self.deployment_manifest()
        self.assertTrue(manifest["migration_attempted"])
        self.assertEqual(manifest["status"], "failed_recovery_applied")
        self.assertTrue(manifest["recovery"]["imageVerified"])
        self.assertTrue(manifest["recovery"]["readinessVerified"])
        self.assertEqual((self.local / "last-active-deployment").read_text().strip(), str(previous))
        self.assertEqual((self.root / ".env").read_text(), previous_env)
        self.assertEqual(self.running_image, OLD_ID)
        self.ready.assert_called_once()
        self.assertIn("compose_args", self.ready.call_args.kwargs)

    def test_partial_migration_with_incompatible_schema_stops_only_own_web(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertTrue(manifest["migration_attempted"])
        self.assertTrue(manifest["recovery"]["imageVerified"])
        self.assertFalse(manifest["recovery"]["readinessVerified"])
        self.assertTrue(manifest["recovery"]["stopRequested"])
        self.assertTrue(manifest["recovery"]["stopCommandSucceeded"])
        self.assertTrue(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["stopObservedContainers"], 1)
        self.assertIsNone(self.running_image)
        self.assertEqual(self.ready.call_count, 30)
        self.assertEqual(self.sleep.call_count, 29)

    def test_recovery_command_error_keeps_honest_manifest_and_attempts_stop(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.smoke.side_effect = RuntimeError("simulated upstream failure")
        original = self.command.side_effect

        def broken_start(args, **kwargs):
            if args[:2] == ["docker", "compose"] and "up" in args:
                self.operations.append(("command", tuple(args)))
                raise subprocess.CalledProcessError(7, args, stderr="private-recovery-value")
            return original(args, **kwargs)

        self.command.side_effect = broken_start
        with self.assertRaisesRegex(RuntimeError, "Public HTTPS"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertEqual(manifest["recovery"]["errors"], [{"stage": "start-previous-runtime", "errorType": "CalledProcessError"}])
        self.assertTrue(manifest["recovery"]["webStopped"])
        self.assertIsNone(self.running_image)

    def test_wrong_previous_image_cannot_restore_active_pointer(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        original = self.command.side_effect

        def wrong_image(args, **kwargs):
            if args[:2] == ["docker", "inspect"] and args[-1] == "{{.Image}}|{{.State.Running}}":
                self.operations.append(("command", tuple(args)))
                return WEB_ID + "|true"
            return original(args, **kwargs)

        self.command.side_effect = wrong_image
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertFalse(manifest["recovery"]["imageVerified"])
        self.assertTrue(manifest["recovery"]["webStopped"])
        self.ready.assert_not_called()

    def test_readiness_exception_is_sanitized_and_does_not_skip_manifest(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.side_effect = RuntimeError("private-recovery-value")
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertEqual(manifest["recovery"]["errors"], [{"stage": "verify-previous-readiness", "errorType": "RuntimeError"}])
        self.assertTrue(manifest["recovery"]["webStopped"])

    def test_malformed_previous_readiness_json_never_claims_recovery(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.side_effect = self.original_ready
        with patch.object(DEPLOY.subprocess, "run", return_value=subprocess.CompletedProcess(["mock"], 0, stdout="private-recovery-value", stderr="")) as probe:
            with self.assertRaisesRegex(RuntimeError, "partial migration"):
                self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertEqual(manifest["recovery"]["errors"], [{"stage": "verify-previous-readiness", "errorType": "JSONDecodeError"}])
        self.assertEqual(probe.call_args.kwargs["timeout"], 10)
        self.assertIn(str(self.root), probe.call_args.args[0])

    def test_recovery_stop_error_still_clears_active_and_preserves_published_history(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        original = self.command.side_effect

        def broken_stop(args, **kwargs):
            if args[:2] == ["docker", "compose"] and args[-2:] == ["stop", "web"]:
                self.operations.append(("command", tuple(args)))
                raise subprocess.TimeoutExpired(args, 30, stderr="private-recovery-value")
            return original(args, **kwargs)

        self.command.side_effect = broken_stop
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertFalse(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["errors"][-1], {"stage": "stop-own-web", "errorType": "TimeoutExpired"})

    def test_recovery_uses_actual_previous_config_and_preserves_prepared_updates(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        prepared_env = self.environment + "IDENTITY_EMAIL_ENABLED=false\nCANDIDATE_ONLY=private-recovery-value\n"
        (self.root / ".env").write_text(prepared_env)
        self.fail_migration()
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        _, manifest = self.deployment_manifest()
        self.assertEqual(manifest["status"], "failed_recovery_applied")
        self.assertEqual((self.root / ".env").read_text(), prepared_env)
        self.assertEqual((previous / "active.env").read_text(), previous_env)
        self.assertEqual(self.route.read_text(), self.previous_route)
        self.assertEqual((self.local / "last-deployment").read_text().strip(), str(previous))
        self.assertNotIn("private-recovery-value", json.dumps(manifest))
        self.assertEqual(self.ready.call_args.kwargs["env"]["IDENTITY_EMAIL_ENABLED"], "true")

    def test_readiness_timeout_leaves_honest_maintenance_state(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.side_effect = subprocess.TimeoutExpired(["mock"], 10, stderr="private-recovery-value")
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertEqual(manifest["recovery"]["errors"], [{"stage": "verify-previous-readiness", "errorType": "TimeoutExpired"}])
        self.assertTrue(manifest["recovery"]["webStopped"])

    def test_routing_recovery_exception_does_not_prevent_stop_and_manifest(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        with patch.object(DEPLOY, "atomic_copy", side_effect=PermissionError("private-recovery-value")):
            with self.assertRaisesRegex(RuntimeError, "partial migration"):
                self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertEqual(manifest["recovery"]["errors"], [{"stage": "restore-routing", "errorType": "PermissionError"}])
        self.assertTrue(manifest["recovery"]["webStopped"])
        self.ready.assert_not_called()

    def test_stop_exit_zero_with_running_container_does_not_claim_stopped(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        original = self.command.side_effect

        def acknowledge_without_stop(args, **kwargs):
            if args[:2] == ["docker", "compose"] and args[-2:] == ["stop", "web"]:
                self.operations.append(("command", tuple(args)))
                return ""
            return original(args, **kwargs)

        self.command.side_effect = acknowledge_without_stop
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertTrue(manifest["recovery"]["stopRequested"])
        self.assertTrue(manifest["recovery"]["stopCommandSucceeded"])
        self.assertFalse(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["errors"][-1], {"stage": "verify-own-web-state", "errorType": "RuntimeError"})
        self.assertEqual(self.running_image, OLD_ID)

    def test_post_stop_inventory_failure_does_not_claim_stopped(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        original = self.command.side_effect

        def broken_inventory(args, **kwargs):
            if args[:2] == ["docker", "compose"] and args[-4:] == ["ps", "-a", "-q", "web"]:
                self.operations.append(("command", tuple(args)))
                raise subprocess.CalledProcessError(1, args, stderr="private-recovery-value")
            return original(args, **kwargs)

        self.command.side_effect = broken_inventory
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertTrue(manifest["recovery"]["stopCommandSucceeded"])
        self.assertFalse(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["errors"][-1], {"stage": "list-own-web-after-stop", "errorType": "CalledProcessError"})

    def test_post_stop_inspection_timeout_does_not_claim_stopped(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        original = self.command.side_effect

        def unknown_state(args, **kwargs):
            if args[:2] == ["docker", "inspect"] and args[-1] == "{{.State.Running}}":
                self.operations.append(("command", tuple(args)))
                raise subprocess.TimeoutExpired(args, 30, stderr="private-recovery-value")
            return original(args, **kwargs)

        self.command.side_effect = unknown_state
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertTrue(manifest["recovery"]["stopCommandSucceeded"])
        self.assertFalse(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["errors"][-1], {"stage": "verify-own-web-state", "errorType": "TimeoutExpired"})

    def test_post_stop_inspection_nonzero_does_not_claim_stopped(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        original = self.command.side_effect

        def failed_inspection(args, **kwargs):
            if args[:2] == ["docker", "inspect"] and args[-1] == "{{.State.Running}}":
                self.operations.append(("command", tuple(args)))
                raise subprocess.CalledProcessError(1, args, stderr="private-recovery-value")
            return original(args, **kwargs)

        self.command.side_effect = failed_inspection
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertTrue(manifest["recovery"]["stopCommandSucceeded"])
        self.assertFalse(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["errors"][-1], {"stage": "verify-own-web-state", "errorType": "CalledProcessError"})

    def test_any_remaining_web_replica_prevents_verified_stop(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        original = self.command.side_effect
        other = "e" * 64

        def one_remaining_replica(args, **kwargs):
            if args[:2] == ["docker", "compose"] and args[-4:] == ["ps", "-a", "-q", "web"]:
                self.operations.append(("command", tuple(args)))
                return STOP_CONTAINER_ID + "\n" + other
            if args[:2] == ["docker", "inspect"] and args[2] == other:
                self.operations.append(("command", tuple(args)))
                self.assertEqual(args[-1], "{{.State.Running}}")
                return "true"
            return original(args, **kwargs)

        self.command.side_effect = one_remaining_replica
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertTrue(manifest["recovery"]["stopCommandSucceeded"])
        self.assertFalse(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["stopObservedContainers"], 2)
        self.assertEqual(manifest["recovery"]["errors"][-1], {"stage": "verify-own-web-state", "errorType": "RuntimeError"})

    def test_post_stop_empty_inventory_is_valid_absence(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        original = self.command.side_effect

        def remove_own_container(args, **kwargs):
            result = original(args, **kwargs)
            if args[:2] == ["docker", "compose"] and args[-2:] == ["stop", "web"]:
                self.web_container_exists = False
            return result

        self.command.side_effect = remove_own_container
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertTrue(manifest["recovery"]["stopRequested"])
        self.assertTrue(manifest["recovery"]["stopCommandSucceeded"])
        self.assertTrue(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["stopObservedContainers"], 0)
        self.assertFalse(any(kind == "command" and args[-1:] == ("{{.State.Running}}",) for kind, args in self.operations))

    def test_post_stop_unknown_inventory_output_does_not_claim_absence(self):
        previous, previous_env, _ = self.seed_previous_deployment()
        self.fail_migration()
        self.ready.return_value = False
        original = self.command.side_effect

        def malformed_inventory(args, **kwargs):
            if args[:2] == ["docker", "compose"] and args[-4:] == ["ps", "-a", "-q", "web"]:
                self.operations.append(("command", tuple(args)))
                return "unexpected diagnostic output"
            return original(args, **kwargs)

        self.command.side_effect = malformed_inventory
        with self.assertRaisesRegex(RuntimeError, "partial migration"):
            self.execute()
        manifest = self.assert_incomplete_recovery(previous, previous_env)
        self.assertTrue(manifest["recovery"]["stopCommandSucceeded"])
        self.assertFalse(manifest["recovery"]["webStopped"])
        self.assertEqual(manifest["recovery"]["errors"][-1], {"stage": "list-own-web-after-stop", "errorType": "RuntimeError"})

    def test_set_image_refs_preserves_unrelated_values_and_private_permissions(self):
        original = self.environment + "APP_IMAGE=old\nMIGRATION_IMAGE=old-migrations\n"
        (self.root / ".env").write_text(original)
        DEPLOY.set_image_refs(WEB_REF, MIGRATION_REF)
        written = (self.root / ".env").read_text()
        self.assertTrue(written.startswith(self.environment))
        self.assertEqual(written.count("\nAPP_IMAGE="), 1)
        self.assertEqual(written.count("\nMIGRATION_IMAGE="), 1)
        self.assertEqual(stat.S_IMODE((self.root / ".env").stat().st_mode), 0o600)
        self.assertFalse((self.root / ".env.deploy.tmp").exists())
        self.assertEqual(DEPLOY.env_values()["UNCHANGED_SETTING"], "a value with spaces = retained")

    def test_atomic_copy_replaces_own_file_only(self):
        neighbor = self.route.parent / "neighbor.yml"
        neighbor.write_text("leave this file intact")
        self.route.write_text("old own route")
        DEPLOY.atomic_copy(self.template, self.route)
        self.assertEqual(self.route.read_text(), self.template.read_text())
        self.assertEqual(neighbor.read_text(), "leave this file intact")
        self.assertFalse(self.route.with_name(self.route.name + ".tmp").exists())
        self.assertEqual(stat.S_IMODE(self.route.stat().st_mode), 0o644)

    def test_routing_collision_refuses_activation(self):
        neighbor = self.route.parent / "neighbor.yml"
        neighbor.write_text(DEPLOY.yaml.safe_dump({"http": {"routers": {
            "foreign": {"rule": "Host(`" + DEPLOY.DOMAIN + "`)"}
        }}}))
        with self.assertRaisesRegex(RuntimeError, "another routing file"):
            self.execute()
        self.assert_not_activated()
        self.assertTrue(neighbor.exists())


if __name__ == "__main__":
    unittest.main()
