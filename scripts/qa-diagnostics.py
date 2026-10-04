#!/usr/bin/env python3
"""Capture bounded, private QA diagnostics without retaining raw application logs.

Readiness is a sample of at most 2000 recent lines per owned web container.
An empty sample does not establish absence of failures; k6 retains the full count.
"""
import argparse
import datetime as dt
import json
import math
import os
from pathlib import Path
import re
import selectors
import secrets
import signal
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
PROJECT_RE = re.compile(r"acropolis_test_[a-z0-9_]{1,100}\Z", re.ASCII)
STAGE_RE = re.compile(r"[a-z][a-z0-9_-]{0,63}\Z", re.ASCII)
ID_RE = re.compile(r"[a-f0-9]{64}\Z", re.ASCII)
TIMESTAMP_RE = re.compile(r"([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\.([0-9]{1,9}))?Z\Z")
CATEGORY = "Acropolis.Api.ReadinessDiagnostics"
EVENT_ID = 1001
REASONS = {"history_mismatch", "timeout", "database_error", "probe_error"}
EXCEPTIONS = {"None", "TimeoutException", "OperationCanceledException", "PostgresException",
              "NpgsqlException", "InvalidOperationException", "Exception"}
STATES = {"active", "idle", "idle in transaction", "idle in transaction (aborted)",
          "fastpath function call", "disabled", "none"}
ERROR_CODES = {
    "invalid_timestamp", "invalid_scope", "invalid_output", "symlink_output", "output_exists",
    "deadline_exceeded", "tool_unavailable", "command_timeout", "output_limit", "command_failed",
    "invalid_numeric_metric", "invalid_container_id", "invalid_container_metadata",
    "ownership_mismatch", "invalid_host_metric", "invalid_cgroup_metric", "invalid_activity_metric",
    "unsafe_readiness_event", "invalid_cursor", "container_count_limit", "counter_reset",
    "database_count_mismatch", "log_tail_limit", "file_unavailable", "invalid_metric", "internal_error",
}
WAIT_TYPES = {"Activity", "BufferPin", "Client", "Extension", "IO", "IPC", "Lock",
              "LWLock", "Timeout", "none"}
INSPECT_TEMPLATE = (
    '{"id":{{json .Id}},"project":{{json (index .Config.Labels "com.docker.compose.project")}},'
    '"service":{{json (index .Config.Labels "com.docker.compose.service")}},'
    '"status":{{json .State.Status}},"running":{{json .State.Running}},'
    '"oom_killed":{{json .State.OOMKilled}},"exit_code":{{json .State.ExitCode}},'
    '"restart_count":{{json .RestartCount}},"limits":{'
    '"nano_cpus":{{json .HostConfig.NanoCpus}},"memory_bytes":{{json .HostConfig.Memory}},'
    '"memory_swap_bytes":{{json .HostConfig.MemorySwap}},"pids":{{json .HostConfig.PidsLimit}},'
    '"cpu_quota":{{json .HostConfig.CpuQuota}},"cpu_period":{{json .HostConfig.CpuPeriod}}}}')
CGROUP_COMMAND = (
    'for field in cpu.stat memory.events memory.current memory.max pids.current; do '
    'printf "[%s]\\n" "$field"; cat "/sys/fs/cgroup/$field" || exit 70; done')
ACTIVITY_SQL = """SELECT COALESCE(json_agg(row_to_json(g)), '[]'::json)
FROM (SELECT COALESCE(state,'none') AS state,
 COALESCE(wait_event_type,'none') AS wait_event_type,
 CASE WHEN wait_event_type='Extension' THEN 'extension'
      WHEN wait_event IS NULL THEN 'none'
      WHEN wait_event IN (SELECT name FROM pg_catalog.pg_wait_events WHERE type<>'Extension')
        THEN wait_event ELSE 'other' END AS wait_event,
 count(*)::integer AS count
 FROM pg_catalog.pg_stat_activity
 WHERE datname=current_database() AND pid<>pg_backend_pid()
 GROUP BY state,wait_event_type,wait_event) g;"""
ACTIVITY_COMMAND = (
    'test "$POSTGRES_DB" = "$1" || exit 64; '
    'PGPASSWORD="$POSTGRES_PASSWORD" PGCONNECT_TIMEOUT=3 '
    'PGOPTIONS="-c statement_timeout=2000 -c lock_timeout=500" '
    'exec psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" '
    '-X -A -t -q -c "$2"')


class DiagnosticError(RuntimeError):
    def __init__(self, code):
        self.code = code if code in ERROR_CODES else "internal_error"
        super().__init__("QA diagnostic operation failed: " + self.code)


def timestamp_ticks(value):
    match = TIMESTAMP_RE.fullmatch(value) if isinstance(value, str) else None
    if not match:
        raise DiagnosticError("invalid_timestamp")
    try:
        whole = dt.datetime.strptime(match[1], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=dt.timezone.utc)
    except ValueError as exc:
        raise DiagnosticError("invalid_timestamp") from exc
    return int(whole.timestamp()) * 1_000_000_000 + int((match[2] or "").ljust(9, "0") or "0")


def utc_now():
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def validate_request(project, output, stage, since):
    if not PROJECT_RE.fullmatch(project) or not STAGE_RE.fullmatch(stage):
        raise DiagnosticError("invalid_scope")
    timestamp_ticks(since)
    output = Path(output)
    allowed = ROOT / ".local/qa"
    if (not output.is_absolute() or output.suffix != ".json"
            or not output.resolve().is_relative_to(allowed.resolve())):
        raise DiagnosticError("invalid_output")
    for path in (output, *output.parents):
        if path.is_symlink():
            raise DiagnosticError("symlink_output")
        if path == ROOT:
            break
    if output.exists():
        raise DiagnosticError("output_exists")
    output.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    # Existing QA directories have permissions required by isolated tool containers.
    # Do not change those permissions; this file and cursor are root-private 600.
    return output


def command(arguments, *, timeout=5, byte_limit=2 * 1024 * 1024, include_stderr=False):
    """Bound stdout/stderr in RAM; error text and command arguments are never surfaced."""
    if timeout <= 0:
        raise DiagnosticError("deadline_exceeded")
    try:
        process = subprocess.Popen(arguments, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, start_new_session=True)
    except OSError as exc:
        raise DiagnosticError("tool_unavailable") from exc
    stdout, stderr = bytearray(), bytearray()
    deadline = time.monotonic() + timeout
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ, stdout)
            selector.register(process.stderr, selectors.EVENT_READ, stderr)
            while selector.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise DiagnosticError("command_timeout")
                for key, _ in selector.select(min(0.2, remaining)):
                    chunk = os.read(key.fileobj.fileno(), 8192)
                    if not chunk:
                        selector.unregister(key.fileobj)
                        continue
                    key.data.extend(chunk)
                    if len(stdout) + len(stderr) > byte_limit:
                        raise DiagnosticError("output_limit")
            process.wait(timeout=max(0.01, deadline - time.monotonic()))
        if process.returncode:
            raise DiagnosticError("command_failed")
        return bytes(stdout + (b"\n" + stderr if include_stderr else b""))
    except subprocess.TimeoutExpired as exc:
        raise DiagnosticError("command_timeout") from exc
    finally:
        if process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass  # It exited between poll and kill; still reap the owned child.
            process.wait()
        process.stdout.close()
        process.stderr.close()


def numeric(value, *, minimum=0):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise DiagnosticError("invalid_numeric_metric")
    if value < minimum or not math.isfinite(value):
        raise DiagnosticError("invalid_numeric_metric")
    return value


def owned_metadata(container, project, invoke=command):
    if not ID_RE.fullmatch(container):

        raise DiagnosticError("invalid_container_id")
    try:
        metadata = json.loads(invoke(["docker", "inspect", "--format", INSPECT_TEMPLATE, container]))
    except (json.JSONDecodeError, UnicodeError) as exc:
        raise DiagnosticError("invalid_container_metadata") from exc
    if (not isinstance(metadata, dict) or metadata.get("id") != container or metadata.get("project") != project
            or not re.fullmatch(r"[a-z][a-z0-9-]{0,31}", str(metadata.get("service", "")), re.ASCII)):
        raise DiagnosticError("ownership_mismatch")
    if metadata.get("status") not in {"created", "running", "paused", "restarting", "removing", "exited", "dead"}:
        raise DiagnosticError("invalid_container_metadata")
    for name in ("running", "oom_killed"):
        if not isinstance(metadata.get(name), bool):
            raise DiagnosticError("invalid_container_metadata")
    for name in ("exit_code", "restart_count"):
        numeric(metadata.get(name))
    limits = metadata.get("limits")
    if not isinstance(limits, dict) or set(limits) != {
            "nano_cpus", "memory_bytes", "memory_swap_bytes", "pids", "cpu_quota", "cpu_period"}:
        raise DiagnosticError("invalid_container_metadata")
    for value in limits.values():
        if value is not None:
            numeric(value, minimum=-1)
    # Never return unknown fields, even if Docker emitted them.
    return {key: metadata[key] for key in
            ("id", "service", "status", "running", "oom_killed", "exit_code", "restart_count", "limits")}


def host_metrics():
    metrics = {"cpu_count": os.cpu_count(), "loadavg": [numeric(value) for value in os.getloadavg()]}
    memory = {}
    for line in Path("/proc/meminfo").read_text().splitlines():
        key, _, rest = line.partition(":")
        if key in {"MemTotal", "MemAvailable", "SwapTotal", "SwapFree", "Dirty", "Writeback"}:
            fields = rest.split()
            if len(fields) != 2 or fields[1] != "kB":
                raise DiagnosticError("invalid_host_metric")
            memory[key + "_bytes"] = numeric(int(fields[0])) * 1024
    pressure = {}
    for resource in ("cpu", "memory", "io"):
        entries = {}
        for line in Path("/proc/pressure/" + resource).read_text().splitlines():
            fields = line.split()
            if fields[0] not in {"some", "full"}:
                raise DiagnosticError("invalid_host_metric")
            values = {}
            for field in fields[1:]:
                key, separator, value = field.partition("=")
                if not separator or key not in {"avg10", "avg60", "avg300", "total"}:
                    raise DiagnosticError("invalid_host_metric")
                values[key] = numeric(int(value) if key == "total" else float(value))
            entries[fields[0]] = values
        pressure[resource] = entries
    metrics.update(memory=memory, pressure=pressure)
    return metrics


def cgroup_metrics(raw):
    sections, active = {}, None
    for line in raw.decode("ascii").splitlines():
        if line.startswith("[") and line.endswith("]"):
            active = line[1:-1]
            if active not in {"cpu.stat", "memory.events", "memory.current", "memory.max", "pids.current"}:
                raise DiagnosticError("invalid_cgroup_metric")
            sections[active] = {}
        elif active:
            fields = line.split()
            if len(fields) == 1 and active in {"memory.current", "memory.max", "pids.current"}:
                sections[active] = None if fields[0] == "max" else numeric(int(fields[0]))
            elif len(fields) == 2:
                allowed = ({"usage_usec", "user_usec", "system_usec", "nr_periods",
                            "nr_throttled", "throttled_usec", "nr_bursts", "burst_usec"}
                           if active == "cpu.stat" else
                           {"low", "high", "max", "oom", "oom_kill", "oom_group_kill", "sock_throttled"})
                if fields[0] in allowed:
                    sections[active][fields[0]] = numeric(int(fields[1]))
            else:
                raise DiagnosticError("invalid_cgroup_metric")
    if (set(sections) != {"cpu.stat", "memory.events", "memory.current", "memory.max", "pids.current"}
            or not isinstance(sections.get("cpu.stat"), dict) or "usage_usec" not in sections["cpu.stat"]
            or not isinstance(sections["memory.events"], dict)
            or not isinstance(sections["memory.current"], int)
            or not isinstance(sections["pids.current"], int)
            or sections["memory.max"] is not None and not isinstance(sections["memory.max"], int)):
        raise DiagnosticError("invalid_cgroup_metric")
    return sections


def activity_groups(raw):
    try:
        rows = json.loads(raw)
    except (json.JSONDecodeError, UnicodeError) as exc:
        raise DiagnosticError("invalid_activity_metric") from exc
    if not isinstance(rows, list) or len(rows) > 300:
        raise DiagnosticError("invalid_activity_metric")
    result = []
    for row in rows:
        if (not isinstance(row, dict) or row.get("state") not in STATES
                or row.get("wait_event_type") not in WAIT_TYPES
                or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,63}", str(row.get("wait_event", "")), re.ASCII)):
            raise DiagnosticError("invalid_activity_metric")
        count = numeric(row.get("count"))
        if not isinstance(count, int):
            raise DiagnosticError("invalid_activity_metric")
        result.append({key: row[key] for key in ("state", "wait_event_type", "wait_event", "count")})
    return result


def readiness_events(raw, after):
    """Select only the known structured event. No raw line, Message or Exception survives."""
    threshold = timestamp_ticks(after)
    latest, latest_ticks = after, threshold
    events, rejected = [], 0
    for line in raw.splitlines():
        if not line:
            continue
        prefix, separator, content = line.partition(b" ")
        try:
            stamp = prefix.decode("ascii")
            ticks = timestamp_ticks(stamp)
        except (UnicodeError, DiagnosticError):
            rejected += 1
            continue
        if ticks > latest_ticks:
            latest, latest_ticks = stamp, ticks
        if ticks <= threshold:
            continue
        if len(content) > 32768 or not separator:
            rejected += 1
            continue
        try:
            entry = json.loads(content)
        except (json.JSONDecodeError, UnicodeError):
            rejected += 1
            continue
        if not isinstance(entry, dict) or entry.get("Category") != CATEGORY or entry.get("EventId") != EVENT_ID:
            continue
        state = entry.get("State")
        if (not isinstance(state, dict) or state.get("Reason") not in REASONS
                or state.get("ExceptionType") not in EXCEPTIONS
                or not re.fullmatch(r"(?:[A-Z0-9]{5})?", str(state.get("SqlState", "")), re.ASCII)):
            raise DiagnosticError("unsafe_readiness_event")
        event = {"timestamp": stamp, "event_id": EVENT_ID, "category": CATEGORY,
                 "reason": state["Reason"], "exception_type": state["ExceptionType"],
                 "sql_state": state.get("SqlState", "")}
        if "ElapsedMilliseconds" in state:
            elapsed = numeric(state["ElapsedMilliseconds"])
            if elapsed > 3_600_000:
                raise DiagnosticError("unsafe_readiness_event")
            event["elapsed_milliseconds"] = elapsed
        events.append(event)
    return {"events": events, "discarded_unstructured_lines": rejected}, latest


def read_cursor(path):
    if not path.exists():
        return {}
    if path.is_symlink() or path.stat().st_uid != os.geteuid() or path.stat().st_mode & 0o777 != 0o600:
        raise DiagnosticError("invalid_cursor")
    try:
        cursor = json.loads(path.read_bytes())
    except (json.JSONDecodeError, UnicodeError) as exc:

        raise DiagnosticError("invalid_cursor") from exc
    if not isinstance(cursor, dict) or len(cursor) > 256:
        raise DiagnosticError("invalid_cursor")
    for container, stamp in cursor.items():
        if not ID_RE.fullmatch(container):
            raise DiagnosticError("invalid_cursor")
        timestamp_ticks(stamp)
    return cursor


def private_json(path, data, *, replace=False):
    if path.is_symlink() or (path.exists() and not replace):
        raise DiagnosticError("output_exists")
    fd, temporary = tempfile.mkstemp(prefix=".qa-diagnostic-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            os.fchmod(handle.fileno(), 0o600)
            json.dump(data, handle, sort_keys=True, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        if replace:
            os.replace(temporary, path)
        else:
            os.link(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def collect(project, output, stage, since, *, timeout=35):
    output = validate_request(project, output, stage, since)
    deadline = time.monotonic() + timeout
    report = {"schema": 1, "project": project, "stage": stage, "captured_at": utc_now(),
              "since": since, "host": {"status": "unavailable"}, "containers": [],
              "database_activity": {"status": "unavailable"}, "readiness": {"status": "unavailable", "complete_stream": False, "tail_limit_per_container": 2000},
              "diagnostic_errors": []}
    def error(scope, failure, container=None):
        entry = {"scope": scope, "code": (failure.code if isinstance(failure, DiagnosticError)
                                         else "file_unavailable" if isinstance(failure, OSError) else "invalid_metric")}
        if container:
            entry["container_id"] = container
        report["diagnostic_errors"].append(entry)
    def invoke(args, **kwargs):
        kwargs["timeout"] = min(kwargs.get("timeout", 5), max(0, deadline - time.monotonic()))
        return command(args, **kwargs)
    try:
        report["host"] = {"status": "available", **host_metrics()}
    except (OSError, ValueError, DiagnosticError) as exc:
        error("host", exc)
    try:
        ids = invoke(["docker", "ps", "-a", "--no-trunc", "--filter",
                      "label=com.docker.compose.project=" + project, "--format", "{{.ID}}"]).decode("ascii").splitlines()
        if len(ids) > 24 or len(set(ids)) != len(ids):
            raise DiagnosticError("container_count_limit")
    except (DiagnosticError, UnicodeError) as exc:
        error("discovery", exc)
        ids = []
    first = {}
    for container in ids:
        try:
            metadata = owned_metadata(container, project, invoke)
            metadata["cgroup"] = {"status": "unavailable"}
            report["containers"].append(metadata)
            if metadata["running"]:
                raw = invoke(["docker", "exec", container, "sh", "-c", CGROUP_COMMAND], timeout=3)
                metrics = cgroup_metrics(raw)
                first[container] = (time.monotonic(), metrics)
                metadata["cgroup"] = {"status": "available", "sample": metrics}
        except (DiagnosticError, ValueError, UnicodeError) as exc:
            error("container", exc, container if ID_RE.fullmatch(container) else None)
    if first and deadline - time.monotonic() > 1:
        time.sleep(0.2)
        for metadata in report["containers"]:
            container = metadata["id"]
            if container not in first:
                continue
            try:
                # Revalidate ownership before every later access after discovery.
                owned_metadata(container, project, invoke)
                sampled = cgroup_metrics(invoke(["docker", "exec", container, "sh", "-c", CGROUP_COMMAND], timeout=3))
                sampled_at = time.monotonic()
                before_at, before = first[container]
                elapsed = sampled_at - before_at
                usage = sampled["cpu.stat"]["usage_usec"] - before["cpu.stat"]["usage_usec"]
                if usage < 0 or elapsed <= 0:
                    raise DiagnosticError("counter_reset")
                metadata["cgroup"].update(sample=sampled, elapsed_seconds=elapsed,
                                          cpu_used_cores=usage / 1_000_000 / elapsed)
                for field in ("nr_throttled", "throttled_usec"):
                    if field in sampled["cpu.stat"] and field in before["cpu.stat"]:
                        delta = sampled["cpu.stat"][field] - before["cpu.stat"][field]
                        metadata["cgroup"][field + "_delta"] = numeric(delta)
            except (DiagnosticError, ValueError, UnicodeError) as exc:
                error("cpu_delta", exc, container)
    dbs = [item for item in report["containers"] if item["service"] == "db" and item["running"]]
    if len(dbs) == 1:
        container = dbs[0]["id"]
        try:
            owned_metadata(container, project, invoke)
            raw = invoke(["docker", "exec", container, "sh", "-c", ACTIVITY_COMMAND,
                          "qa-diagnostics", project, ACTIVITY_SQL], timeout=5)
            report["database_activity"] = {"status": "available", "groups": activity_groups(raw)}
        except (DiagnosticError, ValueError) as exc:
            error("database_activity", exc, container)
    elif len(dbs) > 1:
        error("database_activity", DiagnosticError("database_count_mismatch"))
    cursor_path = output.parent / (".qa-diagnostics-cursor-" + project + ".json")
    try:
        cursor = read_cursor(cursor_path)
    except (DiagnosticError, OSError) as exc:
        error("readiness_cursor", exc)
        cursor = {}
    all_events, discarded, read_containers = [], 0, 0
    for metadata in report["containers"]:
        if metadata["service"] != "web":
            continue
        container = metadata["id"]
        try:
            owned_metadata(container, project, invoke)
            bound = cursor.get(container, since)
            if timestamp_ticks(bound) < timestamp_ticks(since):
                bound = since
            raw = invoke(["docker", "logs", "--timestamps", "--since", bound, "--tail", "2000", container],
                         timeout=4, include_stderr=True)
            if len(raw.splitlines()) >= 2000:
                error("readiness", DiagnosticError("log_tail_limit"), container)
            filtered, latest = readiness_events(raw, bound)
            all_events.extend({"container_id": container, **event} for event in filtered["events"])
            discarded += filtered["discarded_unstructured_lines"]
            cursor[container] = latest
            read_containers += 1
        except (DiagnosticError, ValueError) as exc:
            error("readiness", exc, container)
    if read_containers:
        report["readiness"] = {"status": "available", "events": all_events,
                               "discarded_unstructured_lines": discarded, "tail_limit_per_container": 2000, "complete_stream": False}
    report["status"] = "partial" if report["diagnostic_errors"] else "captured"
    # Publish evidence before advancing the cursor; a failed report write must not
    # cause readiness events to disappear from the next diagnostic attempt.
    private_json(output, report)
    try:
        if read_containers:
            private_json(cursor_path, cursor, replace=True)
    except (DiagnosticError, OSError) as exc:
        error("readiness_cursor", exc)
        report["status"] = "partial"
        private_json(output, report, replace=True)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--stage", required=True)
    parser.add_argument("--since", required=True)

    args = parser.parse_args()
    try:
        report = collect(args.project, args.output, args.stage, args.since)
    except (DiagnosticError, OSError) as exc:
        code = exc.code if isinstance(exc, DiagnosticError) else "file_unavailable"
        print(json.dumps({"status": "failed", "code": code}), file=sys.stderr)
        return 2
    except Exception:
        # Unexpected programming failures are errors, never successful diagnostics.
        # Suppress exception text/traceback because provider output can contain secrets.
        print(json.dumps({"status": "failed", "code": "internal_error"}), file=sys.stderr)
        return 2
    print(json.dumps({"path": str(args.output), "status": report["status"],
                      "containers": len(report["containers"]),
                      "diagnostic_errors": len(report["diagnostic_errors"])}))
    return 1 if report["diagnostic_errors"] else 0


if __name__ == "__main__":
    sys.exit(main())

