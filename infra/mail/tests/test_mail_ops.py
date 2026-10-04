#!/usr/bin/env python3
"""Isolated mail operations tests: temporary CA/ACME/files and mocked Docker/DNS."""
import base64
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import socket
import subprocess
import tarfile
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("mail_ops", Path(__file__).parents[1] / "scripts/mail-ops.py")
ops = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ops)


class MailOpsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workspace = tempfile.TemporaryDirectory(prefix="naperu-mail-tests-")
        cls.pki = Path(cls.workspace.name)
        def command(*args):
            subprocess.run(["openssl", *args], check=True, stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL)
        cls.command = staticmethod(command)
        command("req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256",
                "-nodes", "-days", "30", "-subj", "/CN=Synthetic Mail Ops CA",
                "-addext", "basicConstraints=critical,CA:TRUE",
                "-addext", "keyUsage=critical,keyCertSign,cRLSign",
                "-keyout", str(cls.pki / "ca.key"), "-out", str(cls.pki / "ca.pem"))
        cls.cert, cls.key = cls.make_cert(ops.HOST, "mail")

    @classmethod
    def make_cert(cls, host, name, days="30"):
        cls.command("req", "-new", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256",
                    "-nodes", "-subj", "/CN=" + host, "-keyout", str(cls.pki / (name + ".key")),
                    "-out", str(cls.pki / (name + ".csr")))
        extension = cls.pki / (name + ".ext")
        extension.write_text("subjectAltName=DNS:" + host + "\nbasicConstraints=CA:FALSE\n"
                             "keyUsage=digitalSignature\nextendedKeyUsage=serverAuth\n")
        cls.command("x509", "-req", "-in", str(cls.pki / (name + ".csr")),
                    "-CA", str(cls.pki / "ca.pem"), "-CAkey", str(cls.pki / "ca.key"),
                    "-CAcreateserial", "-days", days, "-extfile", str(extension),
                    "-out", str(cls.pki / (name + ".pem")))
        return ((cls.pki / (name + ".pem")).read_bytes() + (cls.pki / "ca.pem").read_bytes(),
                (cls.pki / (name + ".key")).read_bytes())

    @classmethod
    def tearDownClass(cls):
        cls.workspace.cleanup()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="naperu-mail-case-")
        self.root = Path(self.temp.name)
        self.acme = self.root / "dummy-acme.json"
        actual_run, actual_popen = subprocess.run, subprocess.Popen
        def isolated_run(args, *a, **kwargs):
            if args[0] in ("docker", "dig"):
                raise AssertionError("An isolated test attempted real Docker or DNS access.")
            return actual_run(args, *a, **kwargs)
        def isolated_popen(args, *a, **kwargs):
            if args[0] in ("docker", "dig"):
                raise AssertionError("An isolated test attempted real Docker or DNS access.")
            return actual_popen(args, *a, **kwargs)
        run_guard = mock.patch.object(ops.subprocess, "run", side_effect=isolated_run)
        popen_guard = mock.patch.object(ops.subprocess, "Popen", side_effect=isolated_popen)
        run_guard.start()
        popen_guard.start()
        self.addCleanup(run_guard.stop)
        self.addCleanup(popen_guard.stop)

    def tearDown(self):
        self.temp.cleanup()

    def write_acme(self, cert=None, key=None, extra=None):
        entry = {"domain": {"main": ops.HOST, "sans": []},
                 "certificate": base64.b64encode(cert or self.cert).decode(),
                 "key": base64.b64encode(key or self.key).decode()}
        data = {"letsencrypt": {"Certificates": [entry]}}
        if extra:
            data.update(extra)
        self.acme.write_text(json.dumps(data))
        return data

    def export(self, **kwargs):
        return ops.export_cert(self.root, self.acme, cafile=self.pki / "ca.pem", inspect=False, **kwargs)

    def snapshot_source(self):
        for component in ops.COMPONENTS:
            path = self.root / component
            if "." in path.name:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("synthetic-" + component)
                path.chmod(0o600)
            else:
                path.mkdir()
                (path / "sample").write_bytes(b"mail sample; no credentials\n")
        # Reproduce actual atomic certificate symlink layout.
        certs = self.root / "certs"
        (certs / "versions/stage").mkdir(parents=True)
        for name, content in (("fullchain.pem", self.cert), ("privkey.pem", self.key)):
            (certs / "versions/stage" / name).write_bytes(content)
            (certs / "versions/stage" / name).chmod(0o600)
            (certs / name).symlink_to("current/" + name)
        (certs / "current").symlink_to("versions/stage")
        (self.root / ".local").chmod(0o700)
        ops.init_key(self.root)

    def backup(self):
        with mock.patch.object(ops, "container_state", return_value=False):
            result = ops.backup(self.root, offline=True)
        return Path(result["path"]), self.root / ".local/backup.key"

    def test_selects_only_exact_primary_from_exact_resolver_and_noop(self):
        unrelated_cert, unrelated_key = self.make_cert("other.invalid", "other")
        self.write_acme(extra={"otherresolver": {"Certificates": [{
            "domain": {"main": ops.HOST},
            "certificate": base64.b64encode(unrelated_cert).decode(),
            "key": base64.b64encode(unrelated_key).decode()}]}})
        first = self.export()
        self.assertTrue(first["changed"])
        current = os.readlink(self.root / "certs/current")
        self.assertEqual((self.root / "certs/fullchain.pem").read_bytes(), self.cert)
        self.assertEqual(stat.S_IMODE((self.root / "certs/privkey.pem").stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE((self.root / "certs/versions").stat().st_mode), 0o700)
        self.assertEqual(self.export(), {"changed": False, "reloaded": False})
        self.assertEqual(os.readlink(self.root / "certs/current"), current)
        self.assertEqual(len(list((self.root / "certs/versions").iterdir())), 1)

    def test_partial_json_wrong_resolver_and_ambiguous_selection_preserve_current(self):
        data = self.write_acme()
        self.export()
        current = os.readlink(self.root / "certs/current")
        invalid = ("{", json.dumps({"otherresolver": data["letsencrypt"]}),
                   json.dumps({"letsencrypt": {"Certificates": data["letsencrypt"]["Certificates"] * 2}}),
                   json.dumps({"letsencrypt": {"Certificates": [{"domain": {
                       "main": "other.invalid", "sans": [ops.HOST]}}]}}))
        for content in invalid:
            self.acme.write_text(content)
            with self.assertRaises(ops.OpsError):
                self.export()
            self.assertEqual(os.readlink(self.root / "certs/current"), current)

    def test_bad_san_key_trust_and_expiry_preserve_current(self):
        self.write_acme()
        self.export()
        before = os.readlink(self.root / "certs/current")
        other_cert, other_key = self.make_cert("wrong.invalid", "wrong")
        short_cert, short_key = self.make_cert(ops.HOST, "short", days="1")
        for cert, key in ((other_cert, other_key), (self.cert, other_key), (short_cert, short_key)):
            self.write_acme(cert=cert, key=key)
            with self.assertRaises(ops.OpsError):
                self.export()
            self.assertEqual(os.readlink(self.root / "certs/current"), before)
        self.write_acme()
        with self.assertRaises(ops.OpsError):
            ops.export_cert(self.root, self.acme, inspect=False)  # Synthetic CA is not system trusted.

        self.assertEqual(os.readlink(self.root / "certs/current"), before)

    def test_only_changed_certificate_reloads_and_only_expected_container(self):
        self.write_acme()
        expected = ops.certificate_fingerprint(self.cert)
        actual_run = ops.run
        def runner(args, **kwargs):
            return b"" if args[0] == "docker" else actual_run(args, **kwargs)
        with mock.patch.object(ops, "container_state", return_value=True), \
             mock.patch.object(ops, "run", side_effect=runner) as commands, \
             mock.patch.object(ops, "copied_certificate_fingerprint", return_value=expected), \
             mock.patch.object(ops, "served_certificate_fingerprint", return_value=expected), \
             mock.patch.object(ops, "mail_tls_endpoint", return_value=("127.0.0.1", 2465)):
            result = ops.export_cert(self.root, self.acme, cafile=self.pki / "ca.pem")
            self.assertTrue(result["serving_verified"])
            calls = [call.args[0] for call in commands.call_args_list if call.args[0][0] == "docker"]
            self.assertEqual(calls, [["docker", "exec", ops.CONTAINER, "postfix", "reload"],
                                     ["docker", "exec", ops.CONTAINER, "doveadm", "reload"]])
            commands.reset_mock()
            ops.export_cert(self.root, self.acme, cafile=self.pki / "ca.pem")
            self.assertFalse(any(call.args[0][0] == "docker" for call in commands.call_args_list))
        labels = {"com.docker.compose.project": "other", "com.docker.compose.service": "mailserver"}
        inspected = subprocess.CompletedProcess([], 0, (
            json.dumps(labels) + "\ntrue\n").encode(), b"")
        with mock.patch.object(ops.subprocess, "run", return_value=inspected), \
             mock.patch.object(ops, "run") as commands:
            with self.assertRaises(ops.OpsError):
                ops.container_state(ops.CONTAINER)
            commands.assert_not_called()  # Never inspect env of an unrelated container.
        labels["com.docker.compose.project"] = "naperu-mail"
        inspected.stdout = (json.dumps(labels) + "\ntrue\n").encode()
        with mock.patch.object(ops.subprocess, "run", return_value=inspected), \
             mock.patch.object(ops, "run", return_value=b""):
            with self.assertRaises(ops.OpsError):
                ops.container_state(ops.CONTAINER)  # Manual SSL paths are mandatory.

    @contextlib.contextmanager
    def fake_clock(self):
        now = [0]
        def sleep(seconds):
            now[0] += seconds
        with mock.patch.object(ops.time, "monotonic", side_effect=lambda: now[0]), \
             mock.patch.object(ops.time, "sleep", side_effect=sleep):
            yield now

    def test_reload_waits_for_dms_public_copy_and_actual_served_certificate(self):
        events = []
        copies = iter(("old", "old", "expected"))
        served = iter(("old", "expected"))
        def copied(**kwargs):
            value = next(copies)
            events.append("copy:" + value)
            return value
        def runner(args, **kwargs):
            self.assertIn("copy:expected", events)  # Reload cannot precede DMS copy.
            events.append(args[-2])
            return b""
        with self.fake_clock(), \
             mock.patch.object(ops, "container_state", return_value=True), \
             mock.patch.object(ops, "copied_certificate_fingerprint", side_effect=copied), \
             mock.patch.object(ops, "served_certificate_fingerprint", side_effect=lambda *a, **kw: next(served)), \
             mock.patch.object(ops, "mail_tls_endpoint", return_value=("127.0.0.1", 2465)), \
             mock.patch.object(ops, "run", side_effect=runner):
            self.assertTrue(ops.reload_mail("expected", timeout=10, interval=2))
        self.assertEqual(events[:3], ["copy:old", "copy:old", "copy:expected"])
        self.assertEqual(events[3:], ["postfix", "doveadm"])

    def test_pending_copy_timeout_never_reloads_and_served_timeout_is_failure(self):
        with self.fake_clock(), \
             mock.patch.object(ops, "container_state", return_value=True), \
             mock.patch.object(ops, "copied_certificate_fingerprint", return_value="old"), \
             mock.patch.object(ops, "run") as runner, \
             mock.patch.object(ops, "served_certificate_fingerprint") as served:
            with self.assertRaises(ops.OpsError):
                ops.reload_mail("expected", timeout=6, interval=2)
            runner.assert_not_called()
            served.assert_not_called()
        with self.fake_clock(), \
             mock.patch.object(ops, "container_state", return_value=True), \
             mock.patch.object(ops, "copied_certificate_fingerprint", return_value="expected"), \
             mock.patch.object(ops, "served_certificate_fingerprint", return_value="old"), \
             mock.patch.object(ops, "mail_tls_endpoint", return_value=("127.0.0.1", 2465)), \
             mock.patch.object(ops, "run", return_value=b""):
            with self.assertRaises(ops.OpsError):
                ops.reload_mail("expected", timeout=6, interval=2)

    def test_failed_export_preserves_new_version_records_failure_and_can_retry(self):
        self.write_acme()
        with mock.patch.object(ops, "container_state", return_value=True), \
             mock.patch.object(ops, "reload_mail", side_effect=ops.OpsError("synthetic timeout")):
            with self.assertRaises(ops.OpsError):
                ops.export_cert(self.root, self.acme, cafile=self.pki / "ca.pem")
        current = os.readlink(self.root / "certs/current")
        self.assertEqual((self.root / "certs/fullchain.pem").read_bytes(), self.cert)
        statefile = self.root / ".local/cert-export-state.json"
        state = json.loads(statefile.read_bytes())
        self.assertEqual(state["status"], "failed")
        self.assertFalse(state["serving_verified"])
        self.assertEqual(stat.S_IMODE(statefile.stat().st_mode), 0o600)
        self.assertNotIn(b"PRIVATE KEY", statefile.read_bytes())
        with mock.patch.object(ops, "container_state", return_value=True), \
             mock.patch.object(ops, "reload_mail", return_value=True):
            result = ops.export_cert(self.root, self.acme, cafile=self.pki / "ca.pem")
        self.assertEqual(result, {"changed": False, "reloaded": True, "serving_verified": True})
        self.assertEqual(os.readlink(self.root / "certs/current"), current)
        self.assertEqual(len(list((self.root / "certs/versions").iterdir())), 1)
        self.assertEqual(json.loads(statefile.read_bytes())["status"], "passed")

    def test_mail_tls_endpoint_allows_only_own_stage_or_exact_public_binding(self):
        valid = (("127.0.0.1", 2465, ("127.0.0.1", 2465)),
                 ("0.0.0.0", 465, ("127.0.0.1", 465)),
                 (ops.IP, 465, (ops.IP, 465)))
        for address, port, endpoint in valid:
            payload = json.dumps([{"HostIp": address, "HostPort": str(port)}]).encode()
            with mock.patch.object(ops, "container_state", return_value=True), \
                 mock.patch.object(ops, "run", return_value=payload):
                self.assertEqual(ops.mail_tls_endpoint(), endpoint)
                self.assertEqual(ops.loopback_tls_port(), port)
        for bindings in (None, [], [{"HostIp": "192.0.2.1", "HostPort": "465"}],
                         [{"HostIp": "127.0.0.1", "HostPort": "444"}],
                         [{"HostIp": ops.IP, "HostPort": "2465"}],
                         [{"HostIp": "0.0.0.0", "HostPort": "2465"}],
                         [{"HostIp": "127.0.0.1", "HostPort": "465"},
                          {"HostIp": "127.0.0.1", "HostPort": "2465"}]):
            with mock.patch.object(ops, "container_state", return_value=True), \
                 mock.patch.object(ops, "run", return_value=json.dumps(bindings).encode()):
                with self.assertRaises(ops.OpsError):
                    ops.mail_tls_endpoint()

        with mock.patch.object(ops, "container_state", return_value=False), \
             mock.patch.object(ops, "run") as runner:
            with self.assertRaises(ops.OpsError):
                ops.mail_tls_endpoint()
            runner.assert_not_called()

    def test_public_bound_certificate_check_uses_exact_endpoint_sni_and_trust(self):
        secure = mock.MagicMock()
        secure.getpeercert.return_value = b"synthetic certificate DER"
        context = mock.MagicMock()
        context.wrap_socket.return_value.__enter__.return_value = secure
        with mock.patch.object(ops.ssl, "create_default_context", return_value=context) as factory, \
             mock.patch.object(ops.socket, "create_connection") as connect:
            expected = ops.hashlib.sha256(b"synthetic certificate DER").hexdigest()
            self.assertEqual(ops.served_certificate_fingerprint((ops.IP, 465)), expected)
            connect.assert_called_once_with((ops.IP, 465), timeout=10)
            factory.assert_called_once_with(cafile=None)
            self.assertEqual(context.wrap_socket.call_args.kwargs["server_hostname"], ops.HOST)
        with self.assertRaises(ops.OpsError):
            ops.served_certificate_fingerprint(("192.0.2.1", 465))

    def test_restore_preserves_uid_gid_of_mail_webmail_and_certificate_symlinks(self):
        self.snapshot_source()
        identities = {"mail-data": (5000, 5000), "roundcube-db": (33, 33)}
        if os.geteuid() != 0:
            identities = {name: (os.geteuid(), os.getegid()) for name in identities}
        for component, (uid, gid) in identities.items():
            os.chown(self.root / component, uid, gid)
            os.chown(self.root / component / "sample", uid, gid)
        link_identity = (33, 33) if os.geteuid() == 0 else (os.geteuid(), os.getegid())
        os.chown(self.root / "certs/current", *link_identity, follow_symlinks=False)
        backup, key = self.backup()
        destination = self.root / ".local/restore-tests/ownership"
        self.assertTrue(ops.restore_test(backup, key, destination, self.root)["verified"])
        for component, identity in identities.items():
            for path in (destination / component, destination / component / "sample"):
                self.assertEqual((path.stat().st_uid, path.stat().st_gid), identity)
        info = (destination / "certs/current").lstat()
        self.assertEqual((info.st_uid, info.st_gid), link_identity)
        self.assertEqual(ops.inventory(destination), ops.inventory(self.root))
        with ops.decrypted_archive(backup, key) as archive:
            manifest, _ = ops.checked_archive(archive)
            self.assertEqual(manifest["schema"], 2)
            manifest["entries"][0]["uid"] ^= 1
            changed = json.dumps(manifest).encode()
            original = archive.extractfile
            archive.extractfile = lambda member: io.BytesIO(changed) if member.name == "MANIFEST.json" else original(member)
            with self.assertRaises(ops.OpsError):
                ops.checked_archive(archive)

    def test_encrypted_backup_roundtrip_excludes_key_and_preserves_live_data(self):
        self.snapshot_source()
        backup, key = self.backup()
        self.assertEqual(stat.S_IMODE(backup.stat().st_mode), 0o600)
        self.assertNotIn(b"mail sample", backup.read_bytes())
        with ops.decrypted_archive(backup, key) as archive:
            self.assertNotIn(".local/backup.key", archive.getnames())
            self.assertIn(".local/credentials.json", archive.getnames())
            self.assertIn("mail-state/sample", archive.getnames())
        result = ops.verify_backup(backup, key)
        self.assertTrue(result["verified"])
        destination = self.root / ".local/restore-tests/check"
        self.assertTrue(ops.restore_test(backup, key, destination, self.root)["verified"])
        self.assertEqual((destination / "mail-data/sample").read_bytes(), b"mail sample; no credentials\n")
        self.assertEqual((self.root / "mail-data/sample").read_bytes(), b"mail sample; no credentials\n")
        self.assertEqual((destination / "certs/privkey.pem").read_bytes(), self.key)
        self.assertEqual(os.readlink(destination / "certs/current"), "versions/stage")
        self.assertFalse((destination / ".local/backup.key").exists())
        sidecar = json.loads(backup.with_suffix("").with_suffix(".json").read_text())
        self.assertTrue(sidecar["verified"])
        self.assertEqual(sidecar["encrypted_sha256"], ops.sha_file(backup))
        self.assertNotIn("entries", sidecar)

    def test_wrong_key_tamper_and_insecure_key_rejected(self):
        self.snapshot_source()
        backup, key = self.backup()
        wrong = self.root / "wrong.key"
        wrong.write_text("synthetic wrong key\n")
        wrong.chmod(0o600)
        with self.assertRaises(ops.OpsError):
            ops.verify_backup(backup, wrong)
        damaged = self.root / "damaged.gpg"
        content = bytearray(backup.read_bytes())
        content[len(content) // 2] ^= 1
        damaged.write_bytes(content)
        damaged.chmod(0o600)
        with self.assertRaises(ops.OpsError):
            ops.verify_backup(damaged, key)
        key.chmod(0o644)
        with self.assertRaises(ops.OpsError):
            ops.verify_backup(backup, key)

    def test_restore_refuses_live_existing_and_symlink_destinations(self):
        self.snapshot_source()
        backup, key = self.backup()
        live = self.root / "mail-data"
        with self.assertRaises(ops.OpsError):
            ops.restore_test(backup, key, live, self.root)
        destination = self.root / ".local/restore-tests/existing"
        destination.mkdir()
        with self.assertRaises(ops.OpsError):
            ops.restore_test(backup, key, destination, self.root)
        alias = self.root / ".local/restore-tests/alias"
        alias.symlink_to(self.root / "mail-data", target_is_directory=True)
        with self.assertRaises(ops.OpsError):
            ops.restore_test(backup, key, alias, self.root)

    def test_unsafe_archive_paths_links_and_manifest_changes_rejected(self):
        for name, kind, link in (("../escape", "file", ""), ("/absolute", "file", ""),
                                 ("certs/alias", "link", "/etc/passwd"),
                                 ("certs/hard", "hard", "config/sample")):
            stream = io.BytesIO()
            with tarfile.open(fileobj=stream, mode="w") as archive:
                member = tarfile.TarInfo(name)
                if kind != "file":
                    member.type = tarfile.SYMTYPE if kind == "link" else tarfile.LNKTYPE
                    member.linkname = link
                archive.addfile(member)
            stream.seek(0)
            with tarfile.open(fileobj=stream, mode="r") as archive:
                with self.assertRaises(ops.OpsError):
                    ops.checked_archive(archive)
        self.snapshot_source()
        backup, key = self.backup()
        with ops.decrypted_archive(backup, key) as archive:
            manifest, byname = ops.checked_archive(archive)
            manifest["entries"][0]["mode"] ^= 1
            fake_manifest = json.dumps(manifest).encode()
            original = archive.extractfile
            archive.extractfile = lambda member: io.BytesIO(fake_manifest) if member.name == "MANIFEST.json" else original(member)
            with self.assertRaises(ops.OpsError):
                ops.checked_archive(archive)

    def test_maintenance_resumes_only_originally_running_own_services_on_error(self):
        events = []
        with mock.patch.object(ops, "container_state", side_effect=lambda name, **kw: name == ops.CONTAINER), \
             mock.patch.object(ops, "run", side_effect=lambda args, **kw: events.append(args) or b""):
            with self.assertRaises(ValueError):
                with ops.maintenance():
                    raise ValueError("synthetic interruption")

        self.assertEqual(events, [["docker", "stop", "--time", "30", ops.CONTAINER],
                                   ["docker", "start", ops.CONTAINER]])
        with mock.patch.object(ops, "container_state", return_value=True), \
             mock.patch.object(ops, "run") as runner:
            with self.assertRaises(ops.OpsError):
                with ops.maintenance(offline=True):
                    pass
            runner.assert_not_called()

    def test_dns_and_tls_public_checks_require_exact_answers(self):
        def dns(resolver, host, record):
            return [ops.HOST] if record == "PTR" else [ops.IP] if record == "A" else []
        with mock.patch.object(ops, "dns_query", side_effect=dns), \
             mock.patch.object(ops, "check_ptr_public", return_value={"verified": True, "propagation_pending": False, "dns_queries": 2}), \
             mock.patch.object(ops, "tls_socket") as tls:
            self.assertTrue(ops.check_public()["verified"])
            self.assertEqual(tls.call_args_list, [mock.call(ops.HOST, 443), mock.call(ops.WEBMAIL, 443)])
        for reason in ("aaaa", "a"):
            def bad(resolver, host, record):
                if reason == "aaaa" and record == "AAAA":
                    return ["2001:db8::1"]
                if reason == "a" and record == "A":
                    return [ops.IP, "192.0.2.1"]
                return dns(resolver, host, record)
            with mock.patch.object(ops, "dns_query", side_effect=bad), \
                 mock.patch.object(ops, "check_ptr_public", return_value={"verified": True, "propagation_pending": False, "dns_queries": 2}), \
                 mock.patch.object(ops, "tls_socket") as tls:
                with self.assertRaises(ops.OpsError):
                    ops.check_public()
                tls.assert_not_called()

    def test_dns_queries_reject_nxdomain_servfail_and_cname(self):
        answer = b";; ->>HEADER<<- opcode: QUERY, status: NOERROR, id: 1\nmail.naperu.cloud. 30 IN A 72.61.37.46\n"
        with mock.patch.object(ops, "run", return_value=answer):
            self.assertEqual(ops.dns_query("8.8.8.8", ops.HOST, "A"), [ops.IP])
        empty = b";; ->>HEADER<<- opcode: QUERY, status: NOERROR, id: 1\n"
        with mock.patch.object(ops, "run", return_value=empty):
            self.assertEqual(ops.dns_query("8.8.8.8", ops.HOST, "AAAA"), [])
        for output in (b";; ->>HEADER<<- status: NXDOMAIN, id: 1\n",
                       b";; ->>HEADER<<- status: SERVFAIL, id: 1\n",
                       empty + b"mail.naperu.cloud. 30 IN CNAME other.invalid.\n"):
            with mock.patch.object(ops, "run", return_value=output):
                with self.assertRaises(ops.OpsError):
                    ops.dns_query("8.8.8.8", ops.HOST, "A")

    def test_failed_stop_attempt_restarts_the_original_running_service(self):
        events = []
        def runner(args, **kwargs):
            events.append(args)
            if "stop" in args:
                raise ops.OpsError("synthetic uncertain stop")
            return b""
        with mock.patch.object(ops, "container_state", side_effect=lambda name, **kw: name == ops.CONTAINER), \
             mock.patch.object(ops, "run", side_effect=runner):
            with self.assertRaises(ops.OpsError):
                with ops.maintenance():
                    pass
        self.assertEqual(events, [["docker", "stop", "--time", "30", ops.CONTAINER],
                                   ["docker", "start", ops.CONTAINER]])

    def test_runtime_ipc_allowlist_omissions_preserve_regular_mail_queue(self):
        self.snapshot_source()
        state = self.root / "mail-state"
        (state / "spool-postfix/private").mkdir(parents=True)
        (state / "spool-postfix/public").mkdir()
        (state / "spool-postfix/incoming").mkdir()
        (state / "lib-rspamd").mkdir()
        queue = state / "spool-postfix/incoming/synthetic-queued-mail"
        queue.write_bytes(b"synthetic queued message must survive")
        fifo = state / "spool-postfix/public/pickup"
        os.mkfifo(fifo, 0o600)
        sockets = []
        for relative in ("spool-postfix/private/smtp", "lib-rspamd/rspamd.sock"):
            endpoint = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            endpoint.bind(str(state / relative))
            sockets.append(endpoint)
        try:
            backup, key = self.backup()
            with ops.decrypted_archive(backup, key) as archive:
                manifest, _ = ops.checked_archive(archive)
                omitted = {entry["name"]: entry["type"] for entry in manifest["omissions"]}
                self.assertEqual(omitted, {
                    "mail-state/spool-postfix/private/smtp": "socket",
                    "mail-state/spool-postfix/public/pickup": "fifo",
                    "mail-state/lib-rspamd/rspamd.sock": "socket"})
                self.assertTrue(all(entry["reason"] == "runtime-only-allowlist"
                                    for entry in manifest["omissions"]))
                self.assertFalse(set(omitted) & set(archive.getnames()))
                self.assertIn("mail-state/spool-postfix/incoming/synthetic-queued-mail",
                              archive.getnames())
            destination = self.root / ".local/restore-tests/runtime-ipc"
            result = ops.restore_test(backup, key, destination, self.root)
            self.assertTrue(result["verified"])
            self.assertEqual(result["components"]["mail-state"]["runtime_omitted"], 3)
            self.assertEqual((destination / "mail-state/spool-postfix/incoming/synthetic-queued-mail").read_bytes(),
                             b"synthetic queued message must survive")
            for name in omitted:
                self.assertFalse(os.path.lexists(destination / name))
        finally:
            for endpoint in sockets:
                endpoint.close()

    def test_regular_files_at_runtime_paths_are_kept_and_unlisted_specials_fail(self):
        self.snapshot_source()
        public = self.root / "mail-state/spool-postfix/public"
        public.mkdir(parents=True)
        (public / "pickup").write_bytes(b"regular file at allowlisted FIFO path")
        manifest = ops.inventory(self.root)
        entries = {entry["name"]: entry for entry in manifest["entries"]}
        self.assertEqual(entries["mail-state/spool-postfix/public/pickup"]["type"], "file")
        self.assertFalse(manifest["omissions"])
        unsafe_fifo = self.root / "mail-data/unlisted-fifo"
        os.mkfifo(unsafe_fifo, 0o600)
        with self.assertRaises(ops.OpsError):
            ops.inventory(self.root)
        unsafe_fifo.unlink()
        private = self.root / "mail-state/spool-postfix/private"
        private.mkdir()
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as endpoint:
            endpoint.bind(str(private / "unexpected"))
            with self.assertRaises(ops.OpsError):
                ops.inventory(self.root)

    def test_omission_manifest_cannot_hide_unknown_or_regular_archive_entries(self):
        self.snapshot_source()
        backup, key = self.backup()
        for omitted in (
                {"name": "mail-data/sample", "type": "socket", "reason": "runtime-only-allowlist"},
                {"name": "mail-state/spool-postfix/private/unknown", "type": "socket", "reason": "runtime-only-allowlist"},
                {"name": "mail-state/spool-postfix/public/pickup", "type": "socket", "reason": "runtime-only-allowlist"}):
            with ops.decrypted_archive(backup, key) as archive:
                manifest, _ = ops.checked_archive(archive)
                manifest["omissions"] = [omitted]
                changed = json.dumps(manifest).encode()
                original = archive.extractfile
                archive.extractfile = lambda member: io.BytesIO(changed) if member.name == "MANIFEST.json" else original(member)
                with self.assertRaises(ops.OpsError):

                    ops.checked_archive(archive)

    def ptr_fixture(self, recursive=None, delegation=None, authorities=None):
        recursive = recursive or {}
        authorities = authorities or {}
        def response(server, host, record, *, authoritative=False):
            if server in ops.RESOLVERS and record == "PTR":
                return recursive.get(server, {"values": [ops.HOST], "ttl_seconds": [300], "authoritative": False})
            if server in ops.RESOLVERS and record == "NS":
                if isinstance(delegation, Exception):
                    raise delegation
                return delegation or {"values": list(reversed(ops.PTR_AUTHORITIES)),
                                      "ttl_seconds": [600, 600], "authoritative": False}
            if server in ops.PTR_AUTHORITIES and record == "PTR" and authoritative:
                result = authorities.get(server, {"values": [ops.HOST], "ttl_seconds": [86400], "authoritative": True})
                if isinstance(result, Exception):
                    raise result
                return result
            raise AssertionError("Unexpected DNS operation in isolated PTR fixture.")
        return response

    def test_ptr_fresh_recursors_pass_without_authority_fallback(self):
        with mock.patch.object(ops, "dns_response", side_effect=self.ptr_fixture()) as dns:
            result = ops.check_ptr_public()
        self.assertTrue(result["verified"])
        self.assertFalse(result["propagation_pending"])
        self.assertEqual(result["source"], "recursive")
        self.assertEqual(result["authorities"], [])
        self.assertEqual(result["dns_queries"], 2)
        self.assertEqual(dns.call_count, 2)

    def test_ptr_known_legacy_positive_ttl_requires_both_exact_authorities(self):
        for resolvers in ((ops.RESOLVERS[1],), ops.RESOLVERS):
            recursive = {server: {"values": [ops.PTR_LEGACY], "ttl_seconds": [21600],
                                  "authoritative": False} for server in resolvers}
            with mock.patch.object(ops, "dns_response", side_effect=self.ptr_fixture(recursive=recursive)) as dns:
                result = ops.check_ptr_public()
            self.assertTrue(result["verified"])
            self.assertTrue(result["propagation_pending"])
            self.assertEqual(result["source"], "authoritative")
            self.assertEqual(result["dns_queries"], 6)
            self.assertEqual(result["delegation"]["names"], list(ops.PTR_AUTHORITIES))
            self.assertEqual([item["server"] for item in result["authorities"]], list(ops.PTR_AUTHORITIES))
            self.assertEqual(len([item for item in result["recursive"] if item["value"] == ops.PTR_LEGACY]), len(resolvers))
            self.assertTrue(all(item["ttl_seconds"] > 0 and item["authoritative"]
                                for item in result["authorities"]))
            self.assertEqual(dns.call_count, 6)
            direct_calls = [call for call in dns.call_args_list if call.kwargs.get("authoritative")]
            self.assertEqual(len(direct_calls), 2)

    def test_ptr_unknown_multiple_missing_or_nonpositive_ttl_never_falls_back(self):
        cases = ({"values": ["other.invalid"], "ttl_seconds": [300], "authoritative": False},
                 {"values": [ops.HOST, ops.PTR_LEGACY], "ttl_seconds": [300, 300], "authoritative": False},
                 {"values": [], "ttl_seconds": [], "authoritative": False},
                 {"values": [ops.PTR_LEGACY], "ttl_seconds": [0], "authoritative": False},
                 {"values": [ops.HOST], "ttl_seconds": [0], "authoritative": False})
        for response in cases:
            with mock.patch.object(ops, "dns_response", side_effect=self.ptr_fixture(
                    recursive={ops.RESOLVERS[0]: response})) as dns:
                with self.assertRaises(ops.OpsError):
                    ops.check_ptr_public()
                self.assertEqual(dns.call_count, 1)

    def test_ptr_delegation_mismatch_duplicates_zero_ttl_or_lookup_failure_block(self):
        legacy = {ops.RESOLVERS[1]: {"values": [ops.PTR_LEGACY], "ttl_seconds": [21600],
                                    "authoritative": False}}
        cases = ({"values": ["other.invalid", ops.PTR_AUTHORITIES[1]], "ttl_seconds": [600, 600], "authoritative": False},
                 {"values": [ops.PTR_AUTHORITIES[0]] * 2, "ttl_seconds": [600, 600], "authoritative": False},
                 {"values": list(ops.PTR_AUTHORITIES), "ttl_seconds": [0, 600], "authoritative": False},
                 ops.OpsError("synthetic delegation timeout"))
        for delegation in cases:
            with mock.patch.object(ops, "dns_response", side_effect=self.ptr_fixture(
                    recursive=legacy, delegation=delegation)) as dns:
                with self.assertRaises(ops.OpsError):
                    ops.check_ptr_public()
                self.assertFalse(any(call.kwargs.get("authoritative") for call in dns.call_args_list))

    def test_ptr_authority_unknown_noaa_zero_ttl_and_timeout_block(self):
        legacy = {ops.RESOLVERS[1]: {"values": [ops.PTR_LEGACY], "ttl_seconds": [21600],
                                    "authoritative": False}}
        cases = ({"values": [ops.PTR_LEGACY], "ttl_seconds": [86400], "authoritative": True},
                 {"values": [ops.HOST], "ttl_seconds": [86400], "authoritative": False},
                 {"values": [ops.HOST], "ttl_seconds": [0], "authoritative": True},
                 ops.OpsError("synthetic authority timeout"))
        for authority in cases:
            with mock.patch.object(ops, "dns_response", side_effect=self.ptr_fixture(
                    recursive=legacy, authorities={ops.PTR_AUTHORITIES[1]: authority})):
                with self.assertRaises(ops.OpsError):
                    ops.check_ptr_public()

    def test_direct_dns_metadata_requires_aa_noerror_exact_owner_and_valid_ttl(self):
        answer = (b";; ->>HEADER<<- opcode: QUERY, status: NOERROR, id: 1\n"
                  b";; flags: qr aa; QUERY: 1, ANSWER: 1, AUTHORITY: 0, ADDITIONAL: 0\n"
                  b"46.37.61.72.in-addr.arpa. 86400 IN PTR mail.naperu.cloud.\n")
        with mock.patch.object(ops, "run", return_value=answer) as runner:
            result = ops.dns_response(ops.PTR_AUTHORITIES[0], ops.IP, "PTR", authoritative=True)
            self.assertEqual(result, {"values": [ops.HOST], "ttl_seconds": [86400], "authoritative": True})
            self.assertIn("+norecurse", runner.call_args.args[0])
        for output in (answer.replace(b"NOERROR", b"SERVFAIL"),
                       answer.replace(b"NOERROR", b"NXDOMAIN"),
                       answer.replace(b"qr aa;", b"qr;"),
                       answer.replace(b"86400", b"-1"),
                       answer.replace(b"46.37.61.72", b"45.37.61.72"),
                       answer.replace(b" IN PTR ", b" IN CNAME ")):
            with mock.patch.object(ops, "run", return_value=output):
                with self.assertRaises(ops.OpsError):
                    ops.dns_response(ops.PTR_AUTHORITIES[0], ops.IP, "PTR", authoritative=True)

    def test_lock_and_key_creation_never_overwrite(self):
        ops.init_key(self.root)
        before = (self.root / ".local/backup.key").read_bytes()
        with self.assertRaises(FileExistsError):
            ops.init_key(self.root)
        self.assertEqual((self.root / ".local/backup.key").read_bytes(), before)
        with ops.locked(self.root):
            with self.assertRaises(ops.OpsError):
                with ops.locked(self.root):
                    pass


if __name__ == "__main__":
    unittest.main(verbosity=2)
