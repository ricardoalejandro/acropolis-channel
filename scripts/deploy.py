#!/usr/bin/env python3
"""Deploy the exact verified image; never rebuild or modify shared infrastructure."""
import argparse
import datetime
import fcntl
import json
import os
from pathlib import Path
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

def command(args, capture=False, env=None, check=True):
    result = subprocess.run(args, cwd=ROOT, env=env, text=True, capture_output=capture, check=check)
    return result.stdout.strip() if capture else result.returncode

def compose(*args, env=None, capture=False, check=True):
    return command(['docker', 'compose', '-p', PROJECT, *args], capture=capture, env=env, check=check)

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

def ready():
    result = subprocess.run(['docker', 'compose', '-p', PROJECT, 'exec', '-T', 'web', 'curl', '--fail', '--silent', '--max-time', '4', 'http://127.0.0.1:8080/health/ready'], cwd=ROOT, capture_output=True, text=True)
    return result.returncode == 0 and json.loads(result.stdout).get('status') == 'ok'

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
        manifest = {'database_backup': str(local / 'backups' / ('deploy-' + timestamp + '.dump')), 'sha': sha, 'image_id': image_id(web_ref), 'migration_image_id': image_id(migration_ref), 'qa_report': str(valid.relative_to(ROOT)), 'previous_image': previous_image, 'previous_routing': previous_route, 'previous_active': previous_active, 'status': 'starting'}
        (backup / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        env = os.environ.copy()
        env.update(APP_IMAGE=web_ref, MIGRATION_IMAGE=migration_ref, IDENTITY_EMAIL_ENABLED=email_mode)
        active = False
        try:
            compose('config', '--quiet', env=env)
            if email_mode == 'true':
                compose('--profile', 'migration', 'run', '--rm', '--no-deps', 'migrations', 'smtp-check', env=env)
            compose('up', '-d', '--no-build', '--wait', '--wait-timeout', '90', 'db', env=env)
            command(['bash', 'scripts/backup-db.sh', '--project', PROJECT, '--database', 'acropolis', '--output', str(local / 'backups' / ('deploy-' + timestamp + '.dump'))], env=env)
            backup_identity_material(backup, previous_container)
            compose('--profile', 'migration', 'run', '--rm', 'migrations', env=env)
            active = True
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
                (backup / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
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
            (backup / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
            shutil.copy2(ROOT / '.env', backup / 'active.env')
            shutil.copy2(ROOT / 'compose.yml', backup / 'compose.yml')
            shutil.copy2(ROUTE, backup / 'active-routing.yml')
            (local / 'last-deployment').write_text(str(backup) + '\n')
            print(json.dumps({'status': 'published', 'sha': sha, 'image_id': manifest['image_id'], 'url': 'https://' + DOMAIN}))
            return 0
        except Exception:
            shutil.copy2(backup / 'candidate.env', ROOT / '.env')
            if previous_route:
                atomic_copy(backup / 'previous-routing.yml', ROUTE)
            elif ROUTE.exists():
                ROUTE.unlink()
            if active:
                if previous_image:
                    override = backup / 'rollback.yml'
                    override.write_text(yaml.safe_dump({'services': {'web': {'image': previous_image}}}))
                    recovery_env = dict(os.environ, IDENTITY_EMAIL_ENABLED=env_values(backup / 'previous.env').get('IDENTITY_EMAIL_ENABLED', 'true'))
                    command(['docker', 'compose', '--project-directory', str(ROOT), '-p', PROJECT, '--env-file', str(backup / 'previous.env'), '-f', str(backup / 'compose.yml'), '-f', str(override), 'up', '-d', '--no-build', '--no-deps', 'web'], env=recovery_env)
                else:
                    compose('stop', 'web', check=False)
            if previous_active:
                (local / 'last-active-deployment').write_text(previous_active + '\n')
            elif (local / 'last-active-deployment').exists():
                (local / 'last-active-deployment').unlink()
            manifest['status'] = 'failed_recovery_applied'
            (backup / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
            raise

if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        print('Deployment stopped: ' + str(error), file=sys.stderr)
        sys.exit(1)
