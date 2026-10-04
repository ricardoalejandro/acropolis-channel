import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

FILE = Path(__file__).resolve().parents[1] / "identity-runtime.py"
SPEC = importlib.util.spec_from_file_location("identity_runtime", FILE)
RUNTIME = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNTIME)


class PrivateSmtpPreflightTests(unittest.TestCase):
    def settings(self, **changes):
        values = {
            "IDENTITY_EMAIL_ENABLED": "true", "IDENTITY_DP_CERT_PASSWORD": "synthetic-pfx-password",
            "IDENTITY_KNOWN_PROXIES": "192.0.2.10", "IDENTITY_SMTP_HOST": "mail.naperu.cloud",
            "IDENTITY_SMTP_PORT": "465", "IDENTITY_SMTP_SECURITY": "ssl",
            "IDENTITY_SMTP_USERNAME": "sender@acropolis.test", "IDENTITY_SMTP_PASSWORD": "synthetic-mail-password",
            "IDENTITY_SMTP_FROM_EMAIL": "sender@acropolis.test",
        }
        values.update(changes)
        return values

    def test_own_mail_requires_authenticated_implicit_tls_contract(self):
        RUNTIME.validate_settings(self.settings())
        for field, value in [("IDENTITY_SMTP_PORT", "587"), ("IDENTITY_SMTP_SECURITY", "starttls"),
                             ("IDENTITY_SMTP_HOST", "mail.naperu.cloud."), ("IDENTITY_SMTP_HOST", "MAIL.NAPERU.CLOUD"),
                             ("IDENTITY_SMTP_HOST", " mail.naperu.cloud ")]:
            with self.subTest(field=field, value=value), self.assertRaises(RuntimeError):
                RUNTIME.validate_settings(self.settings(**{field: value}))

    def test_preflight_is_read_only_and_requires_private_mail_network(self):
        with patch.object(RUNTIME, "command", return_value="{}") as run:
            RUNTIME.validate_smtp_network(self.settings())
        self.assertEqual(run.call_args.args[0], ["python3", str(RUNTIME.ROOT / "scripts/smtp-network.py")])
        self.assertEqual(run.call_count, 1)

    def test_failed_topology_blocks_preflight_without_secret_disclosure(self):
        with patch.object(RUNTIME, "command", side_effect=RuntimeError("Identity runtime command failed; private output suppressed")):
            with self.assertRaisesRegex(RuntimeError, "private output suppressed") as error:
                RUNTIME.validate_smtp_network(self.settings())
        self.assertNotIn("synthetic-mail-password", str(error.exception))

    def test_disabled_email_or_external_provider_never_inspects_productive_mail_network(self):
        with patch.object(RUNTIME, "command") as run:
            RUNTIME.validate_smtp_network(self.settings(IDENTITY_EMAIL_ENABLED="false"))
            RUNTIME.validate_smtp_network(self.settings(IDENTITY_SMTP_HOST="smtp.acropolis.test"))
        run.assert_not_called()

    def test_disabled_email_does_not_require_smtp_material(self):
        RUNTIME.validate_settings({"IDENTITY_EMAIL_ENABLED": "false", "IDENTITY_DP_CERT_PASSWORD": "synthetic",
                                   "IDENTITY_KNOWN_PROXIES": "192.0.2.10"})

    def test_network_validation_occurs_inside_runtime_check_before_success(self):
        settings = self.settings()
        with patch.object(RUNTIME, "read_settings", return_value=settings), patch.object(RUNTIME, "validate_protector"), \
                patch.object(RUNTIME, "discover_proxies", return_value="192.0.2.10"), \
                patch.object(RUNTIME, "validate_smtp_network", side_effect=RuntimeError("Invalid private topology")) as check:
            with self.assertRaisesRegex(RuntimeError, "Invalid private topology"):
                RUNTIME.check()
        check.assert_called_once_with(settings)


DEPLOY_SPEC = importlib.util.spec_from_file_location("private_deploy", FILE.with_name("deploy.py"))
DEPLOY = importlib.util.module_from_spec(DEPLOY_SPEC)
DEPLOY_SPEC.loader.exec_module(DEPLOY)


class ComposeSmtpPreflightTests(unittest.TestCase):
    def test_disabled_mail_and_external_provider_still_require_owned_internal_network(self):
        for settings in [{"IDENTITY_EMAIL_ENABLED": "false"},
                         {"IDENTITY_EMAIL_ENABLED": "true", "IDENTITY_SMTP_HOST": "smtp.acropolis.test"}]:
            with self.subTest(settings=settings), patch.object(DEPLOY, "command", return_value="{}") as run:
                DEPLOY.validate_compose_smtp_network(settings)
                self.assertEqual(run.call_args.args[0][-2:], [str(DEPLOY.ROOT / "scripts/smtp-network.py"), "--allow-empty"])
                self.assertTrue(run.call_args.kwargs["capture"])

    def test_own_smtp_never_allows_missing_mailserver(self):
        with patch.object(DEPLOY, "command", return_value="{}") as run:
            DEPLOY.validate_compose_smtp_network({"IDENTITY_EMAIL_ENABLED": "true", "IDENTITY_SMTP_HOST": "mail.naperu.cloud"})
        self.assertEqual(run.call_args.args[0][-1], str(DEPLOY.ROOT / "scripts/smtp-network.py"))
        self.assertNotIn("--allow-empty", run.call_args.args[0])

    def test_any_failed_network_check_blocks_even_email_disabled(self):
        error = DEPLOY.subprocess.CalledProcessError(1, ["synthetic-command"], output="private-metadata", stderr="private-metadata")
        with patch.object(DEPLOY, "command", side_effect=error):
            with self.assertRaisesRegex(RuntimeError, "private output suppressed") as failure:
                DEPLOY.validate_compose_smtp_network({"IDENTITY_EMAIL_ENABLED": "false"})
        self.assertNotIn("private-metadata", str(failure.exception))


if __name__ == "__main__":
    unittest.main()
