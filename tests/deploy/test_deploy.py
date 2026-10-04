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

    def fake_command(self, args, capture=False, env=None, check=True):
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
            self.assertEqual(args[2], "simulated-web-container")
            return self.running_image
        if args[:2] == ["docker", "tag"]:
            return 0
        if args[:2] == ["bash", "scripts/backup-db.sh"]:
            output = Path(args[args.index("--output") + 1])
            self.assertTrue(output.is_relative_to(self.local / "backups"))
            self.assertEqual(args[args.index("--database") + 1], "acropolis")
            output.parent.mkdir(exist_ok=True)
            output.write_bytes(b"only a temporary mocked backup")
            return 0
        if args[:2] == ["docker", "compose"]:
            self.assertIn("--no-deps", args)
            self.assertEqual(args[-1], "web")
            self.assertNotIn("db", args)
            override_path = Path(args[args.index("-f", args.index("-f") + 1) + 1])
            override = DEPLOY.yaml.safe_load(override_path.read_text())
            self.running_image = override["services"]["web"]["image"]
            return 0
        raise AssertionError("Unexpected mocked command: " + repr(args))

    def fake_compose(self, *args, env=None, capture=False, check=True):
        self.operations.append(("compose", args))
        if args == ("ps", "-q", "web"):
            return "simulated-web-container" if self.running_image else ""
        if args == ("config", "--quiet"):
            return 0
        if args == ("up", "-d", "--no-build", "--wait", "--wait-timeout", "90", "db"):
            return 0
        if args == ("--profile", "migration", "run", "--rm", "migrations"):
            self.assertEqual(env["MIGRATION_IMAGE"], MIGRATION_REF)
            return 0
        if args == ("up", "-d", "--no-build", "web"):
            self.assertEqual(env["APP_IMAGE"], WEB_REF)
            self.running_image = self.images[env["APP_IMAGE"]]
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
        return prior, previous_env, previous_compose

    def deployment_manifest(self):
        directories = [p for p in (self.local / "deployments").iterdir() if p.name != "previous"]
        newest = max(directories, key=lambda p: p.name)
        return newest, json.loads((newest / "manifest.json").read_text())

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
            self.ready.return_value = False
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
        self.assertEqual(len(rollback), 1)
        self.assertIn("--no-deps", rollback[0])
        self.assertEqual(rollback[0][-1], "web")
        self.assertFalse(any("restore-db" in str(args) or "down" in args for _, args in self.operations))
        self.assertEqual(self.ready.call_count, 30 if phase == "readiness" else 1)
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
        self.compose.assert_any_call("stop", "web", check=False)

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
