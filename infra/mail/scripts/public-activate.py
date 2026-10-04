#!/usr/bin/env python3
"""Publish only the independent mail stack after verified external prerequisites."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import tempfile
import time

import yaml

ROOT = Path('/root/proyect/naperu-mail')
SOURCE = Path(__file__).resolve().parents[1]
ROUTE = Path('/etc/dokploy/traefik/dynamic/naperu-mail.yml')
PROJECT = 'naperu-mail'
MAIL = PROJECT + '-mailserver-1'
WEBMAIL = PROJECT + '-roundcube-1'
HOST = 'mail.naperu.cloud'
WEB_HOST = 'webmail.naperu.cloud'
IP = '72.61.37.46'
SYSTEM_CA = Path('/etc/ssl/certs/ca-certificates.crt')
CONTAINERS = (MAIL, WEBMAIL)


class ActivationError(RuntimeError):
    def __init__(self, message, recovery=None):
        super().__init__(message)
        self.recovery = recovery or {}


def run(args, *, cwd=None, timeout=30):
    try:
        result = subprocess.run(args, cwd=cwd, text=True, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ActivationError('Required operation could not complete.') from exc
    if result.returncode:
        raise ActivationError('Required operation failed; private output omitted.')
    return result.stdout.strip()


def load_ops(source):
    spec = importlib.util.spec_from_file_location('naperu_mail_ops', source / 'scripts/mail-ops.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def atomic(path, data, mode=0o600):
    fd, name = tempfile.mkstemp(dir=path.parent, prefix='.naperu-mail-')
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, 'wb') as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(name, path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def records(resolver, name, record_type):
    output = run(['dig', '@' + resolver, '+time=4', '+tries=1', '+noall',
                  '+comments', '+answer', name, record_type], timeout=10)
    if not re.search(r'status: NOERROR[,\s]', output):
        raise ActivationError('DNS response not ready: ' + record_type + ' ' + name)
    values = []
    for line in output.splitlines():
        if not line or line.startswith(';'):
            continue
        fields = line.split(None, 4)
        if len(fields) != 5 or fields[0].rstrip('.') != name or fields[2:4] != ['IN', record_type]:
            raise ActivationError('Unexpected DNS answer: ' + record_type + ' ' + name)
        data = fields[4]
        if record_type == 'TXT':
            data = ''.join(shlex.split(data))
        elif record_type == 'MX':
            priority, host = data.split()
            data = priority + ' ' + host.rstrip('.')
        else:
            data = data.rstrip('.')
        values.append(data)
    return values


def check_dns(root, ops, *, all_records):
    expected = json.loads((root / '.local/dns-required.json').read_bytes())
    grouped = {}
    for item in expected:
        if not all_records and item['type'] != 'A':
            continue
        name = 'naperu.cloud' if item['name'] == '@' else item['name'] + '.naperu.cloud'
        value = str(item['priority']) + ' ' + item['value'].rstrip('.') if item['type'] == 'MX' else item['value']
        grouped.setdefault((name, item['type']), set()).add(value)
    for resolver in ('8.8.8.8', '1.1.1.1'):
        for (name, kind), values in grouped.items():
            actual = records(resolver, name, kind)
            if kind == 'TXT':
                # Unrelated verification TXT records may coexist. SPF/DMARC may not conflict.
                prefixes = ('v=spf1', 'v=DMARC1', 'v=DKIM1')
                matching = [prefix for prefix in prefixes if any(v.startswith(prefix) for v in values)]
                relevant = [v for v in actual if any(v.startswith(prefix) for prefix in matching)] if matching else actual
                ready = values.issubset(actual) and (not matching or (set(relevant) == values and len(relevant) == len(values)))
            else:
                ready = set(actual) == values and len(actual) == len(values)
            if not ready:
                raise ActivationError('DNS pending: ' + kind + ' ' + name)
        for host in (HOST, WEB_HOST):
            if records(resolver, host, 'AAAA'):
                raise ActivationError('Unexpected AAAA: ' + host)
    ptr = ops.check_ptr_public() if all_records else None
    if ptr is not None and not ptr.get('verified'):
        raise ActivationError('Public PTR proof did not verify.')
    return {'ptr': ptr, 'propagation_pending': bool(ptr and ptr['propagation_pending'])}


def routing_document(data):
    try:
        document = yaml.safe_load(data)
        if not isinstance(document, dict) or set(document) != {'http'}:
            raise ValueError('document')
        section = document['http']
        if not isinstance(section, dict) or set(section) != {'routers', 'services', 'middlewares'}:
            raise ValueError('http')
        for category in ('routers', 'services', 'middlewares'):
            names = section.get(category, {})
            if not isinstance(names, dict) or any(not n.startswith('naperu-mail-') for n in names):
                raise ValueError('namespace')
        routers = section['routers']
        if set(routers) != {'naperu-mail-http', 'naperu-mail-https'}:
            raise ValueError('routers')
        if any(r.get('service') not in ('noop@internal', 'naperu-mail-webmail') for r in routers.values()):
            raise ValueError('service')
        return document
    except (ValueError, TypeError, KeyError, yaml.YAMLError) as exc:
        raise ActivationError('Owned routing file does not match the mail namespace.') from exc


def route_active(route, source=None):
    if not route.exists():
        return False
    if route.is_symlink() or not route.is_file():
        raise ActivationError('Owned router must be a regular file.')
    document = routing_document(route.read_bytes())
    if source is not None:
        expected = routing_document((source / 'public-routing.yml').read_bytes())
        normalized = yaml.safe_load(yaml.safe_dump(document))
        for router in normalized['http']['routers'].values():
            router['service'] = 'naperu-mail-webmail'
        if normalized != expected:
            raise ActivationError('Owned routing file differs from the approved mail route.')
    services = {r['service'] for r in document['http']['routers'].values()}
    if len(services) != 1:
        raise ActivationError('Owned router has a mixed activation state.')
    return services == {'naperu-mail-webmail'}


def prepared_route(source):
    candidate = routing_document((source / 'public-routing.yml').read_bytes())
    for router in candidate['http']['routers'].values():
        router['service'] = 'noop@internal'
    return yaml.safe_dump(candidate, sort_keys=False).encode()


def check_collisions(route, source):
    candidate = routing_document((source / 'public-routing.yml').read_bytes())['http']
    names = set().union(*(set(candidate.get(k, {})) for k in ('routers', 'services', 'middlewares')))
    for file in list(route.parent.glob('*.yml')) + list(route.parent.glob('*.yaml')):
        if file == route:
            continue
        try:
            section = (yaml.safe_load(file.read_bytes()) or {}).get('http', {})
            other = set().union(*(set(section.get(k, {})) for k in ('routers', 'services', 'middlewares')))
        except (OSError, AttributeError, TypeError, yaml.YAMLError) as exc:
            raise ActivationError('Could not verify routing names without modifying other routes.') from exc
        if names.intersection(other):
            raise ActivationError('Routing collision.')


def check_source(root, source):
    for original, deployed in (
        ('compose.yml', 'compose.yml'), ('public.yml', 'public.yml'),
        ('stage.yml', 'stage.yml'), ('roundcube.inc.php', 'roundcube-config/mail.inc.php'),
        ('user-patches.sh', 'config/user-patches.sh'),
        ('rspamd-dkim.conf', 'config/rspamd/override.d/dkim_signing.conf'),
    ):
        if (source / original).read_bytes() != (root / deployed).read_bytes():
            raise ActivationError('Runtime source drift: ' + deployed)
    original = defer_content((source / 'postfix-main.cf').read_text(), '')
    deployed = defer_content((root / 'config/postfix-main.cf').read_text(), '')
    if original != deployed:
        raise ActivationError('Runtime source drift: config/postfix-main.cf')


def inspect_container(root, name):
    if name not in CONTAINERS:
        raise ActivationError('Container name is outside the mail stack.')
    service = 'mailserver' if name == MAIL else 'roundcube'
    output = run(['docker', 'inspect', '--format',
                  '{{json .Config.Labels}}\n{{json .State}}\n{{json .HostConfig.PortBindings}}', name])
    try:
        labels, current, ports = [json.loads(line) for line in output.splitlines()]
        if (labels.get('com.docker.compose.project') != PROJECT
                or labels.get('com.docker.compose.service') != service
                or labels.get('com.docker.compose.project.working_dir') != str(root)):
            raise ValueError('ownership')
        if not isinstance(current, dict) or not isinstance(ports, (dict, type(None))):
            raise ValueError('state')
        return {'state': current, 'ports': ports}
    except (ValueError, TypeError, AttributeError) as exc:
        raise ActivationError('Container identity does not belong to this mail stack.') from exc


def inspect_services(root):
    return {name: inspect_container(root, name) for name in CONTAINERS}


def container_exec(root, name, command, *, timeout=30):
    if not inspect_container(root, name)['state'].get('Running'):
        raise ActivationError('Owned mail container is not running.')
    return run(['docker', 'exec', name, *command], timeout=timeout)


def wait_healthy(root, *, timeout=240):
    deadline = time.monotonic() + timeout
    while True:
        states = inspect_services(root)
        if all(s['state'].get('Running') and s['state'].get('Health', {}).get('Status') == 'healthy'
               for s in states.values()):
            return
        if any(s['state'].get('Health', {}).get('Status') == 'unhealthy' for s in states.values()):
            raise ActivationError('Mail stack reported unhealthy.')
        if time.monotonic() >= deadline:
            raise ActivationError('Mail stack did not become healthy in time.')
        time.sleep(2)


def cookie_proof(headers):
    values = [h.partition(':')[2].strip() for h in headers if h.lower().startswith('set-cookie:')]
    session = [v for v in values if v.split('=', 1)[0] == 'roundcube_sessid']
    if len(session) != 1:
        raise ActivationError('Webmail session cookie was not observed.')
    attrs = {p.strip().lower() for p in session[0].split(';')[1:]}
    if not {'secure', 'httponly', 'samesite=lax'}.issubset(attrs):
        raise ActivationError('Webmail session cookie requires Secure, HttpOnly and SameSite=Lax.')
    return {'secure': True, 'http_only': True, 'same_site': 'Lax'}


def verify_webmail(root=ROOT):
    # PHP sees exactly the new public environment, without opening the public router.
    code = ('$ctx=stream_context_create(["http"=>["header"=>"Host: webmail.naperu.cloud\r\n",'
            '"timeout"=>10,"follow_location"=>0]]);$h=get_headers("http://127.0.0.1/",false,$ctx);'
            'if($h===false){exit(1);}echo json_encode($h);')
    try:
        headers = json.loads(container_exec(root, WEBMAIL, ['php', '-r', code], timeout=15))
        if not headers or not re.match(r'HTTP/\S+ 200\b', headers[0]):
            raise ValueError('status')
    except (ValueError, TypeError) as exc:
        raise ActivationError('Webmail private HTTP proof failed.') from exc
    return cookie_proof(headers)


def postfix_values(root=ROOT):
    names = ['default_transport', 'relay_transport', 'smtp_transport_rate_delay',
             'smtp_destination_recipient_limit', 'mynetworks', 'transport_maps',
             'sender_dependent_default_transport_maps', 'milter_default_action', 'defer_transports']
    output = container_exec(root, MAIL, ['postconf', *names])
    return {line.partition('=')[0].strip(): line.partition('=')[2].strip()
            for line in output.splitlines() if '=' in line}


def require_postfix(root=ROOT, *, deferred=True):
    expected = {'default_transport': 'smtp', 'relay_transport': 'smtp',
                'smtp_transport_rate_delay': '16s', 'smtp_destination_recipient_limit': '1',
                'mynetworks': '127.0.0.0/8', 'transport_maps': '',
                'sender_dependent_default_transport_maps': '', 'milter_default_action': 'tempfail'}
    values = postfix_values(root)
    if any(values.get(k) != v for k, v in expected.items()):
        raise ActivationError('Postfix relay, pacing or DKIM safeguards differ from the approved configuration.')
    if values.get('defer_transports') not in ('', 'smtp'):
        raise ActivationError('Unexpected SMTP defer state.')
    if deferred and values.get('defer_transports') != 'smtp':
        raise ActivationError('SMTP must remain deferred until public verification completes.')


def compose(root, overlay):
    inspect_services(root)
    run(['docker', 'compose', '--project-name', PROJECT, '--env-file', str(root / 'runtime.env'),
         '-f', str(root / 'compose.yml'), '-f', str(root / overlay), 'up', '-d'],
        cwd=root, timeout=120)


def defer_content(content, value):
    lines = re.findall(r'^defer_transports\s*=.*$', content, flags=re.M)
    if len(lines) != 1:
        raise ActivationError('Postfix source must contain one explicit defer_transports setting.')
    return re.sub(r'^defer_transports\s*=.*$', 'defer_transports = ' + value, content, flags=re.M).encode()


def fail_closed(root, ops):
    """Block deliveries on disk and live qmgr; stop only owned DMS if reload cannot be proven."""
    recovery = {'delivery_disabled': False, 'mail_stopped': False}
    try:
        conf = root / 'config/postfix-main.cf'
        atomic(conf, defer_content(conf.read_text(), 'smtp'))
        ops.container_state(MAIL)  # Identity check precedes every emergency mutation.
        container_exec(root, MAIL, ['postconf', '-e', 'defer_transports=smtp'])
        container_exec(root, MAIL, ['postfix', 'reload'])
        if postfix_values(root).get('defer_transports') != 'smtp':
            raise ActivationError('Deferred state was not restored.')
        recovery['delivery_disabled'] = True
    except Exception:
        try:
            current = inspect_container(root, MAIL)
            if ops.container_state(MAIL) and current['state'].get('Running'):
                run(['docker', 'stop', '--time', '30', MAIL], timeout=60)
            recovery.update(delivery_disabled=True, mail_stopped=True)
        except Exception:
            recovery['recovery_required'] = True
    return recovery


def state(root, phase, **details):
    payload = {'schema': 1, 'phase': phase, **details}
    atomic(root / '.local/publication-state.json', json.dumps(payload, sort_keys=True).encode() + b'\n')
    return payload


def ensure_started(root):
    marker = root / '.local/public-started'
    if marker.is_symlink() or (marker.exists() and not marker.is_file()):
        raise ActivationError('Public-started marker must be an owned regular file.')
    if not marker.exists():
        atomic(marker, b'1\n')


def record_recovery(root, phase, recovery):
    try:
        state(root, phase, **recovery)
    except Exception:
        recovery.update(state_recorded=False, recovery_required=True)


def activate(operation, *, root=ROOT, route=ROUTE, source=SOURCE, ops=None):
    """Testable entry point; all external effects use run() and an injected operations module."""
    if operation not in ('routing', 'publish', 'allow-delivery'):
        raise ActivationError('Unknown activation operation.')
    ops = ops or load_ops(source)
    with ops.locked(root):
        active = route_active(route, source)
        check_collisions(route, source)
        if operation == 'routing' and active:
            return {'routing_prepared': True, 'active_preserved': True, 'public_router_unchanged': True}
        dns = check_dns(root, ops, all_records=operation != 'routing')
        if operation == 'routing':
            snapshot = root / '.local/routing-before.yml'
            if route.exists() and not snapshot.exists():
                atomic(snapshot, route.read_bytes())
            atomic(route, prepared_route(source), 0o644)
            state(root, 'prepared', external_delivery_enabled=False)
            return {'routing_prepared': True, 'certificate_pending': True, 'public_smtp': False}
        if not route.exists():
            raise ActivationError('Prepare the certificate router before publishing.')
        check_source(root, source)
        inspect_services(root)  # No action may target an unowned container.
        ops.check_public()
        ops.export_cert(root=root)
        if operation == 'publish' and active:
            wait_healthy(root)
            cookie = verify_webmail(root)
            ops.check_public(mail_tls=True)
            require_postfix(root, deferred=False)
            try:
                ensure_started(root)
            except Exception as exc:
                recovery = fail_closed(root, ops)
                recovery.update(public_router_active=True, active_preserved=True,
                                public_inbound_may_be_open=True)
                record_recovery(root, 'failed_publication', recovery)
                raise ActivationError('Publication marker failed; owned recovery state is reported.', recovery) from exc
            return {'public_ports_started': True, 'active_preserved': True,
                    'cookie': cookie, **dns}
        if operation == 'publish':
            require_postfix(root)
            previous_route = route.read_bytes()
            bundle = root / 'roundcube-trust/ca-bundle.pem'
            previous_bundle = bundle.read_bytes()
            public_attempted = False
            try:
                state(root, 'publishing', external_delivery_enabled=False)
                atomic(bundle, SYSTEM_CA.read_bytes(), 0o644)
                public_attempted = True
                compose(root, 'public.yml')
                wait_healthy(root)
                require_postfix(root)
                cookie = verify_webmail(root)
                ops.check_public(mail_tls=True)
                # Only the complete proof switches noop to the webmail backend.
                atomic(route, (source / 'public-routing.yml').read_bytes(), 0o644)
                state(root, 'public', external_delivery_enabled=False, cookie=cookie, **dns)
                ensure_started(root)
                return {'public_ports_started': True, 'external_delivery_still_deferred': True,
                        'cookie': cookie, **dns}
            except Exception as exc:
                recovery = fail_closed(root, ops)
                try:
                    atomic(route, previous_route, 0o644)
                    recovery['public_router_active'] = False
                except Exception:
                    recovery.update(public_router_active=None, recovery_required=True)
                    try:
                        recovery['public_router_active'] = route_active(route, source)
                    except Exception:
                        pass
                    # If the router cannot be restored, make its owned backend unavailable.
                    try:
                        if inspect_container(root, WEBMAIL)['state'].get('Running'):
                            run(['docker', 'stop', '--time', '30', WEBMAIL], timeout=60)
                        recovery['webmail_stopped'] = True
                    except Exception:
                        recovery['webmail_stopped'] = False
                try:
                    atomic(bundle, previous_bundle, 0o644)
                    recovery['trust_restored'] = True
                except Exception:
                    recovery.update(trust_restored=False, recovery_required=True)
                if public_attempted and recovery.get('public_router_active') is False:
                    try:
                        compose(root, 'stage.yml')
                        # A stopped DMS may be restarted by compose; reassert its durable defer.
                        recovery.update(fail_closed(root, ops))
                        if not recovery['delivery_disabled']:
                            raise ActivationError('Rollback SMTP defer could not be verified.')
                        bindings = inspect_services(root)
                        for current in bindings.values():
                            if any(b.get('HostIp') not in ('127.0.0.1', '::1')
                                   for items in (current['ports'] or {}).values() for b in (items or [])):
                                raise ActivationError('Public ports remain bound.')
                        recovery['public_inbound_may_be_open'] = False
                    except Exception:
                        recovery.update(public_inbound_may_be_open=True, recovery_required=True)
                elif public_attempted:
                    recovery.update(public_inbound_may_be_open=True, recovery_required=True)
                record_recovery(root, 'failed_publication', recovery)
                raise ActivationError('Publication failed; owned recovery state is reported.', recovery) from exc
        if operation != 'allow-delivery' or not active:
            raise ActivationError('External delivery requires an already verified public router.')
        wait_healthy(root)
        cookie = verify_webmail(root)
        ops.check_public(mail_tls=True)
        require_postfix(root, deferred=False)
        conf = root / 'config/postfix-main.cf'
        try:
            state(root, 'enabling_delivery', external_delivery_enabled=False)
            atomic(conf, defer_content(conf.read_text(), ''))
            ops.container_state(MAIL)
            container_exec(root, MAIL, ['postconf', '-e', 'defer_transports='])
            container_exec(root, MAIL, ['postfix', 'reload'])
            if postfix_values(root).get('defer_transports') != '':
                raise ActivationError('Delivery activation could not be verified.')
            state(root, 'delivery_enabled', external_delivery_enabled=True, cookie=cookie, **dns)
            return {'external_delivery_enabled': True, 'application_email_unchanged': True, **dns}
        except Exception as exc:
            recovery = fail_closed(root, ops)
            record_recovery(root, 'failed_delivery', recovery)
            raise ActivationError('Delivery activation failed; SMTP blocked or recovery required.', recovery) from exc


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['routing', 'publish', 'allow-delivery'])
    args = parser.parse_args()
    os.umask(0o077)
    try:
        if os.geteuid() != 0:
            raise ActivationError('Administrative activation requires root.')
        print(json.dumps(activate(args.operation), sort_keys=True))
        return 0
    except Exception as exc:
        payload = {'status': 'blocked', 'reason': str(exc) if isinstance(exc, ActivationError)
                   else 'Required operation failed; private output omitted.'}
        if isinstance(exc, ActivationError) and exc.recovery:
            payload['recovery'] = exc.recovery
        print(json.dumps(payload, sort_keys=True))
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
