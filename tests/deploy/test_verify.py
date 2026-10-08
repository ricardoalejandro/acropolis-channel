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
        (self.root / "scripts/verify-frontend.sh").write_bytes((SOURCE_ROOT / "scripts/verify-frontend.sh").read_bytes())
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
        for arguments in (("--unknown",), ("--preflight", "--working-tree"), ("--preflight", "unexpected"), ("--frontend-low-risk", "--preflight"), ("--frontend-low-risk", "unexpected")):
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



class FrontendScopedCertificateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import importlib.util
        spec = importlib.util.spec_from_file_location('frontend_release_tests', SOURCE_ROOT / 'scripts/frontend-release.py')
        cls.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.module)

    def setUp(self):
        from unittest.mock import patch
        self.patch = patch
        self.temporary = tempfile.TemporaryDirectory(prefix='acropolis-scoped-contract-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.folder = self.root / '.local/qa' / SHA / 'synthetic'
        self.folder.mkdir(parents=True)
        self.base_id = 'sha256:' + 'd' * 64
        self.web_id = 'sha256:' + 'e' * 64
        self.migration_id = 'sha256:' + 'f' * 64
        self.proof = {'scope':self.module.SCOPE,'source_sha':SHA,'active_web_image_id':self.base_id,
                      'inherited_backend':{'sha':'c'*40,'migration_image_id':self.migration_id}}
        self.proof_path = self.folder / 'scope-proof.json'
        self.proof_path.write_text(json.dumps(self.proof))
        self.files_path = self.folder / 'frontend-files.sha256'
        self.files_path.write_text('synthetic frontend fingerprint\n')
        self.report = {'scope':self.module.SCOPE,'sha':SHA,'image_id':self.web_id,
                       'migration_image_id':self.migration_id,'migration_source_sha':'c'*40,
                       'status':'passed','deployment_eligible':True,'gate_eligible':True,
                       'cleanup_complete':True,'working_tree':False,'supervisor_review_pending':False,
                       'last_stage':'complete','passed_steps':sorted(self.module.FRESH_STEPS),
                       'scope_proof_path':str(self.proof_path),'scope_proof_sha256':self.module.digest(self.proof_path),
                       'inherited_backend':self.proof['inherited_backend'],
                       'candidate_proof':{'image_id':self.web_id,'runtime_config_preserved':True,
                                          'runtime_layers_inherited':True,'backend_tree_sha256':'a'*64,
                                          'frontend_files_sha256':self.module.digest(self.files_path)}}
        self.base = {'Config':{'User':'app','Entrypoint':['dotnet','Acropolis.Api.dll'],
                               'Env':['DOTNET_EnableDiagnostics=0'],'Labels':{'org.opencontainers.image.revision':'c'*40}},
                     'RootFS':{'Layers':['certified-layer']}}
        self.candidate = json.loads(json.dumps(self.base))
        self.candidate['Config']['Labels']['org.opencontainers.image.revision'] = SHA
        self.candidate['RootFS']['Layers'].append('frontend-layer')

    def validate(self):
        with self.patch.object(self.module,'admit',return_value=self.proof), self.patch.object(
                self.module,'image_info',side_effect=lambda image,root: self.base if image==self.base_id else self.candidate):
            return self.module.validate_certificate(self.root,self.report,SHA,self.web_id,self.migration_id)

    def test_certificate_keeps_backend_and_migration_source_separate_from_new_frontend_sha(self):
        self.assertEqual(self.validate(), self.proof)
        self.assertNotEqual(self.report['migration_source_sha'],self.report['sha'])

    def test_failed_unfinished_or_uncertain_cleanup_never_certifies(self):
        for field,value in [('status','failed'),('cleanup_complete',False),('working_tree',True),
                            ('deployment_eligible',False),('gate_eligible',False),('last_stage','editor_mobile'),
                            ('supervisor_review_pending',True)]:
            with self.subTest(field=field):
                previous=self.report[field]; self.report[field]=value
                with self.assertRaises(ValueError): self.validate()
                self.report[field]=previous

    def test_missing_or_duplicate_fresh_workflow_never_certifies(self):
        original=self.report['passed_steps']
        for value in [original[:-1],original+['editor_mobile']]:
            self.report['passed_steps']=value
            with self.assertRaises(ValueError):self.validate()

    def test_changed_scope_or_frontend_manifest_rejects_certificate(self):
        self.proof_path.write_text('{}')
        with self.assertRaisesRegex(ValueError,'admission proof changed'):self.validate()
        self.proof_path.write_text(json.dumps(self.proof))
        self.files_path.write_text('changed')
        with self.assertRaisesRegex(ValueError,'artifact fingerprint changed'):self.validate()

    def test_wrong_new_image_or_inherited_migration_is_rejected(self):
        for field in ('image_id','migration_image_id','migration_source_sha'):
            previous=self.report[field];self.report[field]='invalid'
            with self.subTest(field=field),self.assertRaises(ValueError):self.validate()
            self.report[field]=previous

    def test_runtime_config_and_backend_layers_must_remain_identical(self):
        self.module.compatible_images(self.base,self.candidate)
        for field,value in [('User','root'),('Env',['TLS_DISABLED=true']),('Entrypoint',['sh'])]:
            old=self.candidate['Config'][field]; self.candidate['Config'][field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):self.module.compatible_images(self.base,self.candidate)
            self.candidate['Config'][field]=old
        self.candidate['RootFS']['Layers'][0]='other-backend'
        with self.assertRaisesRegex(ValueError,'runtime layers'):self.module.compatible_images(self.base,self.candidate)

    def test_allowlist_rejects_backend_identity_dependencies_and_deleted_editor(self):
        for row in ['M\tsrc/Acropolis.Api/Program.cs','M\tfrontend/package-lock.json',
                    'M\tfrontend/src/features/identity/Accounts.tsx', 'D\t'+self.module.EDITOR]:
            with self.subTest(row=row),self.patch.object(self.module,'command',side_effect=['',row]):
                with self.assertRaises(ValueError):self.module.changed_paths(self.root,'c'*40,SHA)
        with self.patch.object(self.module,'command',side_effect=['','M\t'+self.module.EDITOR]):
            self.assertEqual(self.module.changed_paths(self.root,'c'*40,SHA),[self.module.EDITOR])

    def test_private_artifacts_cannot_escape_or_use_symlinks(self):
        outside=self.root/'external.json';outside.write_text('{}')
        with self.assertRaises(ValueError):self.module.private_path(self.root,outside)
        link=self.folder/'linked.json';link.symlink_to(self.proof_path)
        with self.assertRaises(ValueError):self.module.private_path(self.root,link)

    def test_scoped_runner_has_fresh_editor_and_full_frontend_but_no_unrelated_volume_work(self):
        source=(SOURCE_ROOT/'scripts/verify-frontend.sh').read_text()
        for expected in ('npm run format:check','npm run typecheck','npm run lint','npm audit --audit-level=high',
                         'npm run test:coverage','npm run build','qa-seed --count 200',
                         'desktop-chromium','mobile-chromium','--trace=off','backend_equivalence'):
            self.assertIn(expected,source)
        for forbidden in ('qa-seed --count 100000','qa-seed-catalog','k6 run','dotnet test','restore-db-test.sh','build_migrations'):
            self.assertNotIn(forbidden,source)
        self.assertIn('docker commit --change "LABEL org.opencontainers.image.revision=$sha"',source)
        self.assertNotIn('--entrypoint',source[source.index('build_frontend_candidate()'):source.index('run_step build_candidate')])

if __name__ == "__main__":
    unittest.main()
