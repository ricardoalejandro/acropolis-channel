"""Isolated SMTP consumer contract tests; never operates production mail or Docker."""
import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import call, patch

import yaml

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('smtp_network_contract_tests', ROOT / 'scripts/smtp-network.py')
ops = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ops)
NETID = 'a' * 64
MAILID = 'b' * 64
WEBID = 'c' * 64


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
                       'com.docker.compose.project.working_dir': str(ops.PROVIDER_ROOT if mail else ops.APP_ROOT)},
            'networks': networks}


class SmtpNetworkConsumerTests(unittest.TestCase):
    def test_empty_internal_owned_network_is_valid_only_before_mail_attachment(self):
        self.assertFalse(ops.validate_smtp_network(network(), {})['mailserver_attached'])
        with self.assertRaises(ops.NetworkError):
            ops.validate_smtp_network(network(), {}, require_mail=True)
        for change in ({'Name': 'another-network'}, {'Driver': 'overlay'}, {'Internal': False},
                       {'Scope': 'swarm'}, {'EnableIPv6': True}, {'Id': 'untrusted'},
                       {'Labels': {}}, {'Options': {'com.docker.network.bridge.name': 'custom'}}):
            candidate = network(); candidate.update(change)
            with self.subTest(change=change), self.assertRaises(ops.NetworkError):
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
            with self.subTest(role=role), self.assertRaises(ops.NetworkError):
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
            with self.subTest(alteration=alteration), self.assertRaises(ops.NetworkError):
                ops.validate_smtp_network(current, candidate, require_mail=True)
    def test_dns_equivalent_mail_aliases_cannot_spoof_or_duplicate_the_owned_alias(self):
        current = network(); current['Containers'] = {MAILID: {}, WEBID: {}}
        original = {MAILID: member('naperu-mail', 'mailserver', aliases=[ops.HOST]),
                    WEBID: member('acropolis-channel', 'web')}
        for alias in ('MAIL.NAPERU.CLOUD', 'mail.naperu.cloud.', 'Mail.Naperu.Cloud...'):
            for target in (MAILID, WEBID):
                candidate = copy.deepcopy(original)
                candidate[target]['networks'][ops.SMTP_NETWORK]['Aliases'].append(alias)
                with self.subTest(alias=alias, target=target), self.assertRaises(ops.NetworkError):
                    ops.validate_smtp_network(current, candidate, require_mail=True)
        candidate = copy.deepcopy(original)
        candidate[MAILID]['networks'][ops.SMTP_NETWORK]['Aliases'] = ['MAIL.NAPERU.CLOUD']
        with self.assertRaises(ops.NetworkError):
            ops.validate_smtp_network(current, candidate, require_mail=True)
    def test_check_detects_membership_change_and_does_not_read_container_environment(self):
        first = network(); first['Containers'] = {MAILID: {}}
        current_member = member('naperu-mail', 'mailserver', aliases=[ops.HOST])
        payload = (json.dumps(current_member['labels']) + '\n' + json.dumps(current_member['networks'])).encode()
        changed = network()
        with patch.object(ops, 'read_smtp_network', side_effect=[first, changed]), \
             patch.object(ops, 'inspect_output', return_value=payload) as command:
            with self.assertRaisesRegex(ops.NetworkError, 'cambió'):
                ops.check_smtp_network(require_mail=True)
        self.assertNotIn('.Config.Env', command.call_args.args[0][3])
        self.assertEqual(command.call_args.args[0][-1], MAILID)
    def test_cli_requires_mail_unless_allow_empty_is_explicit(self):
        for extra, required in (([], True), (['--allow-empty'], False)):
            with self.subTest(extra=extra), patch.object(ops.sys, 'argv', ['smtp-network.py', *extra]), \
                 patch.object(ops, 'check_smtp_network', return_value={'owner_verified': True}) as check, \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(ops.main(), 0)
                check.assert_called_once_with(require_mail=required)
                self.assertEqual(json.loads(output.getvalue()), {'owner_verified': True})
        with patch.object(ops.sys, 'argv', ['smtp-network.py', 'prepare-smtp-network']), \
             patch.object(ops, 'check_smtp_network') as check, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as caught:
                ops.main()
        self.assertEqual(caught.exception.code, 2)
        check.assert_not_called()

    def test_inspection_boundary_allows_only_exact_network_and_attached_id_metadata(self):
        commands = [ ['docker', 'network', 'inspect', '--format', ops.NETWORK_TEMPLATE, ops.SMTP_NETWORK],
                     ['docker', 'inspect', '--format', ops.MEMBER_TEMPLATE, MAILID] ]
        result = SimpleNamespace(returncode=0, stdout=b'{}', stderr=b'')
        with patch.object(ops.subprocess, 'run', return_value=result) as command:
            for args in commands:
                self.assertEqual(ops.inspect_output(args), b'{}')
        self.assertEqual([item.args[0] for item in command.call_args_list], commands)
        for args in (['docker', 'network', 'create', ops.SMTP_NETWORK],
                     ['docker', 'network', 'connect', ops.SMTP_NETWORK, MAILID],
                     ['docker', 'exec', MAILID, 'postfix', 'reload'],
                     ['docker', 'inspect', '--format', '{{json .Config.Env}}', MAILID],
                     ['docker', 'inspect', '--format', ops.MEMBER_TEMPLATE, 'foreign-container'],
                     ['docker', 'network', 'inspect', '--format', ops.NETWORK_TEMPLATE, 'dokploy-network']):
            with self.subTest(command=args), patch.object(ops.subprocess, 'run') as command:
                with self.assertRaises(ops.NetworkError):
                    ops.inspect_output(args)
                command.assert_not_called()

    def test_command_failures_and_cli_errors_never_expose_provider_response(self):
        args = ['docker', 'network', 'inspect', '--format', ops.NETWORK_TEMPLATE, ops.SMTP_NETWORK]
        result = SimpleNamespace(returncode=1, stdout=b'synthetic-private-output', stderr=b'synthetic-password')
        with patch.object(ops.subprocess, 'run', return_value=result):
            with self.assertRaises(ops.NetworkError) as failure:
                ops.inspect_output(args)
        self.assertNotIn('synthetic-private-output', str(failure.exception))
        self.assertNotIn('synthetic-password', str(failure.exception))
        with patch.object(ops.sys, 'argv', ['smtp-network.py']), \
             patch.object(ops, 'check_smtp_network', side_effect=ValueError('synthetic-password')), \
             contextlib.redirect_stderr(io.StringIO()) as output:
            self.assertEqual(ops.main(), 1)
        self.assertNotIn('synthetic-password', output.getvalue())
        self.assertEqual(json.loads(output.getvalue())['status'], 'blocked')

    def test_unowned_network_blocks_before_reading_any_container_metadata(self):
        candidate = network(); candidate['Labels'] = {}; candidate['Containers'] = {MAILID: {}}
        with patch.object(ops, 'read_smtp_network', return_value=candidate), \
             patch.object(ops, 'inspect_output') as command:
            with self.assertRaises(ops.NetworkError):
                ops.check_smtp_network()
        command.assert_not_called()

    def test_read_network_accepts_only_complete_json_metadata(self):
        candidate = network()
        valid = '\n'.join(json.dumps(candidate[key]) for key in ops.NETWORK_KEYS).encode()
        with patch.object(ops, 'inspect_output', return_value=valid):
            self.assertEqual(ops.read_smtp_network(), candidate)
        for payload in (b'{}', b'not-json', valid + b'\n{}', b'\xff'):
            with self.subTest(payload=payload), patch.object(ops, 'inspect_output', return_value=payload):
                with self.assertRaises(ops.NetworkError):
                    ops.read_smtp_network()

    def test_full_contract_path_uses_only_inspection_and_rechecks_the_same_snapshot(self):
        candidate = network(); candidate['Containers'] = {MAILID: {}}
        current_member = member('naperu-mail', 'mailserver', aliases=[ops.HOST])
        network_payload = '\n'.join(json.dumps(candidate[key]) for key in ops.NETWORK_KEYS).encode()
        member_payload = (json.dumps(current_member['labels']) + '\n' + json.dumps(current_member['networks'])).encode()
        responses = [SimpleNamespace(returncode=0, stdout=data, stderr=b'')
                     for data in (network_payload, member_payload, network_payload)]
        with patch.object(ops.subprocess, 'run', side_effect=responses) as command:
            self.assertTrue(ops.check_smtp_network(require_mail=True)['mailserver_attached'])
        self.assertEqual([item.args[0][:3] for item in command.call_args_list],
                         [['docker', 'network', 'inspect'], ['docker', 'inspect', '--format'],
                          ['docker', 'network', 'inspect']])
        self.assertTrue(all('.Config.Env' not in str(item.args) and '.Mounts' not in str(item.args)
                            for item in command.call_args_list))

    def test_duplicate_provider_and_missing_members_are_rejected(self):
        candidate = network(); candidate['Containers'] = {MAILID: {}, WEBID: {}}
        members = {MAILID: member('naperu-mail', 'mailserver', aliases=[ops.HOST]),
                   WEBID: member('naperu-mail', 'mailserver', aliases=[ops.HOST])}
        with self.assertRaises(ops.NetworkError):
            ops.validate_smtp_network(candidate, members, require_mail=True)
        with self.assertRaises(ops.NetworkError):
            ops.validate_smtp_network(candidate, {MAILID: members[MAILID]}, require_mail=True)

    def test_app_compose_attaches_only_consumers_and_qa_never_uses_productive_network(self):
        app = yaml.safe_load((ROOT / 'compose.yml').read_bytes())
        qa = yaml.safe_load((ROOT / 'compose.qa.yml').read_bytes())
        self.assertNotIn('mailserver', app['services'])
        self.assertNotIn('roundcube', app['services'])
        self.assertNotIn('application_smtp', app['services']['db']['networks'])
        for service in ('web', 'migrations'):
            self.assertIn('application_smtp', app['services'][service]['networks'])
        self.assertEqual(app['networks']['application_smtp'], {'external': True, 'name': ops.SMTP_NETWORK})
        self.assertNotIn('application_smtp', qa.get('networks', {}))
        self.assertFalse(any(ops.SMTP_NETWORK == value.get('name') for value in qa.get('networks', {}).values()))
        self.assertTrue(all('application_smtp' not in value.get('networks', []) for value in qa['services'].values()))



if __name__ == "__main__":
    unittest.main()
