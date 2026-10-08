"""Fast verification CLI behavior with synthetic processes and temporary checkouts only."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest

SOURCE_ROOT = Path(__file__).resolve().parents[2]
SHA = "a" * 40
NODE_ID = "sha256:" + "b" * 64


class VerificationPreflightTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="acropolis-preflight-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "checkout"
        self.root.mkdir()
        for directory in ("scripts", "scripts/tests", "infra", "tests/deploy", "tests/load", "frontend", "bin"):
            (self.root / directory).mkdir(parents=True, exist_ok=True)
        (self.root / "scripts/verify.sh").write_bytes((SOURCE_ROOT / "scripts/verify.sh").read_bytes())
        self.source_paths = ["scripts/verify.sh", "frontend/package.json", "frontend/package-lock.json", "Dockerfile", ".dockerignore", "compose.qa.yml"]
        for relative, content in {
            "frontend/package.json": '{"name":"synthetic-preflight","version":"1.0.0"}\n',
            "frontend/package-lock.json": '{"lockfileVersion":3,"packages":{}}\n',
            "Dockerfile": "FROM synthetic-node\n",
            ".dockerignore": ".local\n",
            "compose.qa.yml": "services: {}\n",
        }.items():
            (self.root / relative).write_text(content)
        boundary_test = (
            "import os\nfrom pathlib import Path\nimport unittest\n"
            "class FixtureBoundary(unittest.TestCase):\n"
            " def test_synthetic_isolated_checkout(self):\n"
            "  self.assertRegex(os.environ['QA_PROJECT'], r'^acropolis_test_[a-z0-9_]+$')\n"
            "  self.assertTrue(Path('compose.qa.yml').is_file())\n"
        )
        for relative in ("tests/deploy/test_deploy.py", "scripts/tests/test_smtp_network.py", "scripts/tests/test_identity_runtime.py"):
            (self.root / relative).write_text(boundary_test)
        cleanup_source = """
            import json,os,re,sys
            from pathlib import Path
            arguments=sys.argv[1:]
            project=arguments[arguments.index('--project')+1]
            if not re.fullmatch(r'acropolis_test_[a-z0-9_]+',project): raise SystemExit(64)
            with Path(os.environ['MOCK_CALLS']).open('a') as log:
                log.write(json.dumps({'boundary':'cleanup','argv':arguments})+'\\n')
            if os.environ.get('MOCK_CLEANUP_FAIL')=='1': raise SystemExit(73)
        """
        diagnostic_source = """
            import json,sys
            from pathlib import Path
            arguments=sys.argv[1:]
            Path(arguments[arguments.index('--output')+1]).write_text(json.dumps({'synthetic':True}))
        """
        (self.root / "scripts/qa-cleanup.py").write_text(textwrap.dedent(cleanup_source))
        (self.root / "scripts/qa-diagnostics.py").write_text(textwrap.dedent(diagnostic_source))
        git_source = """
            import json,os,sys
            arguments=sys.argv[1:]
            if arguments==['branch','--show-current']: print('main')
            elif arguments==['rev-parse','HEAD']: print('a'*40)
            elif arguments==['status','--porcelain']:
                if os.environ.get('MOCK_DIRTY')=='1': print(' M frontend/package.json')
            elif arguments and arguments[0]=='ls-files':
                sys.stdout.write('\\0'.join(json.loads(os.environ['MOCK_SOURCE_PATHS']))+'\\0')
            else: raise SystemExit('Unexpected synthetic git command')
        """
        docker_source = """
            import json,os,sys
            from pathlib import Path
            arguments=sys.argv[1:]
            log_path=Path(os.environ['MOCK_CALLS'])
            previous=[json.loads(line) for line in log_path.read_text().splitlines()] if log_path.exists() else []
            with log_path.open('a') as log:
                log.write(json.dumps({'boundary':'docker','argv':arguments})+'\\n')
            if arguments and arguments[0]=='build':
                if '--target' in arguments:
                    if arguments[arguments.index('--target')+1]!='node': raise SystemExit('Unexpected tools build')
                    if os.environ.get('MOCK_NODE_BUILD_FAIL')=='1': raise SystemExit(71)
                elif os.environ.get('MOCK_STOP_AT_CANDIDATE')=='1': raise SystemExit(72)
                else: raise SystemExit('Unexpected full execution in a fast CLI test')
            elif arguments[:2]==['image','inspect']:
                already_ran=any(row['boundary']=='docker' and row['argv'][0]=='compose' and 'run' in row['argv'] for row in previous)
                if already_ran and os.environ.get('MOCK_AUDIT_MUTATE')=='1':
                    (Path(os.environ['QA_ARTIFACTS'])/'npm-audit.json').write_text('{"changed":true}\\n')
                print('sha256:'+('c' if already_ran and os.environ.get('MOCK_CHANGE_NODE_TAG')=='1' else 'b')*64)
            elif arguments and arguments[0]=='compose':
                if 'run' in arguments:
                    if 'node' not in arguments: raise SystemExit('Unexpected QA service')
                    if os.environ.get('MOCK_PREFLIGHT_FAIL')=='1': raise SystemExit(74)
                    (Path(os.environ['QA_ARTIFACTS'])/'npm-audit.json').write_text('{"metadata":{"vulnerabilities":{"high":0,"critical":0}}}\\n')
                    if os.environ.get('MOCK_SOURCE_MUTATE')=='1':
                        Path('frontend/package.json').write_text('{"name":"changed-after-check"}\\n')
                    if os.environ.get('MOCK_SOURCE_REAPPEAR')=='1':
                        Path('frontend/old-component.ts').write_text('export const restored = true;\\n')
                elif 'config' not in arguments and 'down' not in arguments:
                    raise SystemExit('Unexpected compose effect')
            else: raise SystemExit('Unexpected synthetic Docker command')
        """
        for name, source in (("git", git_source), ("docker", docker_source)):
            executable = self.root / "bin" / name
            executable.write_text("#!" + sys.executable + "\n" + textwrap.dedent(source))
            executable.chmod(0o700)
        self.calls_path = self.root / "calls.jsonl"
        self.environment = {
            **os.environ,
            "PATH": str(self.root / "bin") + os.pathsep + os.environ["PATH"],
            "MOCK_CALLS": str(self.calls_path),
            "MOCK_SOURCE_PATHS": json.dumps(self.source_paths),
        }
        for name in ("QA_SUPERVISOR_NONCE", "MOCK_DIRTY", "MOCK_NODE_BUILD_FAIL", "MOCK_PREFLIGHT_FAIL", "MOCK_SOURCE_MUTATE", "MOCK_SOURCE_REAPPEAR", "MOCK_CHANGE_NODE_TAG", "MOCK_AUDIT_MUTATE", "MOCK_CLEANUP_FAIL", "MOCK_STOP_AT_CANDIDATE"):
            self.environment.pop(name, None)

    def invoke(self, *arguments, **flags):
        return subprocess.run(
            ["bash", "scripts/verify.sh", *arguments],
            cwd=self.root,
            env={**self.environment, **flags},
            text=True,
            capture_output=True,
            timeout=30,
        )

    def calls(self):
        return [json.loads(line) for line in self.calls_path.read_text().splitlines()] if self.calls_path.exists() else []

    def report(self):
        reports = list((self.root / ".local/qa" / SHA).glob("*/report.json"))
        self.assertEqual(len(reports), 1)
        return json.loads(reports[0].read_text())

    def assert_ineligible(self, report):
        self.assertFalse(report["deployment_eligible"])
        self.assertFalse(report["gate_eligible"])
        self.assertFalse(report["supervisor_review_pending"])

    def assert_cleanup_scopes(self):
        ownership = [
            row["argv"][row["argv"].index("--project") + 1]
            for row in self.calls()
            if row["boundary"] == "cleanup" and "--check-ownership" in row["argv"]
        ]
        self.assertEqual(len(ownership), 3)
        self.assertEqual(ownership[1], ownership[0] + "_restore")
        self.assertEqual(ownership[2], ownership[0] + "_integration")

    def test_preflight_checks_current_node_before_any_application_effect_and_stays_ineligible(self):
        result = self.invoke("--preflight")
        self.assertEqual(result.returncode, 0, result.stderr)
        report = self.report()
        self.assertEqual(report["status"], "passed")
        self.assertEqual(report["scope"], "preflight")
        self.assertTrue(report["preflight"])
        self.assertTrue(report["working_tree"])
        self.assertTrue(report["cleanup_complete"])
        self.assertEqual(report["last_stage"], "preflight_complete")
        self.assertEqual(report["sha"], SHA)
        self.assertEqual(report["node_image_id"], NODE_ID)
        self.assertRegex(report["frontend_source_fingerprint"], r"^[a-f0-9]{64}$")
        self.assertRegex(report["npm_audit_sha256"], r"^[a-f0-9]{64}$")
        self.assertEqual(report["image_id"], "")
        self.assertEqual(report["migration_image_id"], "")
        self.assert_ineligible(report)
        docker = [row["argv"] for row in self.calls() if row["boundary"] == "docker"]
        builds = [args for args in docker if args[0] == "build"]
        self.assertEqual(len(builds), 1)
        self.assertEqual(builds[0][builds[0].index("--target") + 1], "node")
        self.assertIn("org.opencontainers.image.revision=" + SHA, builds[0])
        runs = [args for args in docker if args[0] == "compose" and "run" in args]
        self.assertEqual(len(runs), 1)
        self.assertIn("--no-deps", runs[0])
        self.assertIn("NODE_EXTRA_CA_CERTS=", runs[0])
        self.assertEqual(runs[0][runs[0].index("--env-file") + 1], "/dev/null")
        command = runs[0][-1]
        expected = ["npm ci --no-audit --no-fund", "npm run format:check", "npm run typecheck", "npm run lint", "npm audit --audit-level=high --json"]
        positions = [command.index(value) for value in expected]
        self.assertEqual(positions, sorted(positions))
        self.assertNotIn("test:coverage", command)
        self.assertNotIn("npm run build", command)
        self.assertFalse(any("up" in args for args in docker))
        self.assertEqual(report["passed_steps"][-2:], ["frontend_preflight", "frontend_preflight_consistency"])
        self.assert_cleanup_scopes()

    def test_full_modes_run_fast_checks_before_the_first_candidate_build(self):
        for arguments in ((), ("--working-tree",)):
            with self.subTest(arguments=arguments):
                result = self.invoke(*arguments, MOCK_STOP_AT_CANDIDATE="1")
                self.assertNotEqual(result.returncode, 0)
                calls = [row["argv"] for row in self.calls() if row["boundary"] == "docker"]
                node_index = next(index for index, args in enumerate(calls) if args[0] == "build" and "--target" in args)
                preflight_index = next(index for index, args in enumerate(calls) if args[0] == "compose" and "run" in args)
                candidate_index = next(index for index, args in enumerate(calls) if args[0] == "build" and "--target" not in args)
                self.assertLess(node_index, preflight_index)
                self.assertLess(preflight_index, candidate_index)
                report = self.report()
                self.assertEqual(report["status"], "failed")
                self.assertNotIn("scope", report)
                self.assertEqual(report["last_stage"], "build_candidate")
                self.assertIn("frontend_preflight_consistency", report["passed_steps"])
                self.assert_ineligible(report)
                # Use fresh fixture state for the next CLI mode without editing product source.
                if not arguments:
                    self.temporary.cleanup()
                    self.setUp()

    def test_preflight_allows_iteration_but_clean_full_gate_rejects_dirty_checkout(self):
        result = self.invoke(MOCK_DIRTY="1")
        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.calls(), [])
        result = self.invoke("--preflight", MOCK_DIRTY="1")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assert_ineligible(self.report())

    def test_invalid_or_combined_modes_stop_before_external_effects(self):
        for arguments in (("--unknown",), ("--preflight", "--working-tree"), ("--preflight", "unexpected")):
            with self.subTest(arguments=arguments):
                self.assertEqual(self.invoke(*arguments).returncode, 2)
        self.assertEqual(self.calls(), [])
        help_result = self.invoke("--help")
        self.assertEqual(help_result.returncode, 0)
        self.assertIn("--preflight", help_result.stdout)
        self.assertEqual(self.calls(), [])

    def test_node_build_and_fast_check_failures_cleanup_without_starting_later_blocks(self):
        for flag in ("MOCK_NODE_BUILD_FAIL", "MOCK_PREFLIGHT_FAIL"):
            with self.subTest(flag=flag):
                result = self.invoke("--preflight", **{flag: "1"})
                self.assertNotEqual(result.returncode, 0)
                report = self.report()
                self.assertEqual(report["status"], "failed")
                self.assert_ineligible(report)
                self.assertTrue(report["cleanup_complete"])
                self.assertEqual(report["last_stage"], "build_node_runner" if flag == "MOCK_NODE_BUILD_FAIL" else "frontend_preflight")
                self.assert_cleanup_scopes()
                if flag == "MOCK_NODE_BUILD_FAIL":
                    self.temporary.cleanup()
                    self.setUp()

    def test_source_changed_after_checks_is_rejected_with_current_fingerprint(self):
        result = self.invoke("--preflight", MOCK_SOURCE_MUTATE="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("source or quality policy changed", (result.stdout + result.stderr).lower())
        self.assertEqual(self.report()["status"], "failed")
        self.assert_ineligible(self.report())

    def test_current_tracked_deletion_is_recorded_but_reappearance_after_checks_is_rejected(self):
        for reappear in (False, True):
            with self.subTest(reappear=reappear):
                self.source_paths.append("frontend/old-component.ts")
                self.environment["MOCK_SOURCE_PATHS"] = json.dumps(self.source_paths)
                deleted = self.root / "frontend/old-component.ts"
                deleted.write_text("export const obsolete = true;\n")
                deleted.unlink()
                flags = {"MOCK_DIRTY": "1"}
                if reappear:
                    flags["MOCK_SOURCE_REAPPEAR"] = "1"
                result = self.invoke("--preflight", **flags)
                report = self.report()
                if reappear:
                    self.assertNotEqual(result.returncode, 0)
                    self.assertEqual(report["status"], "failed")
                    self.assertEqual(report["last_stage"], "frontend_preflight_consistency")
                    self.assertTrue(deleted.is_file())
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(report["status"], "passed")
                    self.assertFalse(deleted.exists())
                self.assertRegex(report["frontend_source_fingerprint"], r"^[a-f0-9]{64}$")
                self.assert_ineligible(report)
                self.assert_cleanup_scopes()
                if not reappear:
                    self.temporary.cleanup()
                    self.setUp()

    def test_runner_identity_or_audit_evidence_changed_after_checks_is_rejected(self):
        for flag in ("MOCK_CHANGE_NODE_TAG", "MOCK_AUDIT_MUTATE"):
            with self.subTest(flag=flag):
                result = self.invoke("--preflight", **{flag: "1"})
                self.assertNotEqual(result.returncode, 0)
                report = self.report()
                self.assertEqual(report["status"], "failed")
                self.assertEqual(report["last_stage"], "frontend_preflight_consistency")
                self.assert_ineligible(report)
                if flag == "MOCK_CHANGE_NODE_TAG":
                    self.temporary.cleanup()
                    self.setUp()

    def test_cleanup_uncertainty_cannot_make_a_successful_preflight_pass(self):
        result = self.invoke("--preflight", MOCK_CLEANUP_FAIL="1")
        self.assertNotEqual(result.returncode, 0)
        report = self.report()
        self.assertEqual(report["status"], "failed")
        self.assertEqual(report["last_stage"], "cleanup")
        self.assertFalse(report["cleanup_complete"])
        self.assert_ineligible(report)
        self.assert_cleanup_scopes()


if __name__ == "__main__":
    unittest.main()
