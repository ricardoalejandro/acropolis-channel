"""QA diagnostic tests: synthetic providers, no Docker or database access."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "qa_diagnostics", Path(__file__).resolve().parents[2] / "scripts/qa-diagnostics.py")
D = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(D)
A, B = "a" * 64, "b" * 64
PROJECT = "acropolis_test_diagnostics"
SINCE = "2026-10-04T12:00:00Z"
STAMP = "2026-10-04T12:00:01.123456789Z"
SECRET = "SYNTHETIC_SECRET_DO_NOT_RETAIN"
CGROUP = b"""[cpu.stat]
usage_usec 100000
user_usec 60000
system_usec 40000
nr_periods 80
nr_throttled 4
throttled_usec 2000
[memory.events]
low 0
high 0
max 0
oom 0
oom_kill 0
[memory.current]
123456
[memory.max]
268435456
[pids.current]
12
"""


def metadata(container=A, project=PROJECT, service="web"):
    return {"id": container, "project": project, "service": service, "running": True,
            "status": "running", "oom_killed": False, "exit_code": 0, "restart_count": 0,
            "limits": {"nano_cpus": 500000000, "memory_bytes": 268435456,
                       "memory_swap_bytes": -1, "pids": 64, "cpu_quota": 0, "cpu_period": 0}}


def event(state=None, stamp=STAMP, category=D.CATEGORY, event_id=D.EVENT_ID):
    state = state or {"Reason": "database_error", "ExceptionType": "PostgresException",
                      "SqlState": "53300", "ElapsedMilliseconds": 12.5,
                      "Message": SECRET, "{OriginalFormat}": SECRET, "Password": SECRET}
    value = {"Category": category, "EventId": event_id, "State": state,
             "Message": SECRET, "Exception": SECRET, "Scopes": [SECRET]}
    return (stamp + " " + json.dumps(value) + "\n").encode()


class QaDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="qa-diagnostics-unit-")
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        root = patch.object(D, "ROOT", self.root)
        root.start()
        self.addCleanup(root.stop)
        self.output = self.root / ".local/qa/run/diagnostics-before-load.json"

    def test_invalid_projects_stage_paths_and_symlinks_reject_before_access(self):
        requests = [
            ("acropolis-channel", self.output, "before-load"),
            ("acropolis_test_valid; unsafe", self.output, "before-load"),
            (PROJECT, self.output, "../cleanup"),
            (PROJECT, self.root / "outside.json", "before-load"),
            (PROJECT, self.output, "line\nbreak"),
        ]
        with patch.object(D, "command", side_effect=AssertionError("No provider access")):
            for project, output, stage in requests:
                with self.subTest(project=project, stage=stage), self.assertRaises(D.DiagnosticError):
                    D.collect(project, output, stage, SINCE)
        outside = self.root / "outside"
        outside.mkdir()
        (self.root / ".local").mkdir()
        (self.root / ".local/qa").symlink_to(outside, target_is_directory=True)
        with self.assertRaises(D.DiagnosticError):
            D.validate_request(PROJECT, self.output, "before-load", SINCE)

    def test_labels_are_checked_before_metrics_database_or_logs(self):
        commands = []
        def provider(args, **kwargs):
            commands.append(args)
            if args[1] == "ps":
                return (A + "\n").encode()
            if args[1] == "inspect":
                return json.dumps(metadata(project="other_project")).encode()
            raise AssertionError("Read access occurred before ownership validation")
        with patch.object(D, "command", side_effect=provider), patch.object(D, "host_metrics", return_value={}):
            report = D.collect(PROJECT, self.output, "before-load", SINCE)
        self.assertEqual(report["status"], "partial")
        self.assertEqual(report["containers"], [])
        self.assertEqual(report["diagnostic_errors"][0]["code"], "ownership_mismatch")
        self.assertTrue(all(args[1] in {"ps", "inspect"} for args in commands))

    def test_inspect_never_requests_environment_commands_or_health_output(self):
        inspected = metadata()
        inspected["Environment"] = SECRET
        inspected["Cmd"] = SECRET
        inspected["Health"] = SECRET
        calls = []
        def invoke(args):
            calls.append(args)
            return json.dumps(inspected).encode()
        clean = D.owned_metadata(A, PROJECT, invoke)
        self.assertNotIn(SECRET, json.dumps(clean))
        template = calls[0][3]
        self.assertNotIn(".Config.Env", template)
        self.assertNotIn(".Config.Cmd", template)
        self.assertNotIn(".Health", template)
        for bad in ([], {"id": A, "project": PROJECT, "service": "web"}):
            with self.assertRaises(D.DiagnosticError):
                D.owned_metadata(A, PROJECT, lambda args: json.dumps(bad).encode())
        with self.assertRaises(D.DiagnosticError):
            D.owned_metadata("not-a-container", PROJECT, lambda args: self.fail("Cannot access invalid ID"))

    def test_only_whitelisted_readiness_state_survives_and_nanosecond_cursor_deduplicates(self):
        raw = (event() + event(category="Other.Category") + event(event_id=999)
               + event(stamp=SINCE) + (STAMP + " " + SECRET + "\n").encode())
        filtered, latest = D.readiness_events(raw, SINCE)
        self.assertEqual(len(filtered["events"]), 1)
        self.assertEqual(latest, STAMP)
        saved = json.dumps(filtered)
        self.assertNotIn(SECRET, saved)
        self.assertNotIn("Message", saved)
        self.assertNotIn('"Exception":', saved)
        self.assertEqual(filtered["events"][0]["sql_state"], "53300")
        self.assertEqual(filtered["events"][0]["exception_type"], "PostgresException")
        self.assertEqual(filtered["events"][0]["elapsed_milliseconds"], 12.5)
        repeated, _ = D.readiness_events(event(), STAMP)
        self.assertEqual(repeated["events"], [])
        later = "2026-10-04T12:00:01.123456790Z"
        fresh, cursor = D.readiness_events(event(stamp=later), STAMP)
        self.assertEqual(len(fresh["events"]), 1)
        self.assertEqual(cursor, later)

    def test_unsafe_readiness_payload_is_error_not_a_successful_redaction(self):
        baseline = {"Reason": "timeout", "ExceptionType": "TimeoutException", "SqlState": ""}
        cases = [{"Reason": SECRET}, {"ExceptionType": SECRET}, {"SqlState": SECRET},
                 {"SqlState": "5330\n"}, {"ElapsedMilliseconds": -1}, {"ElapsedMilliseconds": float("inf")}]
        for extra in cases:
            with self.subTest(extra=extra), self.assertRaises(D.DiagnosticError) as failed:
                D.readiness_events(event(state=dict(baseline, **extra)), SINCE)
            self.assertNotIn(SECRET, str(failed.exception))
        with self.assertRaises(D.DiagnosticError):
            D.timestamp_ticks("2026-02-30T12:00:00Z")
        self.assertEqual(D.DiagnosticError(SECRET).code, "internal_error")

    def test_activity_keeps_only_aggregate_state_wait_counts(self):
        raw = json.dumps([{"state": "active", "wait_event_type": "Lock",
                           "wait_event": "transactionid", "count": 3,
                           "query": SECRET, "usename": SECRET, "client_addr": SECRET}]).encode()
        groups = D.activity_groups(raw)
        self.assertEqual(groups, [{"state": "active", "wait_event_type": "Lock",
                                   "wait_event": "transactionid", "count": 3}])
        self.assertNotIn(SECRET, json.dumps(groups))
        self.assertNotIn("usename", D.ACTIVITY_SQL)
        self.assertNotIn("client_addr", D.ACTIVITY_SQL)
        self.assertNotIn("query", D.ACTIVITY_SQL.lower())
        self.assertIn('test "$POSTGRES_DB" = "$1"', D.ACTIVITY_COMMAND)
        self.assertIn('PGPASSWORD="$POSTGRES_PASSWORD"', D.ACTIVITY_COMMAND)
        with self.assertRaises(D.DiagnosticError):
            D.activity_groups(json.dumps([{"state": SECRET, "wait_event_type": "Lock",
                                          "wait_event": "transactionid", "count": 3}]).encode())

    def test_cgroup_pressure_throttling_oom_and_limits_are_numeric(self):
        parsed = D.cgroup_metrics(CGROUP)
        self.assertEqual(parsed["cpu.stat"]["nr_throttled"], 4)
        self.assertEqual(parsed["memory.events"]["oom_kill"], 0)
        self.assertEqual(parsed["memory.current"], 123456)
        for raw in (CGROUP.replace(b"usage_usec 100000", b"usage_usec -1"),
                    CGROUP.replace(b"[cpu.stat]", b"[environment]"),
                    CGROUP.replace(b"123456", SECRET.encode())):
            with self.assertRaises((D.DiagnosticError, ValueError)):
                D.cgroup_metrics(raw)

    def provider(self, calls, *, fail_database=False):
        def fake(args, **kwargs):
            calls.append(args)
            if args[1] == "ps":
                return (A + "\n" + B + "\n").encode()
            if args[1] == "inspect":
                container = args[-1]
                return json.dumps(metadata(container, service="web" if container == A else "db")).encode()
            if args[1] == "logs":
                return event()
            if args[1] == "exec":
                if args[-3] == "qa-diagnostics":
                    if fail_database:
                        raise D.DiagnosticError("command_failed")
                    return b'[{"state":"active","wait_event_type":"Lock","wait_event":"transactionid","count":3}]'
                return CGROUP
            raise AssertionError("Unexpected provider access")
        return fake

    def test_private_snapshot_cursor_and_summary_never_retain_sensitive_outputs(self):
        calls = []
        with patch.object(D, "command", side_effect=self.provider(calls)), \
             patch.object(D, "host_metrics", return_value={"loadavg": [1, 2, 3]}):
            first = D.collect(PROJECT, self.output, "before-load", SINCE)
            later_path = self.output.with_name("diagnostics-after-load.json")
            second = D.collect(PROJECT, later_path, "after-load", SINCE)
        self.assertEqual(first["status"], "captured")
        self.assertEqual(first["database_activity"]["status"], "available")
        self.assertEqual(len(first["readiness"]["events"]), 1)
        self.assertFalse(first["readiness"]["complete_stream"])
        self.assertEqual(first["readiness"]["tail_limit_per_container"], 2000)
        self.assertEqual(second["readiness"]["events"], [])
        self.assertEqual(self.output.stat().st_mode & 0o777, 0o600)
        cursor = self.output.parent / (".qa-diagnostics-cursor-" + PROJECT + ".json")
        self.assertEqual(cursor.stat().st_mode & 0o777, 0o600)
        self.assertNotIn(SECRET, self.output.read_text())
        self.assertNotIn(SECRET, later_path.read_text())
        self.assertNotIn(SECRET, cursor.read_text())
        self.assertEqual({args[-1] for args in calls if args[1] == "logs"}, {A})
        self.assertTrue(all(args[2] in {A, B} for args in calls if args[1] == "exec"))
        with self.assertRaises(D.DiagnosticError):
            D.validate_request(PROJECT, self.output, "before-load", SINCE)

    def test_partial_failure_is_explicit_and_does_not_erase_other_evidence(self):
        with patch.object(D, "command", side_effect=self.provider([], fail_database=True)), \
             patch.object(D, "host_metrics", return_value={"loadavg": [1, 2, 3]}):
            report = D.collect(PROJECT, self.output, "cleanup", SINCE)
        self.assertEqual(report["status"], "partial")
        self.assertEqual(report["database_activity"]["status"], "unavailable")
        self.assertEqual(report["readiness"]["status"], "available")
        self.assertIn({"scope": "database_activity", "code": "command_failed", "container_id": B},
                      report["diagnostic_errors"])

    def test_empty_exact_project_never_falls_back_to_restore_or_production(self):
        calls = []
        def provider(args, **kwargs):
            calls.append(args)
            return b""
        with patch.object(D, "command", side_effect=provider), patch.object(D, "host_metrics", return_value={}):
            report = D.collect(PROJECT, self.output, "cleanup", SINCE)
        self.assertEqual(report["containers"], [])
        self.assertEqual(report["readiness"]["status"], "unavailable")
        self.assertEqual(len(calls), 1)
        self.assertIn("label=com.docker.compose.project=" + PROJECT, calls[0])
        self.assertNotIn("restore", json.dumps(calls))

    def test_failed_report_write_never_advances_readiness_cursor(self):
        written = []
        def fail_output(path, data, **kwargs):
            written.append(path)
            if path == self.output:
                raise OSError(SECRET)
            self.fail("Cursor advanced before evidence publication")
        with patch.object(D, "command", side_effect=self.provider([])), \
             patch.object(D, "host_metrics", return_value={}), \
             patch.object(D, "private_json", side_effect=fail_output):
            with self.assertRaises(OSError):
                D.collect(PROJECT, self.output, "cleanup", SINCE)
        self.assertEqual(written, [self.output])

    def test_log_tail_sampling_limit_is_explicit_not_success(self):
        original = self.provider([])
        limited = (STAMP + ' {"Category":"Other.Category","Message":"' + SECRET + '"}\n').encode() * 2000
        def provider(args, **kwargs):
            return limited if args[1] == "logs" else original(args, **kwargs)
        with patch.object(D, "command", side_effect=provider), \
             patch.object(D, "host_metrics", return_value={}):
            report = D.collect(PROJECT, self.output, "cleanup", SINCE)
        self.assertEqual(report["status"], "partial")
        self.assertIn({"scope": "readiness", "code": "log_tail_limit", "container_id": A},
                      report["diagnostic_errors"])
        self.assertEqual(report["readiness"]["events"], [])
        self.assertFalse(report["readiness"]["complete_stream"])
        self.assertNotIn(SECRET, self.output.read_text())

    def test_bounded_command_failures_never_surface_stdout_stderr_or_args(self):
        with self.assertRaises(D.DiagnosticError) as failure:
            D.command([sys.executable, "-c",
                       "import sys;sys.stdout.write('" + SECRET + "');sys.stderr.write('" + SECRET + "');sys.exit(7)"])
        self.assertEqual(failure.exception.code, "command_failed")
        self.assertNotIn(SECRET, str(failure.exception))
        with self.assertRaises(D.DiagnosticError) as failure:
            D.command([sys.executable, "-c", "print('x'*10000)"], byte_limit=1024)
        self.assertEqual(failure.exception.code, "output_limit")
        with self.assertRaises(D.DiagnosticError) as failure:
            D.command([sys.executable, "-c", "import time;time.sleep(1)"], timeout=0.05)
        self.assertEqual(failure.exception.code, "command_timeout")


if __name__ == "__main__":
    unittest.main()

