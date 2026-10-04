"""Isolated contracts for minimal mail exposure; no production Docker/DNS calls."""
import contextlib
import copy
import io
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import yaml

SOURCE = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('private_mail_ops_tests', SOURCE / 'scripts/mail-ops.py')
ops = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ops)
SPEC = importlib.util.spec_from_file_location('private_smtp_probe_tests', SOURCE / 'scripts/check-smtp.py')
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)
NETID = 'a' * 64
MAILID = 'b' * 64
WEBID = 'c' * 64


def ports():
    return {'25/tcp': [{'HostIp': ops.IP, 'HostPort': '25'}],
            '465/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '465'}],
            '993/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '993'}]}


def network():
    return {'Name': ops.SMTP_NETWORK, 'Id': NETID, 'Driver': 'bridge',
            'Scope': 'local', 'Internal': True, 'EnableIPv6': False,
            'Options': {}, 'Labels': ops.SMTP_NETWORK_LABELS.copy(), 'Containers': {}}


def member(project, service, *, aliases=None):
    mail = project == 'naperu-mail'
    networks = {ops.SMTP_NETWORK: {'NetworkID': NETID, 'Aliases': aliases or [service]}}
    if mail:
        networks['naperu-mail-private'] = {}
    return {'labels': {'com.docker.compose.project': project,
                       'com.docker.compose.service': service,
                       'com.docker.compose.project.working_dir': str(ops.ROOT if mail else ops.APP_ROOT)},
            'networks': networks}


class PrivateTopologyTests(unittest.TestCase):
    def test_only_exact_inbound_ipv4_and_loopback_tls_ports_are_accepted(self):
        self.assertEqual(ops.validate_public_ports(ports())['public_tcp_ports'], [25])
        candidate = ports()
        candidate['587/tcp'] = None  # Image EXPOSE without host publishing is harmless.
        self.assertFalse(ops.validate_public_ports(candidate)['submission_587_published'])
        bad = []
        for port in ('465/tcp', '993/tcp'):
            for address in (ops.IP, '', '0.0.0.0', '::', '::1'):
                candidate = ports()
                candidate[port][0]['HostIp'] = address
                bad.append(candidate)
        candidate = ports(); candidate.pop('25/tcp'); bad.append(candidate)
        candidate = ports(); candidate['25/tcp'][0]['HostIp'] = '0.0.0.0'; bad.append(candidate)
        candidate = ports(); candidate['587/tcp'] = [{'HostIp': '127.0.0.1', 'HostPort': '587'}]; bad.append(candidate)
        candidate = ports(); candidate['465/tcp'].append(candidate['465/tcp'][0].copy()); bad.append(candidate)
        candidate = ports(); candidate['993/tcp'][0]['HostPort'] = '2993'; bad.append(candidate)
        candidate = ports(); candidate['80/tcp'] = [{'HostIp': ops.IP, 'HostPort': '80'}]; bad.append(candidate)
        for candidate in bad + [None, [], {}]:
            with self.subTest(bindings=candidate):
                with self.assertRaises(ops.OpsError):
                    ops.validate_public_ports(candidate)

    def test_roundcube_cannot_publish_a_host_port_and_checks_are_owned(self):
        with patch.object(ops, 'container_state') as owned, patch.object(ops, 'run', side_effect=[
                json.dumps(ports()).encode(), b'{}']):
            self.assertEqual(ops.check_public_ports()['loopback_tcp_ports'], [465, 993])
        self.assertEqual([call.args[0] for call in owned.call_args_list], list(ops.CONTAINERS))
        with patch.object(ops, 'container_state'), patch.object(ops, 'run', side_effect=[
                json.dumps(ports()).encode(), b'{"80/tcp":[{"HostIp":"127.0.0.1","HostPort":"20888"}]}']):
            with self.assertRaises(ops.OpsError):
                ops.check_public_ports()

    def test_empty_internal_owned_network_is_valid_only_before_mail_attachment(self):
        self.assertFalse(ops.validate_smtp_network(network(), {})['mailserver_attached'])
        with self.assertRaises(ops.OpsError):
            ops.validate_smtp_network(network(), {}, require_mail=True)
        for change in ({'Name': 'another-network'}, {'Driver': 'overlay'}, {'Internal': False},
                       {'Scope': 'swarm'}, {'EnableIPv6': True}, {'Id': 'untrusted'},
                       {'Labels': {}}, {'Options': {'com.docker.network.bridge.name': 'custom'}}):
            candidate = network(); candidate.update(change)
            with self.subTest(change=change), self.assertRaises(ops.OpsError):
                ops.validate_smtp_network(candidate, {})

    def test_mail_web_and_transient_runner_require_exact_role_directory_and_alias(self):
        current = network()
        members = {MAILID: member('naperu-mail', 'mailserver', aliases=['mailserver', ops.HOST]),
                   WEBID: member('acropolis-channel', 'web'),
                   'd' * 64: member('acropolis-channel', 'migrations')}
        current['Containers'] = {key: {} for key in members}
        self.assertEqual(ops.validate_smtp_network(current, members, require_mail=True)['members_verified'], 3)
        for role in (('acropolis-channel', 'db'), ('naperu-mail', 'roundcube'),
                     ('other-project', 'web'), ('dokploy', 'traefik')):
            candidate = copy.deepcopy(members)
            candidate[WEBID] = member(*role)
            with self.subTest(role=role), self.assertRaises(ops.OpsError):
                ops.validate_smtp_network(current, candidate, require_mail=True)
        for alteration in ('wrong_directory', 'proxy_on_dms', 'mail_alias_missing', 'alias_spoof', 'network_id'):
            candidate = copy.deepcopy(members)
            if alteration == 'wrong_directory':
                candidate[WEBID]['labels']['com.docker.compose.project.working_dir'] = '/somewhere/else'
            elif alteration == 'proxy_on_dms':
                candidate[MAILID]['networks']['dokploy-network'] = {}
            elif alteration == 'mail_alias_missing':
                candidate[MAILID]['networks'][ops.SMTP_NETWORK]['Aliases'] = ['mailserver']
            elif alteration == 'alias_spoof':
                candidate[WEBID]['networks'][ops.SMTP_NETWORK]['Aliases'] = [ops.HOST]
            else:
                candidate[MAILID]['networks'][ops.SMTP_NETWORK]['NetworkID'] = 'f' * 64
            with self.subTest(alteration=alteration), self.assertRaises(ops.OpsError):
                ops.validate_smtp_network(current, candidate, require_mail=True)

    def test_dns_equivalent_mail_aliases_cannot_spoof_or_duplicate_the_owned_alias(self):
        current = network(); current['Containers'] = {MAILID: {}, WEBID: {}}
        original = {MAILID: member('naperu-mail', 'mailserver', aliases=[ops.HOST]),
                    WEBID: member('acropolis-channel', 'web')}
        for alias in ('MAIL.NAPERU.CLOUD', 'mail.naperu.cloud.', 'Mail.Naperu.Cloud...'):
            for target in (MAILID, WEBID):
                candidate = copy.deepcopy(original)
                candidate[target]['networks'][ops.SMTP_NETWORK]['Aliases'].append(alias)
                with self.subTest(alias=alias, target=target), self.assertRaises(ops.OpsError):
                    ops.validate_smtp_network(current, candidate, require_mail=True)
        candidate = copy.deepcopy(original)
        candidate[MAILID]['networks'][ops.SMTP_NETWORK]['Aliases'] = ['MAIL.NAPERU.CLOUD']
        with self.assertRaises(ops.OpsError):
            ops.validate_smtp_network(current, candidate, require_mail=True)

    def test_network_check_cli_requires_mail_by_default_and_allow_empty_is_explicit(self):
        for extra, required in (([], True), (['--allow-empty'], False)):
            with self.subTest(extra=extra), patch.object(ops.sys, 'argv', ['mail-ops.py', 'check-smtp-network', *extra]), \
                 patch.object(ops.os, 'geteuid', return_value=0), \
                 patch.object(ops, 'locked', side_effect=lambda root: contextlib.nullcontext()), \
                 patch.object(ops, 'check_smtp_network', return_value={'owner_verified': True}) as check, \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(ops.main(), 0)
                check.assert_called_once_with(require_mail=required)
        with patch.object(ops.sys, 'argv', ['mail-ops.py', 'prepare-smtp-network', '--allow-empty']), \
             patch.object(ops, 'prepare_smtp_network') as prepare, \
             contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as caught:
                ops.main()
        self.assertEqual(caught.exception.code, 2)
        prepare.assert_not_called()

    def test_check_detects_membership_change_and_does_not_read_container_environment(self):
        first = network(); first['Containers'] = {MAILID: {}}
        current_member = member('naperu-mail', 'mailserver', aliases=[ops.HOST])
        payload = (json.dumps(current_member['labels']) + '\n' + json.dumps(current_member['networks'])).encode()
        changed = network()
        with patch.object(ops, 'read_smtp_network', side_effect=[first, changed]), \
             patch.object(ops, 'run', return_value=payload) as command:
            with self.assertRaisesRegex(ops.OpsError, 'cambió'):
                ops.check_smtp_network(require_mail=True)
        self.assertNotIn('.Config.Env', command.call_args.args[0][3])
        self.assertEqual(command.call_args.args[0][-1], MAILID)

    def test_prepare_creates_only_absent_scoped_network_and_never_reconfigures_existing(self):
        proof = {'owner_verified': True}
        with patch.object(ops, 'read_smtp_network', return_value=None), \
             patch.object(ops, 'check_smtp_network', return_value=proof), \
             patch.object(ops, 'run') as command:
            self.assertIs(ops.prepare_smtp_network(), proof)
        args = command.call_args.args[0]
        self.assertEqual(args[:6], ['docker', 'network', 'create', '--driver', 'bridge', '--internal'])
        self.assertEqual(args[-1], ops.SMTP_NETWORK)
        for key, value in ops.SMTP_NETWORK_LABELS.items():
            self.assertIn(key + '=' + value, args)
        with patch.object(ops, 'read_smtp_network', return_value=network()), \
             patch.object(ops, 'check_smtp_network', side_effect=ops.OpsError('wrong owner')), \
             patch.object(ops, 'run') as command:
            with self.assertRaises(ops.OpsError):
                ops.prepare_smtp_network()
        command.assert_not_called()

    def test_production_mail_tls_uses_loopback_normal_trust_and_never_submission587(self):
        ptr = {'verified': True, 'propagation_pending': False, 'dns_queries': 2}
        with patch.object(ops, 'dns_query', side_effect=lambda resolver, host, kind: [ops.IP] if kind == 'A' else []), \
             patch.object(ops, 'check_ptr_public', return_value=ptr), \
             patch.object(ops, 'check_public_ports', return_value={'public_tcp_ports': [25]}), \
             patch.object(ops, 'check_smtp_network', return_value={'internal': True}) as net, \
             patch.object(ops, 'tls_socket') as tls:
            result = ops.check_public(mail_tls=True)
        net.assert_called_once_with(require_mail=True)
        self.assertEqual(tls.call_args_list, [unittest.mock.call(ops.HOST, 443),
                         unittest.mock.call(ops.WEBMAIL, 443), unittest.mock.call(ops.HOST, 465),
                         unittest.mock.call(ops.HOST, 993)])
        self.assertEqual(result['exposure']['public_tcp_ports'], [25])
        for host, port, address in ((ops.HOST, 443, ops.IP), (ops.WEBMAIL, 443, ops.IP),
                                    (ops.HOST, 465, '127.0.0.1'), (ops.HOST, 993, '127.0.0.1')):
            context = MagicMock()
            with patch.object(ops.ssl, 'create_default_context', return_value=context) as trust, \
                 patch.object(ops.socket, 'create_connection') as connect:
                ops.tls_socket(host, port)
            trust.assert_called_once_with()
            connect.assert_called_once_with((address, port), timeout=12)
            self.assertEqual(context.wrap_socket.call_args.kwargs['server_hostname'], host)
        with self.assertRaises(ops.OpsError):
            ops.tls_socket(ops.HOST, 587)

    def test_probe_preserves_staging_and_uses_private_production_smtp_imap(self):
        self.assertEqual(probe.probe_endpoints(False), {'smtp': ('127.0.0.1', 2465),
                         'imap': ('127.0.0.1', 2993), 'transfer': ('127.0.0.1', 2525)})
        self.assertEqual(probe.probe_endpoints(True), {'smtp': ('127.0.0.1', 465),
                         'imap': ('127.0.0.1', 993), 'transfer': (ops.IP, 25)})
        context = MagicMock()
        client = probe.SMTP.__new__(probe.SMTP); client.context = context
        with patch.object(probe.socket, 'create_connection') as connect:
            client._get_socket('127.0.0.1', 465, 10)
        connect.assert_called_once_with(('127.0.0.1', 465), 10)
        self.assertEqual(context.wrap_socket.call_args.kwargs['server_hostname'], probe.HOST)

    def test_compose_contract_keeps_stage_independent_and_db_webmail_off_app_smtp(self):
        base = yaml.safe_load((SOURCE / 'compose.yml').read_bytes())
        stage = yaml.safe_load((SOURCE / 'stage.yml').read_bytes())
        public = yaml.safe_load((SOURCE / 'public.yml').read_bytes())
        app = yaml.safe_load((SOURCE.parents[1] / 'compose.yml').read_bytes())
        self.assertEqual(set(public['services']['mailserver']['ports']),
                         {ops.IP + ':25:25', '127.0.0.1:465:465', '127.0.0.1:993:993'})
        self.assertNotIn('application_smtp', base['networks'])
        self.assertNotIn('application_smtp', stage.get('networks', {}))
        self.assertNotIn('application_smtp', base['services']['roundcube']['networks'])
        self.assertEqual(set(public['services']['mailserver']['networks']), {'mail', 'application_smtp'})
        self.assertEqual(public['services']['mailserver']['networks']['application_smtp']['aliases'], [ops.HOST])
        self.assertTrue(public['networks']['application_smtp']['external'])
        self.assertEqual(public['networks']['application_smtp']['name'], ops.SMTP_NETWORK)
        self.assertNotIn('application_smtp', app['services']['db']['networks'])
        for service in ('web', 'migrations'):
            self.assertIn('application_smtp', app['services'][service]['networks'])
        self.assertEqual(app['networks']['application_smtp'], {'external': True, 'name': ops.SMTP_NETWORK})
        self.assertTrue(all(binding.startswith('127.0.0.1:') for binding in stage['services']['mailserver']['ports']))


if __name__ == '__main__':
    unittest.main()
