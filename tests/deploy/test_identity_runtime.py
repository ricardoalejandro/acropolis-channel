"""Private runtime configuration tests with no access to Docker or production."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import subprocess
from types import SimpleNamespace
from unittest.mock import Mock
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "identity_runtime_under_test", Path(__file__).resolve().parents[2] / "scripts/identity-runtime.py")
RUNTIME = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNTIME)


class IdentityRuntimeTests(unittest.TestCase):
    def settings(self):
        return {
            "IDENTITY_DP_CERT_PASSWORD": "synthetic-fixture-only",
            "IDENTITY_KNOWN_PROXIES": "10.10.0.2",
            "IDENTITY_SMTP_HOST": "mail.example.test",
            "IDENTITY_SMTP_FROM_EMAIL": "noreply@example.test",
            "IDENTITY_SMTP_PORT": "587",
            "IDENTITY_SMTP_USERNAME": "qa",
            "IDENTITY_SMTP_PASSWORD": "synthetic-fixture-only",
            "IDENTITY_SMTP_SECURITY": "starttls",
        }

    def test_tls_configuration_accepts_authenticated_provider(self):
        settings = self.settings()
        RUNTIME.validate_settings(settings)
        settings.update(IDENTITY_SMTP_USERNAME="qa", IDENTITY_SMTP_PASSWORD="fixture")
        RUNTIME.validate_settings(settings)

    def test_incomplete_or_insecure_configuration_rejected(self):
        variants = [
            {"IDENTITY_SMTP_HOST": ""},
            {"IDENTITY_SMTP_SECURITY": "none"},
            {"IDENTITY_SMTP_PORT": "invalid"},
            {"IDENTITY_SMTP_PORT": "65536"},
            {"IDENTITY_SMTP_USERNAME": ""},
            {"IDENTITY_SMTP_PASSWORD": ""},
            {"IDENTITY_EMAIL_ENABLED": "typo"},
            {"IDENTITY_PUBLIC_ORIGIN": "https://attacker.example"},
            {"IDENTITY_KNOWN_PROXIES": "0.0.0.0"},
            {"IDENTITY_SMTP_FROM_EMAIL": "bad\r\nBcc: x@example.test"},
        ]
        for variant in variants:
            with self.subTest(variant=variant):
                with self.assertRaises((RuntimeError, ValueError)):
                    RUNTIME.validate_settings(dict(self.settings(), **variant))

    def test_explicitly_deferred_email_keeps_origin_proxy_and_key_requirements(self):
        settings = {key: value for key, value in self.settings().items()
                    if not key.startswith("IDENTITY_SMTP_")}
        settings["IDENTITY_EMAIL_ENABLED"] = "false"
        RUNTIME.validate_settings(settings)
        for variant in [{"IDENTITY_DP_CERT_PASSWORD": ""},
                        {"IDENTITY_KNOWN_PROXIES": "0.0.0.0"},
                        {"IDENTITY_PUBLIC_ORIGIN": "https://foreign.test"}]:
            with self.subTest(variant=variant), self.assertRaises((RuntimeError, ValueError)):
                RUNTIME.validate_settings(dict(settings, **variant))
        with self.assertRaises(RuntimeError):
            RUNTIME.validate_settings(dict(settings, IDENTITY_EMAIL_ENABLED="true"))

    def test_updates_preserve_unrelated_secrets_comments_and_private_mode(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(RUNTIME, "ROOT", Path(folder)):
            path = Path(folder) / ".env"
            path.write_text("# existing\nDB_PASSWORD='kept value'\nIDENTITY_KNOWN_PROXIES=old\n")
            RUNTIME.write_settings({"IDENTITY_KNOWN_PROXIES": "10.0.0.3"})
            self.assertEqual(path.read_text(),
                             "# existing\nDB_PASSWORD='kept value'\nIDENTITY_KNOWN_PROXIES=10.0.0.3\n")
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)

    def test_symlink_configuration_rejected_before_write(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(RUNTIME, "ROOT", Path(folder)):
            outside = Path(folder) / "outside"
            outside.write_text("keep")
            (Path(folder) / ".env").symlink_to(outside)
            with self.assertRaisesRegex(RuntimeError, "symbolic"):
                RUNTIME.write_settings({"KEY": "value"})
            self.assertEqual(outside.read_text(), "keep")

    def test_only_actual_traefik_addresses_on_named_network_are_trusted(self):
        records = {
            "one": [{"NetworkSettings": {"Networks": {
                "dokploy-network": {"IPAddress": "10.0.0.7"},
                "other": {"IPAddress": "172.30.0.8"}}}}],
            "two": [{"NetworkSettings": {"Networks": {
                "dokploy-network": {"IPAddress": "10.0.0.6"}}}}],
        }
        def fake(args):
            return "one\ntwo" if args[1] == "ps" else json.dumps(records[args[2]])
        with patch.object(RUNTIME, "command", side_effect=fake):
            self.assertEqual(RUNTIME.discover_proxies("dokploy-network"), "10.0.0.6,10.0.0.7")

    def test_missing_traefik_never_trusts_a_whole_network(self):
        with patch.object(RUNTIME, "command", return_value=""):
            with self.assertRaises(RuntimeError):
                RUNTIME.discover_proxies("dokploy-network")

    def test_missing_preexisting_protector_never_replaces_it(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(RUNTIME, "ROOT", Path(folder)):
            (Path(folder) / ".env").write_text("IDENTITY_DP_CERT_PASSWORD=existing\n")
            with patch.object(RUNTIME, "command", side_effect=AssertionError("No real command")):
                with self.assertRaisesRegex(RuntimeError, "restore"):
                    RUNTIME.prepare()

    def test_protector_permissions_require_private_runtime_readability(self):
        path = Mock()
        path.is_file.return_value = True
        path.is_symlink.return_value = False
        for mode, uid, gid in [(0o600, 0, 0), (0o644, 0, 1654), (0o660, 0, 1654)]:
            path.stat.return_value = SimpleNamespace(st_mode=mode, st_uid=uid, st_gid=gid)
            with patch.object(RUNTIME.subprocess, "run", side_effect=AssertionError("No command before permissions")):
                with self.assertRaises(RuntimeError):
                    RUNTIME.validate_protector(path, "synthetic")

    def test_real_pkcs12_requires_private_key_and_correct_password(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            key, cert = base / "key.pem", base / "cert.pem"
            password = "synthetic-fixture-only"
            environment = dict(os.environ, ACROPOLIS_PFX_PASSWORD=password)
            subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                            "-days", "1", "-subj", "/CN=QA protector",
                            "-keyout", str(key), "-out", str(cert)],
                           capture_output=True, check=True)
            private, public = base / "private.pfx", base / "public-only.pfx"
            for target, extra in [(private, ["-inkey", str(key)]), (public, ["-nokeys"])]:
                subprocess.run(["openssl", "pkcs12", "-export", "-in", str(cert),
                                "-out", str(target), "-passout", "env:ACROPOLIS_PFX_PASSWORD", *extra],
                               env=environment, capture_output=True, check=True)
                target.chmod(0o600)
            with patch.object(RUNTIME, "APP_UID", os.getuid()):
                RUNTIME.validate_protector(private, password)
                with self.assertRaisesRegex(RuntimeError, "encrypted private key"):
                    RUNTIME.validate_protector(public, password)
                with self.assertRaisesRegex(RuntimeError, "encrypted private key"):
                    RUNTIME.validate_protector(private, "incorrect")

    def test_command_failure_does_not_expose_private_output(self):
        class Result:
            returncode = 1
            stdout = "SYNTHETIC_SECRET_792"
            stderr = "SYNTHETIC_SECRET_792"
        with patch.object(RUNTIME.subprocess, "run", return_value=Result()):
            with self.assertRaisesRegex(RuntimeError, "suppressed") as result:
                RUNTIME.command(["openssl"])
            self.assertNotIn("SYNTHETIC_SECRET_792", str(result.exception))


if __name__ == "__main__":
    unittest.main()
