#!/usr/bin/env bash
# A new frontend over a byte-identical certified backend; never a generic fast gate.
set -Eeuo pipefail
umask 077
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$project_dir"
(($# == 0)) || { echo 'Scoped verification accepts no extra arguments.' >&2; exit 2; }
sha="$(git rev-parse HEAD)"
run_id="$(date -u +%Y%m%dT%H%M%SZ)_$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
export QA_PROJECT="acropolis_test_${run_id,,}" QA_DATABASE="acropolis_test_${run_id,,}"
export QA_ARTIFACTS="$project_dir/.local/qa/$sha/$run_id" QA_TLS="$project_dir/.local/qa/$sha/$run_id/tls"
export QA_HOST="qa-${run_id//_/}.test"; QA_HOST="${QA_HOST,,}"
export QA_IMAGE="acropolis-channel:$sha" QA_NODE_IMAGE="acropolis-channel-qa-node:${run_id,,}"
export QA_OWNER_EMAIL=qa-load-000090@example.test QA_EMAIL_ENABLED=false QA_RECORDING_ENABLED=false
export QA_DP_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_SMTP_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_IDENTITY_PASSWORD="Qa1!$(od -An -N20 -tx1 /dev/urandom | tr -d ' \n')"
export QA_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_APP_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_MIGRATION_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_MIGRATION_IMAGE= QA_PKI_IMAGE= QA_PLAYWRIGHT_IMAGE= QA_SDK_IMAGE= QA_PREVIEW_IMAGE=
mkdir -p "$QA_ARTIFACTS" "$QA_TLS/trust" "$QA_TLS/protection" "$project_dir/.local/modernization"
# A caller with an attribution nonce owns the existing guarded lock. Direct calls serialize too.
supervisor_nonce="${QA_SUPERVISOR_NONCE:-}"
[[ -z "$supervisor_nonce" || "$supervisor_nonce" =~ ^[a-f0-9]{32}$ ]] || exit 2
if [[ -z "$supervisor_nonce" ]]; then
  exec 200>"$project_dir/.local/modernization/final-gate.lock"
  flock -n 200 || { echo 'Another heavy QA task owns the project lock.' >&2; exit 2; }
fi
started_at="$(date -u +%FT%TZ)"; stage=initialization; image_id=; node_image_id=; cleanup_complete=false; assembly_id=
steps=()
overlay="$QA_ARTIFACTS/compose.scoped.yml"
compose() { docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -f "$overlay" -p "$QA_PROJECT" "$@"; }
redact_log() {
  sed -i -e "s/$QA_PASSWORD/[redacted]/g" -e "s/$QA_APP_PASSWORD/[redacted]/g" -e "s/$QA_MIGRATION_PASSWORD/[redacted]/g" -e "s/$QA_DP_PASSWORD/[redacted]/g" -e "s/$QA_SMTP_PASSWORD/[redacted]/g" -e "s/$QA_IDENTITY_PASSWORD/[redacted]/g" "$1"
  python3 - "$1" <<'PY_REDACT'
import re,sys
from pathlib import Path
p=Path(sys.argv[1]); text=p.read_text(errors='replace')
text=re.sub(r'(?i)(token=|token%3D|password=|Cookie:|Set-Cookie:)\S+',r'\1[redacted]',text)
text=re.sub(r'(?i)otpauth://[^\s"<>]+','[redacted-authenticator-uri]',text)
text=re.sub(r'(?i)("(?:secret|sharedKey|recoveryCode|challenge)"\s*:\s*")[^"]*',r'\1[redacted]',text)
text=re.sub(r'(?i)("recoveryCodes"\s*:\s*)\[[^\]]*\]',r'\1["redacted"]',text)
p.write_text(text)
PY_REDACT
}
run_step() {
  stage="$1"; shift
  echo "QA editorial: $stage"
  if "$@" > "$QA_ARTIFACTS/$stage.log" 2>&1; then
    redact_log "$QA_ARTIFACTS/$stage.log"; steps+=("$stage")
  else
    redact_log "$QA_ARTIFACTS/$stage.log"; tail -50 "$QA_ARTIFACTS/$stage.log" >&2; return 1
  fi
}
cleanup() {
  local exit_code=$? status=failed eligible=false cleanup_failed=false
  trap - EXIT INT TERM HUP
  if [[ -n "$assembly_id" ]]; then
    if [[ "$(docker inspect --format '{{.Name}} {{.Image}} {{.State.Running}}' "$assembly_id")" == "/${QA_PROJECT}_frontend_assembly $base_web false" ]]; then
      docker rm "$assembly_id" > "$QA_ARTIFACTS/assembly-cleanup.log" 2>&1 || { exit_code=1; stage=cleanup; cleanup_failed=true; }
    else exit_code=1; stage=cleanup; cleanup_failed=true; fi
  fi
  if [[ -s "$overlay" && -n "$QA_MIGRATION_IMAGE" ]]; then
    if python3 scripts/qa-cleanup.py --project "$QA_PROJECT" --check-ownership > "$QA_ARTIFACTS/cleanup.log" 2>&1 &&
      compose --profile tools down --volumes --remove-orphans --timeout 10 >> "$QA_ARTIFACTS/cleanup.log" 2>&1 &&
      python3 scripts/qa-cleanup.py --project "$QA_PROJECT" --verify-absent >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then
      [[ "$cleanup_failed" == true ]] || cleanup_complete=true
    else exit_code=1; stage=cleanup; fi
    redact_log "$QA_ARTIFACTS/cleanup.log"
  else cleanup_complete=true; fi
  if [[ "$exit_code" == 0 && "$stage" == complete && -n "$image_id" ]]; then status=passed; eligible=true; fi
  local gate_eligible="$eligible" supervisor_review_pending=false
  if [[ -n "$supervisor_nonce" ]]; then supervisor_review_pending=true; eligible=false; fi
  python3 - "$QA_ARTIFACTS" "$sha" "$run_id" "$QA_IMAGE" "$image_id" "$QA_MIGRATION_IMAGE" "$status" "$eligible" "$gate_eligible" "$supervisor_review_pending" "$cleanup_complete" "$stage" "$started_at" "$node_image_id" "${steps[@]}" <<'PY_REPORT'
import datetime,hashlib,json,sys
from pathlib import Path
(folder,sha,run,image,image_id,migration,status,eligible,gate,pending,cleanup,stage,started,node,*steps)=sys.argv[1:]
p=Path(folder); proof_path=p/'scope-proof.json'; proof=json.loads(proof_path.read_text()) if proof_path.exists() else {}
candidate=json.loads((p/'candidate-proof.json').read_text()) if (p/'candidate-proof.json').exists() else {}
report={'scope':'frontend-low-risk','sha':sha,'image':image,'image_id':image_id,'migration_image':migration,
'migration_image_id':migration,'migration_source_sha':proof.get('inherited_backend',{}).get('sha'),
'status':status,'deployment_eligible':eligible=='true','gate_eligible':gate=='true',
'supervisor_review_pending':pending=='true','working_tree':False,'cleanup_complete':cleanup=='true',
'run_id':run,'path':str(p/'report.json'),'started_at':started,
'completed_at':datetime.datetime.now(datetime.timezone.utc).isoformat(),'last_stage':stage,'passed_steps':steps,
'node_image_id':node,'scope_proof_path':str(proof_path),
'scope_proof_sha256':hashlib.sha256(proof_path.read_bytes()).hexdigest() if proof_path.exists() else None,
'inherited_backend':proof.get('inherited_backend'),'candidate_proof':candidate,
'inherited_checks':['backend unit/integration/coverage','backend HTTP/authentication','load','SMTP/resilience','backup/restoration'],
'fresh_runtime_fixture_accounts':200,'full_gate':False}
tmp=p/'report.json.tmp';tmp.write_text(json.dumps(report,indent=2)+'\n');tmp.replace(p/'report.json')
PY_REPORT
  echo "QA $status: $QA_ARTIFACTS/report.json"
  exit "$exit_code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP
run_step scope_admission python3 scripts/frontend-release.py admit --output "$QA_ARTIFACTS/scope-proof.json"
readarray -t inherited < <(python3 - "$QA_ARTIFACTS/scope-proof.json" <<'PY_IMAGES'
import json,sys
p=json.load(open(sys.argv[1]));b=p['inherited_backend']
print(p['active_web_image_id']);print(b['migration_image_id'])
for n in ('pki','playwright','sdk','preview'):print(b['tool_images'][n]['imageId'])
PY_IMAGES
)
((${#inherited[@]} == 6)) || exit 1
base_web="${inherited[0]}"; QA_MIGRATION_IMAGE="${inherited[1]}"; QA_PKI_IMAGE="${inherited[2]}"
QA_PLAYWRIGHT_IMAGE="${inherited[3]}"; QA_SDK_IMAGE="${inherited[4]}"; QA_PREVIEW_IMAGE="${inherited[5]}"
python3 - "$overlay" <<'PY_OVERLAY'
import json,subprocess,sys
from pathlib import Path
services={name:{'cpuset':'0,1'} for name in ('db','proxy','pki','web','migrations','node','playwright')}
for name,ref in (('db','postgres:18-bookworm'),('proxy','caddy:2.11.6-alpine')):
 services[name]['image']=subprocess.check_output(['docker','image','inspect',ref,'--format','{{.Id}}'],text=True).strip()
Path(sys.argv[1]).write_text(json.dumps({'services':services},indent=2)+'\n')
PY_OVERLAY
static_checks() {
  while IFS= read -r -d '' file; do bash -n "$file" || return $?; done < <(find scripts infra -type f -name '*.sh' -print0)
  python3 - <<'PY_STATIC'
import ast
from pathlib import Path
for p in Path('scripts').glob('*.py'):ast.parse(p.read_text(),filename=str(p))
PY_STATIC
}
run_step static_checks static_checks
run_step deployment_orchestration env PYTHONDONTWRITEBYTECODE=1 nice -n 10 python3 -m unittest discover -s tests/deploy -v
run_step compose_validation compose config --quiet
run_step build_node_runner docker build --target node --label "org.opencontainers.image.revision=$sha" --tag "$QA_NODE_IMAGE" .
node_image_id="$(docker image inspect --format '{{.Id}}' "$QA_NODE_IMAGE")"
run_step frontend_quality compose run --rm --no-deps -e NODE_EXTRA_CA_CERTS= node '
  mkdir -p /workspace && cp -a /source/frontend/. /workspace/ && cd /workspace
  node -e "if (process.versions.node.split(\".\")[0] !== \"22\") process.exit(1)"
  npm ci --no-audit --no-fund
  npm run format:check
  npm run typecheck
  npm run lint
  npm audit --audit-level=high --json > /artifacts/npm-audit.json
  FRONTEND_COVERAGE_DIR=/artifacts/frontend-coverage npm run test:coverage
  npm run build
  cp -a dist /artifacts/frontend-dist
  cp -a e2e /artifacts/current-e2e
  cp playwright.config.ts /artifacts/current-playwright.config.ts
'
frontend_coverage() {
  python3 - "$QA_ARTIFACTS/frontend-coverage/coverage-summary.json" <<'PY_COVERAGE'
import json,sys
metrics=json.load(open(sys.argv[1]))['total']
for name in ('lines','branches','functions','statements'):
 value=metrics[name]
 if value.get('total',0)<1 or not isinstance(value.get('pct'),(int,float)) or value['pct']<80:
  raise SystemExit('Frontend coverage failed for '+name)
print('All frontend coverage metrics passed >=80%.')
PY_COVERAGE
}
run_step frontend_coverage frontend_coverage
# Preserve the image Config and backend exactly. The unstarted, private assembly
# container receives only compiled frontend files; existing public asset hashes may remain.
build_frontend_candidate() {
  assembly_id="$(docker create --name "${QA_PROJECT}_frontend_assembly" --network none --cpus 0.5 --memory 256m --pids-limit 64 --security-opt no-new-privileges:true "$base_web")"
  [[ "$assembly_id" =~ ^[a-f0-9]{64}$ ]] || return 1
  [[ "$(docker inspect --format '{{.Name}} {{.Image}} {{.State.Running}}' "$assembly_id")" == "/${QA_PROJECT}_frontend_assembly $base_web false" ]] || return 1
  docker cp "$QA_ARTIFACTS/frontend-dist/." "$assembly_id:/app/wwwroot"
  docker commit --change "LABEL org.opencontainers.image.revision=$sha" "$assembly_id" "$QA_IMAGE"
  docker rm "$assembly_id"
  assembly_id=
}
run_step build_candidate build_frontend_candidate
image_id="$(docker image inspect --format '{{.Id}}' "$QA_IMAGE")"
run_step runtime_image_security docker run --rm --name "${QA_PROJECT}_runtime_image_check" --label "acropolis.qa.run=$QA_PROJECT" --network none --read-only --cap-drop ALL --security-opt no-new-privileges:true --cpus 0.5 --memory 256m --pids-limit 64 --entrypoint /bin/sh "$image_id" -ec '
  test "$(id -u)" -ne 0; test ! -e /source; test ! -e /usr/share/dotnet/sdk
  test -z "$(dotnet --list-sdks)"
  test -z "$(find /app -name .env -o -name .local -o -name .git -o -name node_modules)"
  ! command -v node >/dev/null 2>&1; ! command -v git >/dev/null 2>&1
'
run_step backend_equivalence python3 scripts/frontend-release.py candidate --proof "$QA_ARTIFACTS/scope-proof.json" --image-id "$image_id" --project "$QA_PROJECT" --output "$QA_ARTIFACTS/candidate-proof.json"
export QA_APP_UID="$(docker run --rm --name "${QA_PROJECT}_runtime_uid" --label "acropolis.qa.run=$QA_PROJECT" --network none --read-only --cpus 0.5 --memory 64m --entrypoint id "$image_id" -u)"
run_step private_proxy_start compose up -d --no-build --pull never proxy
proxy_id="$(compose ps -q proxy)"
[[ "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$proxy_id")" == "$QA_PROJECT" ]] || exit 1
export QA_PROXY_IP="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$proxy_id")"
[[ "$QA_PROXY_IP" =~ ^[0-9.]+$ ]] || exit 1
run_step private_pki compose run --rm --no-deps pki prepare
run_step database_start compose up -d --no-build --pull never --wait --wait-timeout 90 db
run_step migrations_first compose run --rm --no-deps migrations
schema_digest() { compose exec -T db sh -ec 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --schema-only --no-owner --no-privileges' | sed '/^\\restrict /d; /^\\unrestrict /d' | sha256sum | cut -d ' ' -f 1; }
schema_before="$(schema_digest)"
run_step migrations_repeat compose run --rm --no-deps migrations
[[ "$(schema_digest)" == "$schema_before" ]] || { stage=migrations_idempotence; exit 1; }
run_step synthetic_accounts compose run --rm --no-deps migrations qa-seed --count 200
run_step grant_qa_editor compose run --rm --no-deps migrations grant-content-manager --email qa-load-000080@example.test
run_step bootstrap_qa_owner compose run --rm --no-deps migrations bootstrap-owner --email "$QA_OWNER_EMAIL"
run_step application_start compose up -d --no-build --pull never web
run_step readiness_initial compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step strict_private_tls compose run --rm --no-deps node 'node /qa-tools/tls-check.mjs'
run_step frontend_assets compose run --rm --no-deps node 'node /qa-tools/http-assets.mjs'
browser_editor() {
  local browser_project="$1"
  compose run --rm --no-deps -e "PLAYWRIGHT_JSON_OUTPUT_NAME=/artifacts/playwright-$browser_project.json" -e QA_OWNER_EMAIL="$QA_OWNER_EMAIL" -e PLAYWRIGHT_OUTPUT_DIR="/artifacts/playwright/$browser_project" -e PLAYWRIGHT_HTML_REPORT="/artifacts/playwright-report/$browser_project" playwright "
    rm -rf /source/frontend/e2e
    cp -a /artifacts/current-e2e /source/frontend/e2e
    cp /artifacts/current-playwright.config.ts /source/frontend/playwright.config.ts
    cd /source/frontend
    npx playwright test e2e/youtube-url.spec.ts e2e/catalog.spec.ts --project $browser_project --grep 'pasted YouTube URL|editorial lifecycle' --reporter=list,html,json --trace=off
  "
  python3 - "$QA_ARTIFACTS/playwright-$browser_project.json" <<'PY_BROWSER'
import json,sys
s=json.load(open(sys.argv[1]))['stats']
if s.get('expected')!=2 or any(s.get(k)!=0 for k in ('unexpected','flaky','skipped')):
 raise SystemExit('The two scoped browser workflows did not both pass without retries or skips.')
print('Both real editor workflows passed without skips/retries.')
PY_BROWSER
}
run_step editor_desktop browser_editor desktop-chromium
run_step editor_mobile browser_editor mobile-chromium
run_step scope_recheck python3 scripts/frontend-release.py recheck --proof "$QA_ARTIFACTS/scope-proof.json" --output "$QA_ARTIFACTS/scope-recheck.json"
stage=complete
