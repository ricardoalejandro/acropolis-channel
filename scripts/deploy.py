#!/usr/bin/env python3
"""Deploy the exact verified image; never rebuild or modify shared infrastructure."""
import argparse
import datetime
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import urllib.request
import urllib.error
import yaml

ROOT = Path(__file__).resolve().parents[1]
ROUTE = Path('/etc/dokploy/traefik/dynamic/acropolis-channel.yml')
DOMAIN = 'acropolischannel.naperu.cloud'
PROJECT = 'acropolis-channel'

def command(args, capture=False, env=None, check=True, timeout=None):
    result = subprocess.run(args, cwd=ROOT, env=env, text=True, capture_output=capture, check=check, timeout=timeout)
    return result.stdout.strip() if capture else result.returncode

def compose(*args, env=None, capture=False, check=True):
    return command(['docker', 'compose', '-p', PROJECT, *args], capture=capture, env=env, check=check)

def validate_compose_smtp_network(settings):
    # Compose attaches this network even when email flows are disabled.
    own_mail = (settings.get('IDENTITY_EMAIL_ENABLED', 'true').lower() == 'true'
                and settings.get('IDENTITY_SMTP_HOST') == 'mail.naperu.cloud')
    arguments = ['python3', str(ROOT / 'scripts/smtp-network.py')]
    if not own_mail:
        arguments.append('--allow-empty')
    try:
        command(arguments, capture=True)
    except (OSError, subprocess.CalledProcessError):
        raise RuntimeError('Private SMTP network is unavailable or outside the owned topology; private output suppressed') from None


def image_id(ref):
    return command(['docker', 'image', 'inspect', ref, '--format', '{{.Id}}'], capture=True)

def env_values(path=None):
    values = {}
    for line in (path or ROOT / '.env').read_text().splitlines():
        if line.strip() and not line.lstrip().startswith('#') and '=' in line:
            key, value = line.split('=', 1)
            values[key.strip()] = value.strip().strip('"').strip("'")
    return values

def set_image_refs(web, migration):
    path = ROOT / '.env'
    lines = [l for l in path.read_text().splitlines() if not l.startswith(('APP_IMAGE=', 'MIGRATION_IMAGE='))]
    lines += ['APP_IMAGE=' + web, 'MIGRATION_IMAGE=' + migration]
    temporary = path.with_name('.env.deploy.tmp')
    temporary.write_text('\n'.join(lines) + '\n')
    temporary.chmod(0o600)
    temporary.replace(path)

def atomic_copy(source, target):
    temporary = target.with_name(target.name + '.tmp')
    shutil.copyfile(source, temporary)
    temporary.chmod(0o644)
    temporary.replace(target)

def ready(compose_args=None, env=None):
    prefix = compose_args or ['docker', 'compose', '-p', PROJECT]
    result = subprocess.run([*prefix, 'exec', '-T', 'web', 'curl', '--fail', '--silent', '--max-time', '4', 'http://127.0.0.1:8080/health/ready'], cwd=ROOT, env=env, capture_output=True, text=True, timeout=10)
    if result.returncode != 0:
        return False
    payload = json.loads(result.stdout)
    return isinstance(payload, dict) and payload.get('status') == 'ok'


def write_manifest(directory, manifest):
    temporary = directory / 'manifest.json.tmp'
    temporary.write_text(json.dumps(manifest, indent=2) + '\n')
    temporary.chmod(0o600)
    temporary.replace(directory / 'manifest.json')


def recover_previous_deployment(directory, previous_image, previous_active, previous_route, restart_required):
    # Forward migrations may already have committed, even if the runner failed.
    # Recover only a runtime that proves it accepts the current database history.
    recovery = {'verified': False, 'imageVerified': False, 'readinessVerified': False,
                'configurationRestored': False, 'routingRestored': False,
                'activePointerRestored': False, 'stopRequested': False,
                'stopCommandSucceeded': False, 'webStopped': False,
                'maintenanceRequired': True, 'restartRequired': restart_required, 'errors': []}

    def record_error(stage, error):
        # No exception message, command arguments, env or private output is retained.
        recovery['errors'].append({'stage': stage, 'errorType': type(error).__name__})

    try:
        # Preserve configuration prepared before this attempt, including new key-protector settings.
        # The recovered container uses its separate previous.env snapshot below.
        shutil.copy2(directory / 'candidate.env', ROOT / '.env')
        (ROOT / '.env').chmod(0o600)
        recovery['configurationRestored'] = True
    except Exception as error:
        record_error('restore-configuration', error)
    try:
        if previous_route:
            atomic_copy(directory / 'previous-routing.yml', ROUTE)
        else:
            ROUTE.unlink(missing_ok=True)
        recovery['routingRestored'] = True
    except Exception as error:
        record_error('restore-routing', error)

    prefix = ['docker', 'compose', '--project-directory', str(ROOT), '-p', PROJECT,
              '--env-file', str(directory / 'previous.env'), '-f', str(directory / 'compose.yml')]
    if previous_image and not recovery['errors']:
        stage = 'prepare-previous-runtime'
        try:
            override = directory / 'rollback.yml'
            override.write_text(yaml.safe_dump({'services': {'web': {'image': previous_image}}}))
            previous_prefix = [*prefix, '-f', str(override)]
            recovery_env = dict(os.environ, IDENTITY_EMAIL_ENABLED=env_values(directory / 'previous.env').get('IDENTITY_EMAIL_ENABLED', 'true'))
            if restart_required:
                stage = 'start-previous-runtime'
                command([*previous_prefix, 'up', '-d', '--no-build', '--no-deps', 'web'], env=recovery_env, capture=True, timeout=30)
            stage = 'verify-previous-image'
            container = command([*previous_prefix, 'ps', '-q', 'web'], env=recovery_env, capture=True, timeout=30)
            if not container or any(character.isspace() for character in container):
                raise RuntimeError('Previous web runtime was not uniquely identified.')
            actual = command(['docker', 'inspect', container, '--format', '{{.Image}}|{{.State.Running}}'], capture=True, timeout=30)
            if actual != previous_image + '|true':
                raise RuntimeError('Previous web runtime image or running state does not match.')
            recovery['imageVerified'] = True
            stage = 'verify-previous-readiness'
            for attempt in range(30):
                if ready(compose_args=previous_prefix, env=recovery_env):
                    recovery['readinessVerified'] = True
                    break
                if attempt != 29:
                    time.sleep(3)
            if not recovery['readinessVerified']:
                raise RuntimeError('Previous web runtime does not accept the current database state.')
            stage = 'restore-active-pointer'
            pointer = ROOT / '.local/last-active-deployment'
            if previous_active:
                pointer.write_text(previous_active + '\n')
                recovery['activePointerRestored'] = True
            else:
                pointer.unlink(missing_ok=True)
            recovery['verified'] = True
            recovery['maintenanceRequired'] = False
        except Exception as error:
            record_error(stage, error)
    elif not previous_image:
        recovery['reason'] = 'no_previous_image'

    if not recovery['verified']:
        # Stop only this project's web; keep the database, snapshots and published history.
        recovery['stopRequested'] = True
        try:
            command([*prefix, 'stop', 'web'], capture=True, timeout=30)
            recovery['stopCommandSucceeded'] = True
        except Exception as error:
            record_error('stop-own-web', error)
        if recovery['stopCommandSucceeded']:
            stage = 'list-own-web-after-stop'
            try:
                output = command([*prefix, 'ps', '-a', '-q', 'web'], capture=True, timeout=30)
                containers = output.splitlines() if output else []
                if len(set(containers)) != len(containers) or any(not re.fullmatch(r'[a-f0-9]{64}', item) for item in containers):
                    raise RuntimeError('Own web container inventory could not be verified.')
                recovery['stopObservedContainers'] = len(containers)
                stage = 'verify-own-web-state'
                for container in containers:
                    running = command(['docker', 'inspect', container, '--format', '{{.State.Running}}'], capture=True, timeout=30)
                    if running != 'false':
                        raise RuntimeError('Own web is running or its stopped state could not be verified.')
                recovery['webStopped'] = True
            except Exception as error:
                record_error(stage, error)
        try:
            (ROOT / '.local/last-active-deployment').unlink(missing_ok=True)
        except Exception as error:
            record_error('clear-active-pointer', error)
    return recovery

def dns_ok(expected):
    try:
        for resolver in ['https://dns.google/resolve', 'https://cloudflare-dns.com/dns-query']:
            request = urllib.request.Request(resolver + '?name=' + DOMAIN + '&type=A', headers={'Accept': 'application/dns-json'})
            with urllib.request.urlopen(request, timeout=10) as response:
                data = json.load(response)
            addresses = {a['data'] for a in data.get('Answer', []) if a['type'] == 1}
            if data.get('Status') != 0 or addresses != {expected}:
                return False
        return True
    except Exception:
        return False

def routing_check():
    template = ROOT / 'infra/traefik/acropolis-channel.yml'
    own = yaml.safe_load(template.read_text())
    assert own['http']['services']['acropolis-channel-web']['loadBalancer']['servers'][0]['url'] == 'http://acropolis-channel-web:8080'
    for file in ROUTE.parent.glob('*.y*ml'):
        if file == ROUTE:
            continue
        data = yaml.safe_load(file.read_text()) or {}
        http = data.get('http', {})
        if any(DOMAIN in str(router.get('rule', '')) for router in http.get('routers', {}).values()):
            raise RuntimeError('Domain already belongs to another routing file; refusing overwrite')
        for kind in ['routers', 'services', 'middlewares']:
            if set(http.get(kind, {})) & set(own['http'].get(kind, {})):
                raise RuntimeError('Own routing names collide with another service')
    return template

def public_smoke():
    for path, wanted in [('/api/v1/greeting', {'message': 'Hola mundo'}), ('/health', {'status': 'ok'}), ('/health/ready', {'status': 'ok'})]:
        with urllib.request.urlopen('https://' + DOMAIN + path, timeout=10) as response:
            if response.status != 200 or json.load(response) != wanted:
                raise RuntimeError('Unexpected public API response')
    with urllib.request.urlopen('https://' + DOMAIN + '/', timeout=10) as response:
        if response.status != 200 or 'id="root"' not in response.read().decode():
            raise RuntimeError('Frontend entrypoint missing')
        if response.headers.get('X-Content-Type-Options') != 'nosniff':
            raise RuntimeError('Security headers missing')
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None
    try:
        urllib.request.build_opener(NoRedirect).open('http://' + DOMAIN + '/', timeout=10)
        raise RuntimeError('HTTP did not redirect')
    except urllib.error.HTTPError as response:
        if response.code not in (301, 308) or response.headers.get('Location') != 'https://' + DOMAIN + '/':
            raise RuntimeError('Unexpected HTTPS redirection')

def backup_identity_material(directory, previous_container):
    protector = ROOT / '.local/identity/key-protector.pfx'
    if not protector.is_file():
        return
    target = directory / 'identity'
    target.mkdir(mode=0o700)
    shutil.copy2(protector, target / 'key-protector.pfx')
    (target / 'key-protector.pfx').chmod(0o600)
    if previous_container:
        result = command(['docker', 'exec', previous_container, 'test', '-d',
                          '/var/acropolis/keys'], check=False)
        if result == 0:
            keyring = target / 'keyring'
            keyring.mkdir(mode=0o700)
            command(['docker', 'cp', previous_container + ':/var/acropolis/keys/.', str(keyring)])
            for path in keyring.rglob('*'):
                path.chmod(0o700 if path.is_dir() else 0o600)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--expected-sha', required=True)
    args = parser.parse_args()
    os.chdir(ROOT)
    local = ROOT / '.local'
    local.mkdir(exist_ok=True)
    with (local / 'deploy.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if command(['git', 'status', '--porcelain'], capture=True):
            raise RuntimeError('Checkout must be clean before deployment')
        if command(['git', 'branch', '--show-current'], capture=True) != 'main':
            raise RuntimeError('Deployment requires main')
        remote = command(['git', 'remote', 'get-url', 'origin'], capture=True)
        if remote.rstrip('/').removesuffix('.git') != 'https://github.com/ricardoalejandro/acropolis-channel':
            raise RuntimeError('Unexpected origin')
        command(['git', 'fetch', 'origin'])
        command(['git', 'pull', '--ff-only', 'origin', 'main'])
        sha = command(['git', 'rev-parse', 'HEAD'], capture=True)
        if sha != args.expected_sha or sha != command(['git', 'rev-parse', 'origin/main'], capture=True):
            raise RuntimeError('Checkout differs from the expected validated commit')
        web_ref = 'acropolis-channel:' + sha
        migration_ref = 'acropolis-channel-migrations:' + sha
        reports = sorted((local / 'qa' / sha).glob('*/report.json'), key=lambda p: p.stat().st_mtime, reverse=True)
        valid = None
        for report_path in reports:
            report = json.loads(report_path.read_text())
            try:
                matches = report.get('sha') == sha and report.get('status') == 'passed' and report.get('deployment_eligible') is True and report.get('image_id') == image_id(web_ref) and report.get('migration_image_id') == image_id(migration_ref)
            except subprocess.CalledProcessError:
                matches = False
            if matches:
                valid = report_path
                break
        if valid is None:
            raise RuntimeError('No matching passed QA report; run scripts/verify.sh first')
        command(['python3', 'scripts/identity-runtime.py', '--check'])
        template = routing_check()
        settings = env_values()
        email_mode = settings.get('IDENTITY_EMAIL_ENABLED', 'true').lower()
        if email_mode not in ('true', 'false'):
            raise RuntimeError('IDENTITY_EMAIL_ENABLED must be true or false')
        validate_compose_smtp_network(settings)
        expected_ip = settings.get('PUBLIC_VPS_IPV4')
        if not expected_ip:
            raise RuntimeError('Configure PUBLIC_VPS_IPV4 privately')
        publication_allowed = dns_ok(expected_ip)
        timestamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
        backup = local / 'deployments' / timestamp
        backup.mkdir(parents=True)
        backup.chmod(0o700)
        shutil.copy2(ROOT / '.env', backup / 'candidate.env')
        (backup / 'candidate.env').chmod(0o600)
        shutil.copy2(ROOT / '.env', backup / 'previous.env')
        shutil.copy2(ROOT / 'compose.yml', backup / 'compose.yml')
        previous_container = compose('ps', '-q', 'web', capture=True)
        previous_image = command(['docker', 'inspect', previous_container, '--format', '{{.Image}}'], capture=True) if previous_container else None
        # Recover the configuration that was actually used with the prior image.
        previous_active = (local / 'last-active-deployment').read_text().strip() if (local / 'last-active-deployment').exists() else None
        prior_pointer = local / 'last-active-deployment' if (local / 'last-active-deployment').exists() else local / 'last-deployment'
        if previous_image and prior_pointer.exists():
            prior = Path(prior_pointer.read_text().strip()).resolve()
            if not prior.is_relative_to((local / 'deployments').resolve()):
                raise RuntimeError('Invalid prior deployment path')
            prior_manifest = json.loads((prior / 'manifest.json').read_text())
            if prior_manifest.get('image_id') != previous_image:
                raise RuntimeError('Running image differs from recorded prior deployment')
            shutil.copy2(prior / 'active.env', backup / 'previous.env')
            shutil.copy2(prior / 'active-compose.yml', backup / 'compose.yml')
        previous_route = ROUTE.exists()
        if previous_route:
            shutil.copy2(ROUTE, backup / 'previous-routing.yml')
        if previous_image:
            command(['docker', 'tag', previous_image, 'acropolis-channel:recovery-' + timestamp.lower()])
        manifest = {'database_backup': str(local / 'backups' / ('deploy-' + timestamp + '.dump')), 'sha': sha, 'image_id': image_id(web_ref), 'migration_image_id': image_id(migration_ref), 'qa_report': str(valid.relative_to(ROOT)), 'previous_image': previous_image, 'previous_routing': previous_route, 'previous_active': previous_active, 'migration_attempted': False, 'status': 'starting'}
        write_manifest(backup, manifest)
        env = os.environ.copy()
        env.update(APP_IMAGE=web_ref, MIGRATION_IMAGE=migration_ref, IDENTITY_EMAIL_ENABLED=email_mode)
        try:
            compose('config', '--quiet', env=env)
            if email_mode == 'true':
                compose('--profile', 'migration', 'run', '--rm', '--no-deps', 'migrations', 'smtp-check', env=env)
            compose('up', '-d', '--no-build', '--wait', '--wait-timeout', '90', 'db', env=env)
            command(['bash', 'scripts/backup-db.sh', '--project', PROJECT, '--database', 'acropolis', '--output', str(local / 'backups' / ('deploy-' + timestamp + '.dump'))], env=env)
            backup_identity_material(backup, previous_container)
            manifest['migration_attempted'] = True
            write_manifest(backup, manifest)
            compose('--profile', 'migration', 'run', '--rm', 'migrations', env=env)
            compose('up', '-d', '--no-build', 'web', env=env)
            set_image_refs(web_ref, migration_ref)
            for _ in range(30):
                if ready():
                    break
                time.sleep(3)
            else:
                raise RuntimeError('Readiness failed after deployment')
            shutil.copy2(ROOT / '.env', backup / 'active.env')
            shutil.copy2(ROOT / 'compose.yml', backup / 'active-compose.yml')
            (local / 'last-active-deployment').write_text(str(backup) + '\n')
            if not publication_allowed:
                manifest['status'] = 'internal_ready_dns_blocked'
                write_manifest(backup, manifest)
                print('Application is internally ready; DNS blocks new publication. Existing routing preserved.')
                return 2
            atomic_copy(template, ROUTE)
            for attempt in range(36):
                try:
                    public_smoke()
                    break
                except Exception:
                    if attempt == 35:
                        raise RuntimeError('Public HTTPS verification failed') from None
                    time.sleep(5)
            command(['docker', 'tag', web_ref, 'acropolis-channel:local'])
            manifest['status'] = 'published'
            manifest['published_at'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
            write_manifest(backup, manifest)
            shutil.copy2(ROOT / '.env', backup / 'active.env')
            shutil.copy2(ROOT / 'compose.yml', backup / 'compose.yml')
            shutil.copy2(ROUTE, backup / 'active-routing.yml')
            (local / 'last-deployment').write_text(str(backup) + '\n')
            print(json.dumps({'status': 'published', 'sha': sha, 'image_id': manifest['image_id'], 'url': 'https://' + DOMAIN}))
            return 0
        except Exception as error:
            manifest['deployment_error_type'] = type(error).__name__
            recovery = recover_previous_deployment(backup, previous_image, previous_active, previous_route, manifest['migration_attempted'])
            manifest['recovery'] = recovery
            manifest['status'] = 'failed_recovery_applied' if recovery['verified'] else 'recovery_incomplete'
            write_manifest(backup, manifest)
            raise

if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print('Deployment stopped: ' + str(error), file=sys.stderr)
        sys.exit(1)
