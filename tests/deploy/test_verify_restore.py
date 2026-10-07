import hashlib
from pathlib import Path
import re
import shlex
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[2]
EMPTY_DIGEST = hashlib.sha256(b'').hexdigest()


def bash_function(source, name):
    match = re.search(r'^' + re.escape(name) + r'\(\) \{\n.*?^\}', source, re.MULTILINE | re.DOTALL)
    if match is None:
        raise AssertionError('Missing real verify function: ' + name)
    return match.group(0)


class VerifyRestoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = (ROOT / 'scripts/verify.sh').read_text()

    def invoke(self, module, mock_exit, output, use_compose=False):
        digest = 'subscription_digest' if module == 'subscriptions' else 'catalog_digest'
        callback = 'restored_subscriptions_consistency' if module == 'subscriptions' else 'restored_catalog_consistency'
        baseline = 'subscriptions_before' if module == 'subscriptions' else 'catalog_before'
        declarations = bash_function(self.source, callback)
        if use_compose:
            declarations += '\n' + bash_function(self.source, digest)
            declarations += '\ncompose() { printf %s ' + shlex.quote(output) + '; return ' + str(mock_exit) + '; }\n'
            diagnostic = (
                'if observed="$(' + digest + ')"; then digest_exit=0; else digest_exit=$?; fi\n'
                'printf "observed=%s\\ndigest_exit=%s\\n" "$observed" "$digest_exit"\n'
            )
        else:
            declarations += '\n' + digest + '() { printf %s ' + shlex.quote(output) + '; return ' + str(mock_exit) + '; }\n'
            diagnostic = ''
        script = (
            'set -Eeuo pipefail\n'
            + declarations + '\n'
            + 'restore_project=acropolis_test_restore_probe\n'
            + 'restore_database=acropolis_test_restore_probe\n'
            + baseline + '=' + shlex.quote(EMPTY_DIGEST) + '\n'
            + diagnostic
            + 'if ' + callback + '; then callback_exit=0; else callback_exit=$?; fi\n'
            + 'exit "$callback_exit"\n'
        )
        result = subprocess.run(
            ['bash', '--noprofile', '--norc', '-c', script],
            cwd=ROOT,
            env={'PATH': '/usr/bin:/bin', 'LC_ALL': 'C'},
            capture_output=True,
            text=True,
            timeout=5,
            check=False,
        )
        return result

    def assert_callback(self, module, mock_exit, output, should_pass, use_compose=False):
        result = self.invoke(module, mock_exit, output, use_compose)
        self.assertEqual(result.returncode, 0 if should_pass else 1)
        if use_compose:
            expected_hash = hashlib.sha256(output.encode()).hexdigest()
            self.assertIn('observed=' + expected_hash + '\n', result.stdout)
            self.assertIn('digest_exit=' + str(mock_exit) + '\n', result.stdout)

    def test_subscriptions_matching_empty_hash_failed_digest_rejected(self):
        self.assert_callback('subscriptions', 17, EMPTY_DIGEST, False)

    def test_catalog_matching_empty_hash_failed_digest_rejected(self):
        self.assert_callback('catalog', 17, EMPTY_DIGEST, False)

    def test_subscriptions_successful_empty_digest_accepted(self):
        self.assert_callback('subscriptions', 0, EMPTY_DIGEST, True)

    def test_catalog_successful_empty_digest_accepted(self):
        self.assert_callback('catalog', 0, EMPTY_DIGEST, True)

    def test_subscriptions_different_digest_rejected(self):
        self.assert_callback('subscriptions', 0, 'a' * 64, False)

    def test_catalog_different_digest_rejected(self):
        self.assert_callback('catalog', 0, 'a' * 64, False)

    def test_subscriptions_real_pipeline_empty_output_failed_compose_rejected(self):
        self.assert_callback('subscriptions', 17, '', False, use_compose=True)

    def test_catalog_real_pipeline_empty_output_failed_compose_rejected(self):
        self.assert_callback('catalog', 17, '', False, use_compose=True)

    def test_subscriptions_real_pipeline_successful_empty_output_accepted(self):
        self.assert_callback('subscriptions', 0, '', True, use_compose=True)

    def test_catalog_real_pipeline_successful_empty_output_accepted(self):
        self.assert_callback('catalog', 0, '', True, use_compose=True)

    def test_subscriptions_real_pipeline_changed_output_rejected(self):
        self.assert_callback('subscriptions', 0, 'changed\n', False, use_compose=True)

    def test_catalog_real_pipeline_changed_output_rejected(self):
        self.assert_callback('catalog', 0, 'changed\n', False, use_compose=True)


if __name__ == '__main__':
    unittest.main()
