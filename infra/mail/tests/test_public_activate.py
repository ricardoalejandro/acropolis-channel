"""Isolated activation tests: Docker, DNS, TLS and certificates are mocked."""
from contextlib import nullcontext
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import yaml

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/public-activate.py'
spec = importlib.util.spec_from_file_location('mail_public_activate_tests', SCRIPT)
act = importlib.util.module_from_spec(spec)
spec.loader.exec_module(act)

COOKIE = {'secure': True, 'http_only': True, 'same_site': 'Lax'}
PTR = {'verified': True, 'propagation_pending': True, 'source': 'authoritative',
       'recursive': [], 'authorities': [], 'dns_queries': 6}
DNS = {'ptr': PTR, 'propagation_pending': True}


class ActivationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='naperu-public-unit-')
        self.addCleanup(self.directory.cleanup)
        base = Path(self.directory.name)
        self.root = base / 'runtime'
        self.source = base / 'source'
        self.dynamic = base / 'dynamic'
        for path in (self.root / '.local', self.root / 'config/rspamd/override.d',
                     self.root / 'roundcube-config', self.root / 'roundcube-trust',
                     self.source, self.dynamic):
            path.mkdir(parents=True, exist_ok=True)
        self.route = self.dynamic / 'naperu-mail.yml'
        self.candidate = (SCRIPT.parents[1] / 'public-routing.yml').read_bytes()
        (self.source / 'public-routing.yml').write_bytes(self.candidate)
        files = (
            ('compose.yml', 'compose.yml'), ('public.yml', 'public.yml'),
            ('stage.yml', 'stage.yml'), ('roundcube.inc.php', 'roundcube-config/mail.inc.php'),
            ('user-patches.sh', 'config/user-patches.sh'),
            ('rspamd-dkim.conf', 'config/rspamd/override.d/dkim_signing.conf'),
            ('postfix-main.cf', 'config/postfix-main.cf'),
        )
        for original, deployed in files:
            content = b'defer_transports = smtp\nmilter_default_action = tempfail\n' if original == 'postfix-main.cf' else (original + '\n').encode()
            (self.source / original).write_bytes(content)
            (self.root / deployed).write_bytes(content)
        self.bundle = self.root / 'roundcube-trust/ca-bundle.pem'
        self.bundle.write_bytes(b'staging public CA\n')
        self.system_ca = base / 'system-ca.pem'
        self.system_ca.write_bytes(b'normal system public CA\n')
        self.other = self.dynamic / 'unrelated.yaml'
        self.other.write_bytes(b'http:\n  routers:\n    another-project:\n      service: another-project\n')
        self.other_before = self.other.read_bytes()
        self.ops = SimpleNamespace(
            locked=Mock(side_effect=lambda root: nullcontext()),
            check_ptr_public=Mock(return_value=PTR),
            check_public=Mock(return_value={'verified': True, **DNS}),
            export_cert=Mock(return_value={'serving_verified': True}),
            container_state=Mock(return_value=True),
        )
        current = {'state': {'Running': True, 'Health': {'Status': 'healthy'}},
                   'ports': {'465/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '2465'}]}}
        self.mocks = {}
        for name, value in (
            ('check_dns', DNS), ('inspect_services', {act.MAIL: current, act.WEBMAIL: current}),
            ('inspect_container', current), ('wait_healthy', None), ('verify_webmail', COOKIE),
            ('require_postfix', None), ('compose', None), ('run', ''),
            ('postfix_values', {'defer_transports': 'smtp'}),
        ):
            p = patch.object(act, name, return_value=value)
            self.mocks[name] = p.start()
            self.addCleanup(p.stop)
        p = patch.object(act, 'SYSTEM_CA', self.system_ca)
        p.start()
        self.addCleanup(p.stop)

    def prepare(self):
        self.route.write_bytes(act.prepared_route(self.source))

    def active(self):
        self.route.write_bytes(self.candidate)

    def invoke(self, operation):
        return act.activate(operation, root=self.root, source=self.source,
                            route=self.route, ops=self.ops)

    def recorded(self):
        return json.loads((self.root / '.local/publication-state.json').read_bytes())

    def assert_untouched_other(self):
        self.assertEqual(self.other.read_bytes(), self.other_before)

    def test_routing_retry_preserves_active_without_dns_or_mutations(self):
        self.active()
        before = self.route.read_bytes()
        result = self.invoke('routing')
        self.assertTrue(result['active_preserved'])
        self.assertEqual(self.route.read_bytes(), before)
        self.mocks['check_dns'].assert_not_called()
        self.mocks['compose'].assert_not_called()
        self.assertFalse((self.root / '.local/publication-state.json').exists())
        self.assert_untouched_other()

    def test_routing_prepares_only_noop_and_preserves_other_routes(self):
        result = self.invoke('routing')
        self.assertTrue(result['routing_prepared'])
        self.assertFalse(act.route_active(self.route, self.source))
        self.assertEqual(self.recorded()['phase'], 'prepared')
        self.ops.check_public.assert_not_called()
        self.mocks['compose'].assert_not_called()
        self.assert_untouched_other()

    def test_collision_in_yaml_blocks_before_any_change(self):
        self.other.write_text('http:\n  routers:\n    naperu-mail-https: {}\n')
        with self.assertRaisesRegex(act.ActivationError, 'collision'):
            self.invoke('routing')
        self.assertFalse(self.route.exists())
        self.mocks['check_dns'].assert_not_called()

    def test_route_drift_cannot_be_claimed_as_owned_active_route(self):
        changed = yaml.safe_load(self.candidate)
        changed['http']['routers']['naperu-mail-https']['rule'] = 'Host("unrelated.example")'
        self.route.write_text(yaml.safe_dump(changed))
        with self.assertRaisesRegex(act.ActivationError, 'approved'):
            self.invoke('routing')
        self.mocks['compose'].assert_not_called()

    def test_route_symlink_is_rejected_and_target_is_untouched(self):
        self.route.symlink_to(self.other)
        with self.assertRaisesRegex(act.ActivationError, 'regular'):
            self.invoke('routing')
        self.assert_untouched_other()

    def test_source_drift_blocks_before_export_or_public_ports(self):
        self.prepare()
        (self.root / 'config/postfix-main.cf').write_text('defer_transports = smtp\nmilter_default_action = accept\n')
        with self.assertRaisesRegex(act.ActivationError, 'source drift'):
            self.invoke('publish')
        self.ops.export_cert.assert_not_called()
        self.mocks['compose'].assert_not_called()

    def test_publish_requires_prepared_router(self):
        with self.assertRaisesRegex(act.ActivationError, 'Prepare'):
            self.invoke('publish')
        self.mocks['compose'].assert_not_called()

    def test_publish_keeps_noop_until_health_cookie_and_tls_proofs_pass(self):
        self.prepare()
        events = []
        def healthy(*args, **kwargs):
            self.assertFalse(act.route_active(self.route, self.source))
            events.append('health')
        def cookies(*args):
            self.assertFalse(act.route_active(self.route, self.source))
            events.append('cookies')
            return COOKIE
        def tls(**kwargs):
            self.assertFalse(act.route_active(self.route, self.source))
            events.append('tls' if kwargs.get('mail_tls') else 'https')
            return {'verified': True, **DNS}
        self.mocks['wait_healthy'].side_effect = healthy
        self.mocks['verify_webmail'].side_effect = cookies
        self.ops.check_public.side_effect = tls
        result = self.invoke('publish')
        self.assertEqual(events, ['https', 'health', 'cookies', 'tls'])
        self.assertTrue(act.route_active(self.route, self.source))
        self.assertTrue(result['external_delivery_still_deferred'])
        self.assertEqual(self.bundle.read_bytes(), self.system_ca.read_bytes())
        self.assertEqual(self.recorded()['phase'], 'public')
        self.mocks['compose'].assert_called_once_with(self.root, 'public.yml')
        self.assert_untouched_other()

    def test_publish_retry_preserves_active_route_and_delivery_config(self):
        self.active()
        conf = self.root / 'config/postfix-main.cf'
        conf.write_text('defer_transports = \nmilter_default_action = tempfail\n')
        before = conf.read_bytes()
        result = self.invoke('publish')
        self.assertTrue(result['active_preserved'])
        self.assertEqual(conf.read_bytes(), before)
        self.assertEqual(self.route.read_bytes(), self.candidate)
        self.mocks['compose'].assert_not_called()
        self.mocks['require_postfix'].assert_called_once_with(self.root, deferred=False)

    def test_health_cookie_or_tls_failure_rolls_back_to_stage_and_noop(self):
        for failure in ('health', 'cookies', 'tls'):
            with self.subTest(failure=failure):
                self.prepare()
                previous_bundle = self.bundle.read_bytes()
                self.mocks['wait_healthy'].side_effect = None
                self.mocks['verify_webmail'].side_effect = None
                self.ops.check_public.side_effect = None
                self.mocks['compose'].reset_mock()
                if failure == 'health':
                    self.mocks['wait_healthy'].side_effect = act.ActivationError('unhealthy')
                elif failure == 'cookies':
                    self.mocks['verify_webmail'].side_effect = act.ActivationError('insecure cookie')
                else:
                    self.ops.check_public.side_effect = [ {'verified': True}, act.ActivationError('TLS failed') ]
                with self.assertRaises(act.ActivationError) as caught:
                    self.invoke('publish')
                self.assertFalse(act.route_active(self.route, self.source))
                self.assertEqual(self.bundle.read_bytes(), previous_bundle)
                self.assertTrue(caught.exception.recovery['delivery_disabled'])
                self.assertFalse(caught.exception.recovery['public_inbound_may_be_open'])
                self.assertEqual(self.recorded()['phase'], 'failed_publication')
                self.assertEqual(self.mocks['compose'].call_args_list[-1].args, (self.root, 'stage.yml'))
                self.assert_untouched_other()

    def test_failed_stage_rollback_reports_possible_public_inbound(self):
        self.prepare()
        self.mocks['wait_healthy'].side_effect = act.ActivationError('unhealthy')
        self.mocks['compose'].side_effect = [None, act.ActivationError('stage failed')]
        with self.assertRaises(act.ActivationError) as caught:
            self.invoke('publish')
        recovery = caught.exception.recovery
        self.assertTrue(recovery['public_inbound_may_be_open'])
        self.assertTrue(recovery['recovery_required'])
        self.assertFalse(recovery['public_router_active'])
        self.assertFalse(act.route_active(self.route, self.source))

    def test_failed_router_restore_stops_owned_webmail_and_does_not_restart_stage(self):
        self.prepare()
        original_atomic = act.atomic
        route_writes = 0
        def failing_route(path, data, mode=0o600):
            nonlocal route_writes
            if path == self.route:
                route_writes += 1
                if route_writes > 1:
                    raise OSError('router restore blocked')
            return original_atomic(path, data, mode)
        original_state = act.state
        def failed_public_state(root, phase, **details):
            if phase == 'public':
                raise OSError('state disk blocked')
            return original_state(root, phase, **details)
        with patch.object(act, 'atomic', side_effect=failing_route), patch.object(act, 'state', side_effect=failed_public_state):
            with self.assertRaises(act.ActivationError) as caught:
                self.invoke('publish')
        self.assertTrue(caught.exception.recovery['public_router_active'])
        self.assertTrue(caught.exception.recovery['webmail_stopped'])
        self.assertTrue(caught.exception.recovery['recovery_required'])
        self.mocks['compose'].assert_called_once_with(self.root, 'public.yml')
        self.mocks['run'].assert_any_call(['docker', 'stop', '--time', '30', act.WEBMAIL], timeout=60)
        self.assert_untouched_other()

    def test_missing_durable_failure_state_still_returns_explicit_recovery(self):
        self.prepare()
        self.mocks['wait_healthy'].side_effect = act.ActivationError('unhealthy')
        original = act.state
        def failing_state(root, phase, **details):
            if phase == 'failed_publication':
                raise OSError('disk blocked')
            return original(root, phase, **details)
        with patch.object(act, 'state', side_effect=failing_state):
            with self.assertRaises(act.ActivationError) as caught:
                self.invoke('publish')
        self.assertFalse(caught.exception.recovery['state_recorded'])
        self.assertTrue(caught.exception.recovery['recovery_required'])
        self.assertFalse(act.route_active(self.route, self.source))

    def test_allow_delivery_requires_active_router(self):
        self.prepare()
        with self.assertRaisesRegex(act.ActivationError, 'already verified'):
            self.invoke('allow-delivery')
        self.mocks['run'].assert_not_called()

    def test_delivery_reload_failure_restores_disk_and_live_defer(self):
        self.active()
        failed_once = False
        def mutation(args, **kwargs):
            nonlocal failed_once
            if args == ['docker', 'exec', act.MAIL, 'postfix', 'reload'] and not failed_once:
                failed_once = True
                raise act.ActivationError('reload failed')
            return ''
        self.mocks['run'].side_effect = mutation
        with self.assertRaises(act.ActivationError) as caught:
            self.invoke('allow-delivery')
        self.assertIn('defer_transports = smtp', (self.root / 'config/postfix-main.cf').read_text())
        self.assertTrue(caught.exception.recovery['delivery_disabled'])
        self.assertFalse(caught.exception.recovery['mail_stopped'])
        self.mocks['run'].assert_any_call(['docker', 'exec', act.MAIL, 'postconf', '-e', 'defer_transports=smtp'], timeout=30)
        self.assertEqual(self.recorded()['phase'], 'failed_delivery')
        self.assertTrue(act.route_active(self.route, self.source))

    def test_unverifiable_live_defer_stops_only_owned_mail_container(self):
        self.active()
        self.mocks['postfix_values'].return_value = {'defer_transports': ''}
        with self.assertRaises(act.ActivationError) as caught:
            # Fail after live enable when writing successful state.
            original = act.state
            def fail_success(root, phase, **details):
                if phase == 'delivery_enabled':
                    raise OSError('disk blocked')
                return original(root, phase, **details)
            with patch.object(act, 'state', side_effect=fail_success):
                self.invoke('allow-delivery')
        self.assertTrue(caught.exception.recovery['mail_stopped'])
        self.mocks['run'].assert_any_call(['docker', 'stop', '--time', '30', act.MAIL], timeout=60)
        self.assertIn('defer_transports = smtp', (self.root / 'config/postfix-main.cf').read_text())

    def test_wrong_container_identity_prevents_emergency_stop_or_exec(self):
        self.mocks['inspect_container'].side_effect = act.ActivationError('wrong owner')
        recovery = act.fail_closed(self.root, self.ops)
        self.assertTrue(recovery['recovery_required'])
        self.assertFalse(recovery['delivery_disabled'])
        self.mocks['run'].assert_not_called()

    def test_delivery_success_persists_only_empty_defer_and_safe_proof(self):
        self.active()
        self.mocks['postfix_values'].return_value = {'defer_transports': ''}
        result = self.invoke('allow-delivery')
        self.assertTrue(result['external_delivery_enabled'])
        self.assertIn('defer_transports = \n', (self.root / 'config/postfix-main.cf').read_text())
        self.assertEqual(self.recorded()['phase'], 'delivery_enabled')
        self.assertTrue(act.route_active(self.route, self.source))
        self.assert_untouched_other()

    def test_postfix_source_only_permits_defer_change(self):
        conf = self.root / 'config/postfix-main.cf'
        conf.write_text('defer_transports = \nmilter_default_action = tempfail\n')
        act.check_source(self.root, self.source)
        conf.write_text('defer_transports = \nmilter_default_action = accept\n')
        with self.assertRaisesRegex(act.ActivationError, 'source drift'):
            act.check_source(self.root, self.source)

    def test_dns_uses_shared_ptr_report_and_preserves_pending_flag(self):
        records = [
            {'type': 'A', 'name': 'mail', 'value': act.IP},
            {'type': 'A', 'name': 'webmail', 'value': act.IP},
        ]
        (self.root / '.local/dns-required.json').write_text(json.dumps(records))
        # Real function is loaded separately to avoid undoing unrelated workflow mocks.
        fresh_spec = importlib.util.spec_from_file_location('mail_public_dns_test', SCRIPT)
        fresh = importlib.util.module_from_spec(fresh_spec)
        fresh_spec.loader.exec_module(fresh)
        with patch.object(fresh, 'records', side_effect=lambda resolver, name, kind: [act.IP] if kind == 'A' else []):
            result = fresh.check_dns(self.root, self.ops, all_records=True)
        self.assertIs(result['ptr'], PTR)
        self.assertTrue(result['propagation_pending'])
        self.ops.check_ptr_public.assert_called_once_with()

    def test_public_marker_is_created_only_after_all_proofs(self):
        self.prepare()
        marker = self.root / '.local/public-started'
        def cookies(*args):
            self.assertFalse(marker.exists())
            return COOKIE
        def tls(**kwargs):
            self.assertFalse(marker.exists())
            return {'verified': True, **DNS}
        self.mocks['verify_webmail'].side_effect = cookies
        self.ops.check_public.side_effect = tls
        self.invoke('publish')
        self.assertEqual(marker.read_bytes(), b'1\n')
        self.assertEqual(marker.stat().st_mode & 0o777, 0o600)

    def test_active_retry_repairs_missing_marker_and_preserves_existing_marker(self):
        self.active()
        marker = self.root / '.local/public-started'
        self.invoke('publish')
        self.assertEqual(marker.read_bytes(), b'1\n')
        marker.write_bytes(b'previous marker\n')
        self.invoke('publish')
        self.assertEqual(marker.read_bytes(), b'previous marker\n')
        self.mocks['compose'].assert_not_called()
        self.assertEqual(self.route.read_bytes(), self.candidate)

    def test_marker_failure_restores_noop_and_defers_smtp(self):
        self.prepare()
        with patch.object(act, 'ensure_started', side_effect=OSError('marker disk blocked')):
            with self.assertRaises(act.ActivationError) as caught:
                self.invoke('publish')
        self.assertFalse(act.route_active(self.route, self.source))
        self.assertTrue(caught.exception.recovery['delivery_disabled'])
        self.assertFalse(caught.exception.recovery['public_inbound_may_be_open'])
        self.assertEqual(self.recorded()['phase'], 'failed_publication')

    def test_marker_failure_on_active_retry_preserves_router_and_blocks_delivery(self):
        self.active()
        marker = self.root / '.local/public-started'
        marker.write_bytes(b'previous marker\n')
        with patch.object(act, 'ensure_started', side_effect=OSError('marker check blocked')):
            with self.assertRaises(act.ActivationError) as caught:
                self.invoke('publish')
        self.assertEqual(marker.read_bytes(), b'previous marker\n')
        self.assertEqual(self.route.read_bytes(), self.candidate)
        self.assertTrue(caught.exception.recovery['active_preserved'])
        self.assertTrue(caught.exception.recovery['delivery_disabled'])
        self.mocks['compose'].assert_not_called()

    def test_identical_spf_or_dmarc_duplicates_are_rejected(self):
        fresh_spec = importlib.util.spec_from_file_location('mail_public_duplicate_dns_test', SCRIPT)
        fresh = importlib.util.module_from_spec(fresh_spec)
        fresh_spec.loader.exec_module(fresh)
        for prefix in ('v=spf1 ip4:72.61.37.46 -all', 'v=DMARC1; p=reject'):
            with self.subTest(value=prefix):
                records = [{'type': 'TXT', 'name': '@', 'value': prefix}]
                (self.root / '.local/dns-required.json').write_text(json.dumps(records))
                with patch.object(fresh, 'records', side_effect=lambda resolver, name, kind: [prefix, prefix] if kind == 'TXT' else []):
                    with self.assertRaisesRegex(fresh.ActivationError, 'DNS pending'):
                        fresh.check_dns(self.root, self.ops, all_records=True)
                self.ops.check_ptr_public.assert_not_called()

    def test_unrelated_verification_txt_can_coexist_with_single_spf(self):
        fresh_spec = importlib.util.spec_from_file_location('mail_public_coexisting_dns_test', SCRIPT)
        fresh = importlib.util.module_from_spec(fresh_spec)
        fresh_spec.loader.exec_module(fresh)
        spf = 'v=spf1 ip4:72.61.37.46 -all'
        (self.root / '.local/dns-required.json').write_text(json.dumps([
            {'type': 'TXT', 'name': '@', 'value': spf}]))
        with patch.object(fresh, 'records', side_effect=lambda resolver, name, kind: [spf, 'unrelated-verification=public'] if kind == 'TXT' else []):
            self.assertTrue(fresh.check_dns(self.root, self.ops, all_records=True)['ptr']['verified'])

    def test_stage_rollback_reasserts_defer_after_compose_restarts_mail(self):
        self.prepare()
        self.mocks['wait_healthy'].side_effect = act.ActivationError('unhealthy')
        with patch.object(act, 'fail_closed', side_effect=[
                {'delivery_disabled': True, 'mail_stopped': True},
                {'delivery_disabled': True, 'mail_stopped': False}]) as blocking:
            with self.assertRaises(act.ActivationError) as caught:
                self.invoke('publish')
        self.assertEqual(blocking.call_count, 2)
        self.assertTrue(caught.exception.recovery['delivery_disabled'])
        self.assertFalse(caught.exception.recovery['mail_stopped'])
        self.assertFalse(caught.exception.recovery['public_inbound_may_be_open'])
        self.assertFalse(act.route_active(self.route, self.source))


class ProofTests(unittest.TestCase):
    def test_cookie_proof_returns_flags_without_cookie_value(self):
        result = act.cookie_proof(['HTTP/1.1 200 OK',
                                  'Set-Cookie: roundcube_sessid=private-value; Path=/; Secure; HttpOnly; SameSite=Lax'])
        self.assertEqual(result, COOKIE)
        self.assertNotIn('private-value', json.dumps(result))

    def test_cookie_proof_rejects_missing_or_wrong_attributes(self):
        for attributes in ('HttpOnly; SameSite=Lax', 'Secure; SameSite=Lax',
                           'Secure; HttpOnly; SameSite=None', 'Secure; HttpOnly; SameSite=Strict'):
            with self.subTest(attributes=attributes):
                with self.assertRaises(act.ActivationError):
                    act.cookie_proof(['Set-Cookie: roundcube_sessid=secret; ' + attributes])

    def test_private_http_proof_disables_redirects_and_checks_owned_container(self):
        with patch.object(act, 'container_exec', return_value=json.dumps([
                'HTTP/1.1 200 OK', 'Set-Cookie: roundcube_sessid=secret; Secure; HttpOnly; SameSite=Lax'])) as execute:
            self.assertEqual(act.verify_webmail(), COOKIE)
        args = execute.call_args.args
        self.assertEqual(args[:2], (act.ROOT, act.WEBMAIL))
        self.assertIn('"follow_location"=>0', args[2][-1])
        self.assertIn('Host: webmail.naperu.cloud\r\n', args[2][-1])

    def test_private_http_proof_rejects_redirect_before_cookie(self):
        with patch.object(act, 'container_exec', return_value=json.dumps([
                'HTTP/1.1 302 Found', 'Set-Cookie: roundcube_sessid=secret; Secure; HttpOnly; SameSite=Lax'])):
            with self.assertRaises(act.ActivationError):
                act.verify_webmail()

    def test_dns_answer_requires_noerror_and_exact_owner_type(self):
        invalid = (
            ';; ->>HEADER<<- opcode: QUERY, status: SERVFAIL, id: 1\n',
            ';; ->>HEADER<<- opcode: QUERY, status: NOERROR, id: 1\nmail.naperu.cloud. 300 IN CNAME other.example.\n',
            ';; ->>HEADER<<- opcode: QUERY, status: NOERROR, id: 1\nother.example. 300 IN A 72.61.37.46\n',
        )
        for response in invalid:
            with self.subTest(response=response.splitlines()[0]):
                with patch.object(act, 'run', return_value=response):
                    with self.assertRaises(act.ActivationError):
                        act.records('8.8.8.8', act.HOST, 'A')

    def test_container_identity_requires_exact_project_service_and_working_dir(self):
        for labels in (
            {'com.docker.compose.project': 'other-project', 'com.docker.compose.service': 'mailserver',
             'com.docker.compose.project.working_dir': str(act.ROOT)},
            {'com.docker.compose.project': act.PROJECT, 'com.docker.compose.service': 'roundcube',
             'com.docker.compose.project.working_dir': str(act.ROOT)},
            {'com.docker.compose.project': act.PROJECT, 'com.docker.compose.service': 'mailserver',
             'com.docker.compose.project.working_dir': '/another/project'},
        ):
            output = '\n'.join(json.dumps(part) for part in (labels, {'Running': True}, {}))
            with patch.object(act, 'run', return_value=output) as command:
                with self.assertRaises(act.ActivationError):
                    act.container_exec(act.ROOT, act.MAIL, ['postfix', 'reload'])
                self.assertEqual(command.call_count, 1)
                self.assertEqual(command.call_args.args[0][:2], ['docker', 'inspect'])

    def test_inspect_format_has_real_line_breaks_and_validates_owned_state(self):
        labels = {'com.docker.compose.project': act.PROJECT,
                  'com.docker.compose.service': 'mailserver',
                  'com.docker.compose.project.working_dir': str(act.ROOT)}
        output = '\n'.join(json.dumps(part) for part in (labels, {'Running': True}, {}))
        with patch.object(act, 'run', return_value=output) as command:
            current = act.inspect_container(act.ROOT, act.MAIL)
        self.assertTrue(current['state']['Running'])
        self.assertEqual(command.call_args.args[0][3].count('\n'), 2)

    def test_postconf_parser_preserves_final_empty_defer_after_output_strip(self):
        output = 'default_transport = smtp\nrelay_transport = smtp\ndefer_transports ='
        with patch.object(act, 'container_exec', return_value=output):
            values = act.postfix_values()
        self.assertEqual(values['defer_transports'], '')
        self.assertEqual(values['default_transport'], 'smtp')


    def test_health_wait_rejects_unhealthy_without_external_operations(self):
        unhealthy = {act.MAIL: {'state': {'Running': True, 'Health': {'Status': 'unhealthy'}}, 'ports': {}},
                     act.WEBMAIL: {'state': {'Running': True, 'Health': {'Status': 'healthy'}}, 'ports': {}}}
        with patch.object(act, 'inspect_services', return_value=unhealthy), patch.object(act.time, 'sleep') as sleeping:
            with self.assertRaisesRegex(act.ActivationError, 'unhealthy'):
                act.wait_healthy(act.ROOT)
        sleeping.assert_not_called()

    def test_postfix_requires_global_smtp_transport_limit_and_closed_relay(self):
        expected = {'default_transport': 'smtp', 'relay_transport': 'smtp',
                    'smtp_transport_rate_delay': '16s', 'smtp_destination_recipient_limit': '1',
                    'mynetworks': '127.0.0.0/8', 'transport_maps': '',
                    'sender_dependent_default_transport_maps': '', 'milter_default_action': 'tempfail',
                    'defer_transports': 'smtp'}
        for change in ({'relay_transport': 'relay'}, {'smtp_transport_rate_delay': '0s'},
                       {'smtp_destination_recipient_limit': '50'}, {'mynetworks': '0.0.0.0/0'},
                       {'milter_default_action': 'accept'}, {'defer_transports': ''}):
            with self.subTest(change=change):
                with patch.object(act, 'postfix_values', return_value={**expected, **change}):
                    with self.assertRaises(act.ActivationError):
                        act.require_postfix()
        with patch.object(act, 'postfix_values', return_value=expected):
            act.require_postfix()


if __name__ == '__main__':
    unittest.main()
