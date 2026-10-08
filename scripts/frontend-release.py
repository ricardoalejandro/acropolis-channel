#!/usr/bin/env python3
"""Fail-closed evidence for a small editorial frontend release, never a full gate."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
SCOPE = 'frontend-low-risk'
# Explicitly reviewed editorial surface, tests, and the release procedure itself.
EDITOR = 'frontend/src/features/catalog/ContentAdmin.tsx'
ALLOWED = {
    EDITOR, 'frontend/src/features/catalog/Catalog.test.tsx', 'frontend/src/Modernization.test.tsx',
    'frontend/e2e/catalog.spec.ts', 'frontend/e2e/modernization.spec.ts',
    'frontend/e2e/topics.spec.ts', 'frontend/e2e/youtube-url.spec.ts',
    'scripts/verify.sh', 'scripts/verify-frontend.sh', 'scripts/frontend-release.py',
    'scripts/deploy.py', 'tests/deploy/test_verify.py', 'tests/deploy/test_deploy.py',
    'AGENTS.md', 'docs/quality.md', '.agents/skills/acropolis-quality/SKILL.md',
    '.agents/skills/acropolis-vps-deploy/SKILL.md',
}

FRESH_STEPS = {
    'scope_admission', 'static_checks', 'deployment_orchestration', 'compose_validation',
    'build_node_runner', 'frontend_quality', 'frontend_coverage', 'frontend_assets', 'build_candidate',
    'runtime_image_security', 'backend_equivalence', 'private_proxy_start', 'private_pki',
    'database_start', 'migrations_first', 'migrations_repeat', 'synthetic_accounts',
    'bootstrap_qa_owner', 'grant_qa_editor', 'application_start', 'readiness_initial', 'strict_private_tls',
    'editor_desktop', 'editor_mobile', 'scope_recheck',
}


def command(args, root=ROOT):
    return subprocess.check_output(args, cwd=root, text=True).strip()


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def private_path(root, name):
    path = Path(name)
    if not path.is_absolute():
        path = root / path
    if path.is_symlink():
        raise ValueError('Evidence may not be a symlink')
    path = path.resolve()
    if not path.is_relative_to((root / '.local').resolve()) or not path.is_file() or path.is_symlink():
        raise ValueError('Evidence must be a regular private artifact')
    return path


def approved(report):
    if (report.get('status') != 'passed' or report.get('deployment_eligible') is not True
            or report.get('cleanup_complete') is not True or report.get('working_tree') is not False
            or report.get('supervisor_review_pending') is True):
        raise ValueError('Baseline is not an approved closed release')


def changed_paths(root, base, sha):
    if not re.fullmatch(r'[a-f0-9]{40}', base) or not re.fullmatch(r'[a-f0-9]{40}', sha):
        raise ValueError('Invalid source SHA')
    command(['git', 'merge-base', '--is-ancestor', base, sha], root)
    rows = command(['git', 'diff', '--name-status', '--no-renames', base, sha], root).splitlines()
    names = []
    for row in rows:
        status, name = row.split('\t')
        if status not in ('A', 'M') or name not in ALLOWED:
            raise ValueError('Change exceeds the editorial frontend scope: ' + name)
        names.append(name)
    if EDITOR not in names:
        raise ValueError('Scoped release requires an actual editorial frontend change')
    return sorted(names)


def image_info(ref, root=ROOT):
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', ref):
        raise ValueError('Image inheritance requires an immutable ID')
    return json.loads(command(['docker', 'image', 'inspect', ref], root))[0]


def baseline(root, manifest):
    path = private_path(root, manifest['qa_report'])
    report = json.loads(path.read_text())
    approved(report)
    if any(report.get(k) != manifest.get(k) for k in ('sha', 'image_id', 'migration_image_id')):
        raise ValueError('Active runtime and baseline certificate differ')
    if report.get('scope') == SCOPE:
        inherited = report['inherited_backend']
        path = private_path(root, inherited['report_path'])
        if digest(path) != inherited['report_sha256']:
            raise ValueError('Inherited backend certificate changed')
        report = json.loads(path.read_text())
        approved(report)
    if report.get('scope') not in (None, 'same-sha-tail'):
        raise ValueError('Backend anchor must have completed the full gate')
    proof = {'sha': report['sha'], 'image_id': report['image_id'],
             'migration_image_id': report['migration_image_id'],
             'report_path': str(path), 'report_sha256': digest(path)}
    if report.get('scope') == 'same-sha-tail':
        provenance_path = private_path(root, report['reuse_provenance'])
        if digest(provenance_path) != report['reuse_provenance_sha256']:
            raise ValueError('Baseline reuse provenance changed')
        provenance = json.loads(provenance_path.read_text())
        if provenance.get('sourceSha') != report['sha'] or provenance.get('reusedSteps') != report.get('reused_steps'):
            raise ValueError('Baseline inherited stages differ')
        original = private_path(root, provenance['originalReportPath'])
        if digest(original) != provenance['originalReportSha256']:
            raise ValueError('Baseline original evidence changed')
        for step, pin in provenance['completedBlockLogPins'].items():
            if digest(original.parent / (step + '.log')) != pin:
                raise ValueError('Baseline completed stage changed')
        later = private_path(root, provenance['laterReportPath'])
        if digest(later) != provenance['laterReportSha256']:
            raise ValueError('Baseline later evidence changed')
        for step, pin in provenance['laterCompletedBlockLogPins'].items():
            if digest(later.parent / (step + '.log')) != pin:
                raise ValueError('Baseline later completed stage changed')
        for name in ('originalClosure', 'originalResult', 'laterClosure'):
            pinned = private_path(root, provenance[name + 'Path'])
            if digest(pinned) != provenance[name + 'Sha256']:
                raise ValueError('Baseline closure evidence changed')
        review = report.get('supervisor_review', {})
        if review.get('decision') != 'approved' or review.get('cleanupComplete') is not True or review.get('cleanupRecovered') is not False:
            raise ValueError('Baseline supervision did not close cleanly')
        proof.update({'provenance_path': str(provenance_path), 'provenance_sha256': digest(provenance_path),
                      'tool_images': provenance['exactImages']})
    else:
        raise ValueError('This initial scoped procedure requires the recorded tooling anchor')
    if manifest['migration_image_id'] != proof['migration_image_id']:
        raise ValueError('Migration runtime differs from the certified backend anchor')
    return proof


def admit(root=ROOT):
    if command(['git', 'branch', '--show-current'], root) != 'main' or command(['git', 'status', '--porcelain'], root):
        raise ValueError('Scoped release requires clean main')
    sha = command(['git', 'rev-parse', 'HEAD'], root)
    pointer = private_path(root, (root / '.local/last-active-deployment').read_text().strip() + '/manifest.json')
    manifest = json.loads(pointer.read_text())
    if manifest.get('status') != 'published':
        raise ValueError('No published baseline runtime')
    anchor = baseline(root, manifest)
    names = changed_paths(root, anchor['sha'], sha)
    for ref in (manifest['image_id'], anchor['migration_image_id']):
        if image_info(ref, root)['Id'] != ref:
            raise ValueError('Baseline image identity changed')
    for name in ('pki', 'playwright', 'preview', 'sdk'):
        item = anchor['tool_images'][name]
        if image_info(item['imageId'], root)['Id'] != item['imageId']:
            raise ValueError('QA tooling anchor is unavailable')
    return {'scope': SCOPE, 'source_sha': sha, 'allowed_changed_paths': names,
            'active_manifest_path': str(pointer), 'active_manifest_sha256': digest(pointer),
            'active_web_image_id': manifest['image_id'], 'active_sha': manifest['sha'],
            'inherited_backend': anchor}


def normalized_config(info):
    config = dict(info['Config'])
    labels = dict(config.get('Labels') or {})
    labels.pop('org.opencontainers.image.revision', None)
    config['Labels'] = labels
    return config


def compatible_images(base, candidate):
    if normalized_config(base) != normalized_config(candidate):
        raise ValueError('Candidate changed backend runtime configuration')
    layers = base['RootFS']['Layers']
    if candidate['RootFS']['Layers'][:len(layers)] != layers:
        raise ValueError('Candidate did not inherit the exact certified runtime layers')


def backend_tree(image, project, root=ROOT):
    if not re.fullmatch(r'acropolis_test_[a-z0-9_]+', project):
        raise ValueError('Backend probe requires an owned QA project')
    script = r'''find /app -path /app/wwwroot -prune -o -print | LC_ALL=C sort | while IFS= read -r p; do
printf '%s|' "$p"
stat -c '%f|%u|%g' "$p"
if [ -L "$p" ]; then readlink "$p"; elif [ -f "$p" ]; then sha256sum "$p"; elif [ ! -d "$p" ]; then exit 1; fi
done'''
    output = command(['docker', 'run', '--rm', '--name', project + '_backend_probe',
                      '--label', 'acropolis.qa.run=' + project, '--network', 'none', '--read-only',
                      '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
                      '--cpus', '0.5', '--memory', '256m', '--pids-limit', '64',
                      '--entrypoint', '/bin/sh', image, '-ec', script], root)
    return hashlib.sha256(output.encode()).hexdigest()


def frontend_files(image, project, root, folder):
    source = folder / 'frontend-dist'
    rows = []
    for path in sorted(source.rglob('*')):
        if path.is_symlink() or (not path.is_file() and not path.is_dir()):
            raise ValueError('Frontend artifact has an unsupported file type')
        if path.is_file():
            name = path.relative_to(source).as_posix()
            if '\n' in name or '\r' in name or '\\' in name:
                raise ValueError('Invalid frontend artifact name')
            rows.append(digest(path) + '  ' + name)
    if not rows or not (source / 'index.html').is_file():
        raise ValueError('Compiled frontend artifacts are missing')
    manifest = folder / 'frontend-files.sha256'
    manifest.write_text('\n'.join(rows) + '\n')
    command(['docker', 'run', '--rm', '--user', '0', '--name', project + '_frontend_probe',
             '--label', 'acropolis.qa.run=' + project, '--network', 'none', '--read-only',
             '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
             '--cpus', '0.5', '--memory', '256m', '--pids-limit', '64',
             '--mount', 'type=bind,source=' + str(manifest) + ',target=/qa-frontend-files,readonly',
             '--entrypoint', '/bin/sh', image, '-ec', 'cd /app/wwwroot && sha256sum -c /qa-frontend-files'], root)
    return digest(manifest)


def candidate_proof(root, proof, candidate_id, project, folder):
    fresh = admit(root)
    if fresh != proof:
        raise ValueError('Source or baseline changed during verification')
    base_info, candidate_info = image_info(proof['active_web_image_id'], root), image_info(candidate_id, root)
    compatible_images(base_info, candidate_info)
    if candidate_info['Config']['Labels'].get('org.opencontainers.image.revision') != proof['source_sha']:
        raise ValueError('Candidate revision label differs')
    original = backend_tree(proof['active_web_image_id'], project, root)
    current = backend_tree(candidate_id, project, root)
    if original != current:
        raise ValueError('Candidate changed files outside the frontend')
    return {'image_id': candidate_id, 'backend_tree_sha256': current,
            'frontend_files_sha256': frontend_files(candidate_id, project, root, folder), 'runtime_config_preserved': True,
            'runtime_layers_inherited': True}


def validate_certificate(root, report, sha, web_id, migration_id):
    """Called by deployment before any mutation; no new certificate from metadata alone."""
    approved(report)
    if report.get('scope') != SCOPE or report.get('sha') != sha or report.get('image_id') != web_id or report.get('migration_image_id') != migration_id:
        raise ValueError('Scoped certificate identity differs')
    if report.get('gate_eligible') is not True or report.get('last_stage') != 'complete':
        raise ValueError('Scoped certificate is incomplete')
    passed = report.get('passed_steps', [])
    if len(passed) != len(set(passed)) or not FRESH_STEPS.issubset(passed):
        raise ValueError('Scoped certificate lacks required fresh checks')
    proof_path = private_path(root, report['scope_proof_path'])
    if digest(proof_path) != report['scope_proof_sha256']:
        raise ValueError('Scoped admission proof changed')
    proof = json.loads(proof_path.read_text())
    if proof != admit(root) or report['inherited_backend'] != proof['inherited_backend']:
        raise ValueError('Scoped source or inherited baseline changed')
    if migration_id != proof['inherited_backend']['migration_image_id'] or report.get('migration_source_sha') != proof['inherited_backend']['sha']:
        raise ValueError('Migration inheritance differs')
    preserved = report.get('candidate_proof', {})
    if (preserved.get('image_id') != web_id or preserved.get('runtime_config_preserved') is not True
            or preserved.get('runtime_layers_inherited') is not True
            or not re.fullmatch(r'[a-f0-9]{64}', preserved.get('backend_tree_sha256', ''))
            or not re.fullmatch(r'[a-f0-9]{64}', preserved.get('frontend_files_sha256', ''))):
        raise ValueError('Backend equality proof missing')
    if digest(proof_path.parent / 'frontend-files.sha256') != preserved['frontend_files_sha256']:
        raise ValueError('Frontend artifact fingerprint changed')
    compatible_images(image_info(proof['active_web_image_id'], root), image_info(web_id, root))
    return proof


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('admit', 'recheck', 'candidate'))
    parser.add_argument('--output', required=True)
    parser.add_argument('--proof')
    parser.add_argument('--image-id')
    parser.add_argument('--project')
    args = parser.parse_args()
    if args.operation == 'admit':
        value = admit()
    else:
        proof = json.loads(private_path(ROOT, args.proof).read_text())
        if args.operation == 'candidate':
            value = candidate_proof(ROOT, proof, args.image_id, args.project, Path(args.output).resolve().parent)
        else:
            value = admit()
            if value != proof:
                raise ValueError('Source or baseline changed during verification')
    path = Path(args.output)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n')
    path.chmod(0o600)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        print('Scoped release rejected: ' + str(error), file=sys.stderr)
        sys.exit(1)
