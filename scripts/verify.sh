#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$project_dir"
working_tree=false
case "${1:-}" in
  '') ;;
  --working-tree) working_tree=true; shift ;;
  --help) echo 'Usage: verify.sh [--working-tree]. Only a clean SHA can produce a deployment certificate.'; exit 0 ;;
  *) echo 'Unknown verification argument.' >&2; exit 2 ;;
esac
(($# == 0)) || { echo 'Unexpected verification arguments.' >&2; exit 2; }
[[ "$(git branch --show-current)" == main ]] || { echo 'Verification requires main.' >&2; exit 2; }
sha="$(git rev-parse HEAD)"
if [[ "$working_tree" == false && -n "$(git status --porcelain)" ]]; then
  echo 'Checkout is dirty. Commit reviewed changes or use --working-tree for an ineligible iteration.' >&2
  exit 2
fi
run_id="$(date -u +%Y%m%dT%H%M%SZ)_$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
export QA_PROJECT="acropolis_test_${run_id,,}"
export QA_DATABASE="$QA_PROJECT"
reference_database="${QA_DATABASE}_schema_reference"
reference_created=false
export QA_ARTIFACTS="$project_dir/.local/qa/$sha/$run_id"
export QA_HOST="qa-${run_id//_/}.test"
QA_HOST="${QA_HOST,,}"
export QA_TLS="$QA_ARTIFACTS/tls"
export QA_DP_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_SMTP_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_IDENTITY_PASSWORD="Qa1!$(od -An -N20 -tx1 /dev/urandom | tr -d ' \n')"
export QA_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_APP_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_MIGRATION_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_IMAGE="acropolis-channel:$sha"
[[ "$working_tree" == false ]] || QA_IMAGE="acropolis-channel:working-${sha:0:12}-${run_id,,}"
export QA_MIGRATION_IMAGE="acropolis-channel-migrations:$sha"
[[ "$working_tree" == false ]] || QA_MIGRATION_IMAGE="acropolis-channel-migrations:working-${sha:0:12}-${run_id,,}"
export QA_SDK_IMAGE="acropolis-channel-qa-sdk:${run_id,,}"
export QA_NODE_IMAGE="acropolis-channel-qa-node:${run_id,,}"
export QA_PKI_IMAGE="acropolis-channel-qa-pki:${run_id,,}"
export QA_PREVIEW_IMAGE="acropolis-channel-preview:${run_id,,}"
export QA_PLAYWRIGHT_IMAGE="acropolis-channel-qa-playwright:${run_id,,}"
restore_project="${QA_PROJECT}_restore"
restore_database="${QA_DATABASE}_restore"
integration_project="${QA_PROJECT}_integration"
integration_database="${QA_DATABASE}_integration"
mkdir -p -- "$QA_ARTIFACTS" "$QA_TLS/trust" "$QA_TLS/smtp" "$QA_TLS/protection"
started_at="$(date -u +%FT%TZ)"
stage=initialization
image_id= migration_image_id=
steps_json='[]'
compose() { docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$QA_PROJECT" "$@"; }
redact_log() {
  sed -i -e "s/$QA_PASSWORD/[redacted]/g" -e "s/$QA_APP_PASSWORD/[redacted]/g" -e "s/$QA_MIGRATION_PASSWORD/[redacted]/g" -e "s/$QA_DP_PASSWORD/[redacted]/g" -e "s/$QA_SMTP_PASSWORD/[redacted]/g" -e "s/$QA_IDENTITY_PASSWORD/[redacted]/g" "$1"
  python3 - "$1" <<'PY_REDACT'
import re,sys
from pathlib import Path
p=Path(sys.argv[1]); text=p.read_text(errors="replace")
text=re.sub(r"(?i)(token=|token%3D|password=|Cookie:|Set-Cookie:)\S+",r"\1[redacted]",text)
text=re.sub(r'(?i)otpauth://[^\s"<>]+', '[redacted-authenticator-uri]', text)
text=re.sub(r'(?i)("(?:secret|sharedKey|recoveryCode|challenge)"\s*:\s*")[^"]*', r'\1[redacted]', text)
text=re.sub(r'(?i)("recoveryCodes"\s*:\s*)\[[^\]]*\]', r'\1["redacted"]', text)
p.write_text(text)
PY_REDACT
}
run_step() {
  stage="$1"; shift
  echo "QA: $stage"
  if "$@" > "$QA_ARTIFACTS/$stage.log" 2>&1; then
    redact_log "$QA_ARTIFACTS/$stage.log"
    if [[ "$steps_json" == '[]' ]]; then steps_json="[\"$stage\"]"; else steps_json="${steps_json%]},\"$stage\"]"; fi
  else
    redact_log "$QA_ARTIFACTS/$stage.log"
    tail -60 -- "$QA_ARTIFACTS/$stage.log" >&2
    return 1
  fi
}
safe_cleanup_project() {
  local own_project="$1" own_database="$2" volume network="$1""_network"
  [[ "$own_project" =~ ^acropolis_test_[a-z0-9_]+$ ]] || { echo 'Unsafe QA cleanup project rejected.' >&2; return 1; }
  for volume in "$own_project""_postgres" "$own_project""_keyring" "$own_project""_caddy"; do
    if docker volume inspect "$volume" >/dev/null 2>&1; then
      [[ "$(docker volume inspect --format '{{index .Labels "acropolis.qa.run"}}' "$volume")" == "$own_project" ]] || { echo 'QA volume ownership mismatch; refusing cleanup.' >&2; return 1; }
    fi
  done
  if docker network inspect "$network" >/dev/null 2>&1; then
    [[ "$(docker network inspect --format '{{index .Labels "acropolis.qa.run"}}' "$network")" == "$own_project" ]] || { echo 'QA network ownership mismatch; refusing cleanup.' >&2; return 1; }
  fi
  QA_PROJECT="$own_project" QA_DATABASE="$own_database" compose --profile tools --profile preview down --volumes --remove-orphans --timeout 10 || return 1
  [[ -z "$(docker ps -aq --filter "label=com.docker.compose.project=$own_project")" ]] || return 1
  for volume in "$own_project""_postgres" "$own_project""_keyring" "$own_project""_caddy"; do
    ! docker volume inspect "$volume" >/dev/null 2>&1 || return 1
  done
  ! docker network inspect "$network" >/dev/null 2>&1
}
cleanup() {
  local exit_code=$? cleanup_failed=false status=failed eligible=false
  trap - EXIT INT TERM
  : > "$QA_ARTIFACTS/cleanup.log"
  if [[ "$reference_created" == true ]] && ! drop_schema_reference >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  if ! safe_cleanup_project "$QA_PROJECT" "$QA_DATABASE" >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  if ! safe_cleanup_project "$restore_project" "$restore_database" >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  if ! safe_cleanup_project "$integration_project" "$integration_database" >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  redact_log "$QA_ARTIFACTS/cleanup.log"
  if [[ "$cleanup_failed" == true ]]; then exit_code=1; stage=cleanup; fi
  if [[ "$exit_code" == 0 && ( "$stage" != complete || -z "$image_id" || -z "$migration_image_id" ) ]]; then exit_code=1; stage=incomplete; fi
  if [[ "$exit_code" == 0 ]]; then
    status=passed
    [[ "$working_tree" == true ]] || eligible=true
  fi
  cat > "$QA_ARTIFACTS/report.json.tmp" <<JSON
{"sha":"$sha","image":"$QA_IMAGE","image_id":"$image_id","migration_image":"$QA_MIGRATION_IMAGE","migration_image_id":"$migration_image_id","status":"$status","deployment_eligible":$eligible,"working_tree":$working_tree,"run_id":"$run_id","path":"$QA_ARTIFACTS/report.json","started_at":"$started_at","completed_at":"$(date -u +%FT%TZ)","last_stage":"$stage","passed_steps":$steps_json}
JSON
  mv -- "$QA_ARTIFACTS/report.json.tmp" "$QA_ARTIFACTS/report.json"
  echo "QA $status: $QA_ARTIFACTS/report.json"
  exit "$exit_code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

static_checks() {
  while IFS= read -r -d '' file; do bash -n "$file" || return $?; done < <(find scripts infra -type f -name '*.sh' -print0)
  python3 - <<'PY_STATIC'
import ast
from pathlib import Path
for source in Path('scripts').glob('*.py'):
    ast.parse(source.read_text(), filename=str(source))
PY_STATIC
}
run_step static_checks static_checks
[[ -s tests/deploy/test_deploy.py ]] || { echo 'Missing deployment orchestration test suite.' >&2; exit 1; }
run_step deployment_orchestration env PYTHONDONTWRITEBYTECODE=1 nice -n 10 python3 -m unittest discover -s tests/deploy -v
run_step compose_validation compose config --quiet
run_step build_candidate docker build --label "org.opencontainers.image.revision=$sha" --build-arg "REVISION=$sha" --tag "$QA_IMAGE" .
image_id="$(docker image inspect --format '{{.Id}}' "$QA_IMAGE")"
runtime_image_check() {
  local runtime_user
  runtime_user="$(docker image inspect --format '{{.Config.User}}' "$QA_IMAGE")"
  case "$runtime_user" in ''|root|root:*|0|0:*) echo 'Candidate image must declare a non-root user.' >&2; return 1 ;; esac
  docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges:true --cpus 0.5 --memory 256m --entrypoint /bin/sh "$QA_IMAGE" -ec '
    test "$(id -u)" -ne 0
    test ! -e /source
    test ! -e /usr/share/dotnet/sdk
    test -z "$(dotnet --list-sdks)"
    test -z "$(find /app -name ".env" -o -name ".env.*" -o -name ".local" -o -name ".git" -o -name "frontend" -o -name "node_modules")"
    ! command -v node >/dev/null 2>&1
    ! command -v git >/dev/null 2>&1
  '
}
run_step runtime_image_security runtime_image_check
run_step build_migrations docker build --target migrations --label "org.opencontainers.image.revision=$sha" --build-arg "REVISION=$sha" --tag "$QA_MIGRATION_IMAGE" .
migration_image_id="$(docker image inspect --format '{{.Id}}' "$QA_MIGRATION_IMAGE")"
run_step build_sdk_runner docker build --target sdk --label "org.opencontainers.image.revision=$sha" --tag "$QA_SDK_IMAGE" .
run_step build_node_runner docker build --target node --label "org.opencontainers.image.revision=$sha" --tag "$QA_NODE_IMAGE" .
run_step build_playwright_runner docker build --target playwright --label "org.opencontainers.image.revision=$sha" --tag "$QA_PLAYWRIGHT_IMAGE" .
run_step build_pki_runner docker build --target qa-pki --tag "$QA_PKI_IMAGE" .
run_step build_preview docker build --target preview --label "org.opencontainers.image.revision=$sha" --tag "$QA_PREVIEW_IMAGE" .
export QA_APP_UID="$(docker run --rm --network none --read-only --entrypoint id "$QA_IMAGE" -u)"
run_step private_proxy_start compose up -d proxy
proxy_id="$(compose ps -q proxy)"
[[ "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$proxy_id")" == "$QA_PROJECT" ]] || exit 1
export QA_PROXY_IP="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$proxy_id")"
[[ "$QA_PROXY_IP" =~ ^[0-9.]+$ ]] || { echo 'QA proxy has no trusted private address.' >&2; exit 1; }
run_step private_pki compose run --rm --no-deps pki prepare
run_step private_mail_start compose up -d --wait --wait-timeout 60 mailpit
run_step smtp_strict_tls compose run --rm --no-deps migrations smtp-check
smtp_untrusted_tls() {
  if compose run --rm --no-deps -e SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt migrations smtp-check; then
    echo 'SMTP accepted an untrusted QA CA.' >&2; return 1
  fi
  echo 'SMTP rejects the same endpoint without its trusted private CA.'
}
run_step smtp_untrusted_tls smtp_untrusted_tls
run_step database_start compose up -d --wait --wait-timeout 90 db
run_step application_before_migrations compose up -d web
run_step readiness_before_migrations compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 503 not_ready'
run_step strict_private_tls compose run --rm --no-deps node 'node /qa-tools/tls-check.mjs'
run_step browser_untrusted_tls compose run --rm --no-deps --entrypoint /bin/bash playwright -euc 'cp /qa-infra/browser-tls-check.mjs /source/frontend/browser-tls-check.mjs; cd /source/frontend; node browser-tls-check.mjs'
run_step browser_trusted_tls compose run --rm --no-deps playwright 'cp /qa-infra/browser-tls-check.mjs /source/frontend/browser-tls-check.mjs; cd /source/frontend; node browser-tls-check.mjs trusted'
run_step liveness_before_migrations compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs http "$BASE_URL/health" 200 ok'
run_step integration_database_start env QA_PROJECT="$integration_project" QA_DATABASE="$integration_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$integration_project" up -d --wait --wait-timeout 90 db
run_step backend_quality env QA_PROJECT="$integration_project" QA_DATABASE="$integration_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$integration_project" run --rm --no-deps sdk '
  mkdir -p /workspace && cp -a /source/. /workspace/ && cd /workspace
  dotnet restore AcropolisChannel.slnx --locked-mode -warnaserror:NU1903,NU1904 -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high -p:TreatWarningsAsErrors=true
  dotnet build AcropolisChannel.slnx -c Release --no-restore -m:1 -p:BuildInParallel=false
  dotnet format AcropolisChannel.slnx --verify-no-changes --no-restore
  dotnet list AcropolisChannel.slnx package --vulnerable --include-transitive --format json > /artifacts/dotnet-audit.json
  dotnet test tests/backend/Acropolis.Platform.UnitTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/platform-unit -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Platform.Application]*"
  dotnet test tests/backend/Acropolis.Api.Tests -c Release --no-build --no-restore --results-directory /artifacts/backend/api
  dotnet test tests/backend/Acropolis.Architecture.Tests -c Release --no-build --no-restore --results-directory /artifacts/backend/architecture
  dotnet test tests/backend/Acropolis.Platform.IntegrationTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/platform-integration -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Platform.Infrastructure]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Migrations/*.cs"
  dotnet test tests/backend/Acropolis.Identity.UnitTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/identity-unit -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Identity.Application]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Acropolis.Identity.Application/Contracts.cs"
  dotnet test tests/backend/Acropolis.Identity.IntegrationTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/identity-integration -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Identity.Infrastructure]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Migrations/*.cs"
  dotnet test tests/backend/Acropolis.Catalog.UnitTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/catalog-unit -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Catalog.Application]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Acropolis.Catalog.Application/Contracts.cs"
  dotnet test tests/backend/Acropolis.Catalog.IntegrationTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/catalog-integration -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Catalog.Infrastructure]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Migrations/*.cs"
'
run_step backend_audit compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs dotnet-audit /artifacts/dotnet-audit.json'
run_step backend_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/platform-unit 80 Acropolis.Platform.Application'
run_step integration_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/platform-integration 80 Acropolis.Platform.Infrastructure'
run_step identity_unit_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/identity-unit 80 Acropolis.Identity.Application'
run_step identity_integration_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/identity-integration 80 Acropolis.Identity.Infrastructure'
run_step catalog_unit_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/catalog-unit 80 Acropolis.Catalog.Application'
run_step catalog_integration_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/catalog-integration 80 Acropolis.Catalog.Infrastructure'
run_step integration_database_cleanup safe_cleanup_project "$integration_project" "$integration_database"
run_step frontend_quality compose run --rm --no-deps node '
  mkdir -p /workspace && cp -a /source/frontend/. /workspace/ && cd /workspace
  node -e "if (process.versions.node.split(\".\")[0] !== \"22\") process.exit(1)"
  npm ci --no-audit --no-fund
  npm run typecheck
  npm run lint
  npm run format:check
  FRONTEND_COVERAGE_DIR=/artifacts/frontend-coverage npm run test:coverage
  npm run build
  npm run build:preview
  npm audit --audit-level=high --json > /artifacts/npm-audit.json
'
run_step migrations_first compose run --rm --no-deps migrations
artifact_digest() {
  [[ -s "$1" ]] || { echo 'Missing or empty comparison artifact.' >&2; return 1; }
  local digest
  digest="$(sha256sum -- "$1" | cut -d ' ' -f 1)" || return 1
  [[ "$digest" =~ ^[0-9a-f]{64}$ ]] || { echo 'Invalid artifact digest.' >&2; return 1; }
  printf '%s\n' "$digest"
}
schema_dump() {
  compose exec -T db sh -ec 'exec pg_dump -U "$POSTGRES_USER" -d "$1" --schema-only --no-owner --no-privileges' sh "${1:-$QA_DATABASE}" |
    sed '/^\\restrict /d; /^\\unrestrict /d'
}
schema_digest() { schema_dump | sha256sum | cut -d ' ' -f 1; }
history_json() {
  compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL'
SELECT json_build_object(
 'platform', (SELECT json_agg(json_build_object('id', "MigrationId", 'version', "ProductVersion") ORDER BY "MigrationId") FROM platform."__EFMigrationsHistory"),
 'identity', (SELECT json_agg(json_build_object('id', "MigrationId", 'version', "ProductVersion") ORDER BY "MigrationId") FROM identity."__EFMigrationsHistory"),
 'catalog', (SELECT json_agg(json_build_object('id', "MigrationId", 'version', "ProductVersion") ORDER BY "MigrationId") FROM catalog."__EFMigrationsHistory"));
SQL
}
history_digest() { history_json | sha256sum | cut -d ' ' -f 1; }
python3 - "$QA_ARTIFACTS/expected-migrations.json" <<'PY_MIGRATIONS'
import json,re,sys
from pathlib import Path
manifest={}
for module in ("Platform","Identity","Catalog"):
    ids=[]
    for source in Path("src/Modules/"+module).glob("**/Migrations/*.cs"):
        ids.extend(re.findall(r'\[Migration\("([^"]+)"\)\]',source.read_text()))
    if not ids or len(ids)!=len(set(ids)): raise SystemExit("Missing or duplicate migration IDs for "+module)
    manifest[module.lower()]=sorted(ids)
Path(sys.argv[1]).write_text(json.dumps(manifest))
PY_MIGRATIONS
history_json > "$QA_ARTIFACTS/migration-history.json"
run_step migration_manifest compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs migration-history /artifacts/migration-history.json /artifacts/expected-migrations.json'

schema_dump > "$QA_ARTIFACTS/schema-initial.sql"
schema_before="$(artifact_digest "$QA_ARTIFACTS/schema-initial.sql")"
history_before="$(history_digest)"
run_step migrations_repeat compose run --rm --no-deps migrations
stage=migrations_idempotence
[[ "$(schema_digest)" == "$schema_before" && "$(history_digest)" == "$history_before" ]] || { echo 'Repeated migrations changed schema or migration history.' >&2; exit 1; }
run_step application_start compose up -d web
run_step preview_start compose --profile preview up -d preview
run_step readiness_initial compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step email_deferred_start env QA_EMAIL_ENABLED=false docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$QA_PROJECT" up -d --no-deps --force-recreate web
run_step email_deferred_readiness compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step email_deferred_http compose run --rm --no-deps node 'node /qa-tools/email-deferred.mjs'
run_step email_deferred_browser compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/email-deferred -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/email-deferred playwright 'cd /source/frontend && npm run test:e2e -- --grep @email-deferred'
run_step email_enabled_start compose up -d --no-deps --force-recreate web
run_step email_enabled_readiness compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step public_asset_efficiency compose run --rm --no-deps node 'node /qa-tools/http-assets.mjs'
run_step synthetic_accounts compose run --rm --no-deps migrations qa-seed --count 100000
synthetic_account_count() {
  local count
  count="$(compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL'
SELECT count(*) FROM identity."Users" WHERE "Email" ~ '^qa-load-[0-9]{6}@example[.]test$' AND "EmailConfirmed";
SQL
)"
  [[ "$count" == 100000 ]] || { echo 'The real synthetic account fixture must contain 100000 confirmed accounts.' >&2; return 1; }
}
run_step synthetic_account_count synthetic_account_count
run_step bootstrap_qa_admin compose run --rm --no-deps migrations bootstrap-admin --email qa-load-000000@example.test
run_step grant_qa_editor compose run --rm --no-deps migrations grant-content-manager --email qa-load-000080@example.test
run_step playwright_desktop compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/desktop -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/desktop playwright '
  cd /source/frontend
  node -e "if (process.versions.node.split(\".\")[0] !== \"22\") process.exit(1)"
  npm run test:e2e -- --project desktop-chromium --grep-invert "@preview|@email-deferred|@catalog|@mfa"
'
# Public register/confirmation/login/reset endpoints retain their Production rate limit.
run_step browser_rate_window sleep 61
run_step playwright_mobile compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/mobile -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/mobile playwright '
  cd /source/frontend
  npm run test:e2e -- --project mobile-chromium --grep-invert "@preview|@email-deferred|@catalog|@mfa"
'
run_step catalog_browser_rate_window sleep 61
run_step catalog_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/catalog -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/catalog playwright 'cd /source/frontend && npm run test:e2e -- --grep @catalog'
run_step mfa_browser_rate_window sleep 61
run_step mfa_desktop_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/mfa-desktop -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/mfa-desktop playwright 'cd /source/frontend && npm run test:e2e -- --project desktop-chromium --grep @mfa'
run_step mfa_mobile_rate_window sleep 61
run_step mfa_mobile_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/mfa-mobile -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/mfa-mobile playwright 'cd /source/frontend && npm run test:e2e -- --project mobile-chromium --grep @mfa'
run_step preview_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/preview -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/preview playwright '
  cd /source/frontend
  npm run test:e2e -- --grep @preview
'
run_step smtp_stop compose stop mailpit
run_step smtp_outage_registration compose run --rm --no-deps node 'node /qa-tools/smtp-failure.mjs queue'
smtp_failed_attempt() {
  local email result
  email="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["email"])' "$QA_ARTIFACTS/smtp-recovery.json")"
  [[ "$email" =~ ^smtp-failure-[0-9]+@acropolis[.]test$ ]] || return 1
  for attempt in {1..45}; do
    result="$(compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1 -v email="$1"' sh "$email" <<'SQL'
SELECT EXISTS(SELECT 1 FROM identity."Outbox" AS o JOIN identity."Users" AS u ON u."Id" = o."UserId" WHERE u."Email" = :'email' AND o."Attempts" > 0 AND o."Status" = 'pending');
SQL
)"
    if [[ "$result" == t ]]; then echo 'Real SMTP outage recorded a failed attempt and retained the retryable message.'; return 0; fi
    sleep 1
  done
  echo 'SMTP outage never exercised a real failed dispatch attempt.' >&2; return 1
}
run_step smtp_failed_attempt smtp_failed_attempt
run_step smtp_recovery compose up -d --wait --wait-timeout 60 mailpit
run_step smtp_queue_delivery compose run --rm --no-deps node 'node /qa-tools/smtp-failure.mjs delivered'
run_step database_stop compose stop db
run_step readiness_database_down compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs http "$BASE_URL/health/ready" 503 not_ready'
run_step liveness_database_down compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs http "$BASE_URL/health" 200 ok'
run_step authenticated_database_down compose run --rm --no-deps node 'node /qa-tools/identity-state.mjs database-down'
run_step database_recovery compose up -d --wait --wait-timeout 90 db
run_step readiness_database_recovery compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step persistence_recreate compose up -d --force-recreate web
run_step readiness_after_recreate compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step session_after_recreate compose run --rm --no-deps node 'node /qa-tools/identity-state.mjs persistence'
run_step persistence_restart compose restart db web
run_step readiness_after_restart compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
stage=persistence_consistency
[[ "$(schema_digest)" == "$schema_before" && "$(history_digest)" == "$history_before" ]] || { echo 'Database schema or migration state did not persist after restart.' >&2; exit 1; }
run_step synthetic_catalog compose run --rm --no-deps migrations qa-seed-catalog --count 10000
catalog_fixture_count() {
  local result
  result="$(compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL'
SELECT CASE WHEN count(*) = 10000
 AND count(*) FILTER (WHERE "Status"='published') = 8000
 AND count(*) FILTER (WHERE "Status"='draft') = 1000
 AND count(*) FILTER (WHERE "Status"='archived') = 1000
 THEN 'ok' ELSE 'failed' END FROM catalog."Contents" WHERE left("Slug",11)='qa-catalog-';
SQL
)"
  [[ "$result" == ok ]] || { echo 'Synthetic catalogue must contain 10000 persisted entries with the expected publication boundaries.' >&2; return 1; }
}
run_step synthetic_catalog_count catalog_fixture_count
run_step synthetic_catalog_repeat compose run --rm --no-deps migrations qa-seed-catalog --count 10000
run_step synthetic_catalog_count_repeat catalog_fixture_count
run_step catalog_load compose run --rm --no-deps k6 run --summary-export /artifacts/k6-catalog-summary.json /qa-tools/catalog.js
run_step identity_load compose run --rm --no-deps k6 run --summary-export /artifacts/k6-identity-summary.json /qa-tools/identity.js
synthetic_session_count() {
  local count
  count="$(compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL'
SELECT count(DISTINCT s."UserId") FROM identity."Sessions" s JOIN identity."Users" u ON u."Id" = s."UserId" WHERE u."Email" ~ '^qa-load-0000[0-4][0-9]@example[.]test$' AND s."ExpiresUtc" > now();
SQL
)"
  [[ "$count" == 50 ]] || { echo 'Performance run did not exercise 50 real active account sessions.' >&2; return 1; }
}
run_step synthetic_session_count synthetic_session_count
run_step load compose run --rm --no-deps k6 run --summary-export /artifacts/k6-summary.json /qa-tools/smoke.js
catalog_digest() {
  compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL' | sha256sum | cut -d ' ' -f 1
SELECT row_to_json(c)::text FROM catalog."Contents" c ORDER BY "Id";
SELECT row_to_json(a)::text FROM catalog."Audit" a ORDER BY "Id";
SQL
}
capture_catalog_digest() {
  catalog_before="$(catalog_digest)" || return 1
  [[ "$catalog_before" =~ ^[0-9a-f]{64}$ ]]
}
run_step catalog_source_digest capture_catalog_digest
run_step catalog_public_boundaries compose run --rm --no-deps node 'node /qa-tools/catalog-state.mjs'
run_step keyring_backup compose run --rm --no-deps pki backup-keyring
run_step private_ca_backup compose run --rm --no-deps pki backup-caddy
source_schema_consistency() {
  schema_dump > "$QA_ARTIFACTS/schema-source.sql" || return 1
  local expected actual
  expected="$(artifact_digest "$QA_ARTIFACTS/schema-initial.sql")" || return 1
  actual="$(artifact_digest "$QA_ARTIFACTS/schema-source.sql")" || return 1
  if ! cmp -s -- "$QA_ARTIFACTS/schema-initial.sql" "$QA_ARTIFACTS/schema-source.sql"; then
    diff -u "$QA_ARTIFACTS/schema-initial.sql" "$QA_ARTIFACTS/schema-source.sql" > "$QA_ARTIFACTS/schema-source.diff" || true
    echo "Source schema changed before backup: expected=$expected actual=$actual; inspect schema-source.diff." >&2; return 1
  fi
  [[ "$actual" == "$schema_before" ]] || return 1
  echo "Source schema preserved: $actual"
}
source_history_consistency() {
  history_json > "$QA_ARTIFACTS/migration-history-source.json" || return 1
  local expected actual
  expected="$(artifact_digest "$QA_ARTIFACTS/migration-history.json")" || return 1
  actual="$(artifact_digest "$QA_ARTIFACTS/migration-history-source.json")" || return 1
  if ! cmp -s -- "$QA_ARTIFACTS/migration-history.json" "$QA_ARTIFACTS/migration-history-source.json"; then
    diff -u "$QA_ARTIFACTS/migration-history.json" "$QA_ARTIFACTS/migration-history-source.json" > "$QA_ARTIFACTS/migration-history-source.diff" || true
    echo "Source migration history changed before backup: expected=$expected actual=$actual." >&2; return 1
  fi
  [[ "$actual" == "$history_before" ]] || return 1
  echo "Source migration history preserved: $actual"
}
restored_schema_consistency() {
  local suffix expected actual
  case "${1:-after}" in before) suffix=-before ;; after) suffix= ;; *) return 2 ;; esac
  QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" schema_dump > "$QA_ARTIFACTS/schema-restored$suffix.sql" || return 1
  expected="$(artifact_digest "$QA_ARTIFACTS/schema-reference.sql")" || return 1
  actual="$(artifact_digest "$QA_ARTIFACTS/schema-restored$suffix.sql")" || return 1
  if ! cmp -s -- "$QA_ARTIFACTS/schema-reference.sql" "$QA_ARTIFACTS/schema-restored$suffix.sql"; then
    diff -u "$QA_ARTIFACTS/schema-reference.sql" "$QA_ARTIFACTS/schema-restored$suffix.sql" > "$QA_ARTIFACTS/schema-restored$suffix.diff" || true
    echo "Restored schema differs from canonical reference: expected=$expected actual=$actual; inspect schema-restored$suffix.diff." >&2; return 1
  fi
  echo "Restored schema matches canonical reference: $actual"
}
restored_history_consistency() {
  local suffix expected actual
  case "${1:-after}" in before) suffix=-before ;; after) suffix= ;; *) return 2 ;; esac
  QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" history_json > "$QA_ARTIFACTS/migration-history-restored$suffix.json" || return 1
  expected="$(artifact_digest "$QA_ARTIFACTS/migration-history-source.json")" || return 1
  actual="$(artifact_digest "$QA_ARTIFACTS/migration-history-restored$suffix.json")" || return 1
  if ! cmp -s -- "$QA_ARTIFACTS/migration-history-source.json" "$QA_ARTIFACTS/migration-history-restored$suffix.json"; then
    diff -u "$QA_ARTIFACTS/migration-history-source.json" "$QA_ARTIFACTS/migration-history-restored$suffix.json" > "$QA_ARTIFACTS/migration-history-restored$suffix.diff" || true
    echo "Restored migration history differs from source: expected=$expected actual=$actual; inspect migration-history-restored$suffix.diff." >&2; return 1
  fi
  [[ "$actual" == "$history_before" ]] || return 1
  echo "Restored migration history matches source: $actual"
}
guard_schema_reference() {
  [[ "$QA_PROJECT" =~ ^acropolis_test_[a-z0-9_]+$ && "$QA_DATABASE" == "$QA_PROJECT"
    && "$reference_database" == "${QA_DATABASE}_schema_reference" && ${#reference_database} -le 63 ]] ||
    { echo 'Unsafe QA schema reference rejected.' >&2; return 2; }
  local container
  container="$(compose ps -q db)" || return 1
  [[ -n "$container" && "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$container")" == "$QA_PROJECT"
    && "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.service"}}' "$container")" == db ]] ||
    { echo 'Schema reference container ownership mismatch.' >&2; return 2; }
}
create_schema_reference() {
  guard_schema_reference || return $?
  local exists
  exists="$(compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d postgres -At -v ON_ERROR_STOP=1 -v reference="$1"' sh "$reference_database" <<'SQL'
SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=:'reference');
SQL
)" || return 1
  [[ "$exists" == f ]] || { echo 'Refusing to reuse an existing schema reference database.' >&2; return 2; }
  compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d postgres -v ON_ERROR_STOP=1 -v reference="$1"' sh "$reference_database" <<'SQL' || return 1
SELECT format('CREATE DATABASE %I OWNER acropolis_migrator TEMPLATE template0', :'reference') \gexec
SQL
  reference_created=true
  compose exec -T db sh -ec 'exec pg_restore -U "$POSTGRES_USER" -d "$1" --schema-only --no-owner --role=acropolis_migrator --exit-on-error --single-transaction' sh "$reference_database" < "$QA_ARTIFACTS/database.dump" || return 1
  schema_dump "$reference_database" > "$QA_ARTIFACTS/schema-reference.sql" || return 1
  [[ -s "$QA_ARTIFACTS/schema-reference.sql" ]] || return 1
  echo 'Canonical schema reference restored without data; all schema objects and CHECK constraints remain compared.'
}
drop_schema_reference() {
  [[ "$reference_created" == true ]] || return 0
  guard_schema_reference || return $?
  local owner
  owner="$(compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d postgres -At -v ON_ERROR_STOP=1 -v reference="$1"' sh "$reference_database" <<'SQL'
SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=:'reference';
SQL
)" || return 1
  [[ "$owner" == acropolis_migrator ]] || { echo 'Schema reference database owner mismatch; refusing drop.' >&2; return 2; }
  compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d postgres -v ON_ERROR_STOP=1 -v reference="$1"' sh "$reference_database" <<'SQL' || return 1
SELECT format('DROP DATABASE %I', :'reference') \gexec
SQL
  reference_created=false
  echo 'Owned QA schema reference database removed.'
}
run_step source_schema_consistency source_schema_consistency
run_step source_history_consistency source_history_consistency
run_step backup bash scripts/backup-db.sh --project "$QA_PROJECT" --database "$QA_DATABASE" --output "$QA_ARTIFACTS/database.dump"
run_step schema_reference create_schema_reference
run_step schema_reference_cleanup drop_schema_reference
run_step restore_database_start env QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$restore_project" up -d --wait --wait-timeout 90 db
run_step restore bash scripts/restore-db-test.sh --project "$restore_project" --database "$restore_database" --input "$QA_ARTIFACTS/database.dump" --maintenance
run_step restored_schema_before_runner restored_schema_consistency before
run_step restored_history_before_runner restored_history_consistency before
restore_proxy_ip="$QA_PROXY_IP"
restore_compose() {
  QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" QA_PROXY_IP="$restore_proxy_ip" compose "$@"
}
restored_permissions() {
  local result
  result="$(restore_compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL'
SELECT CASE WHEN
  has_schema_privilege('acropolis_app', 'platform', 'USAGE')
  AND has_schema_privilege('acropolis_app', 'identity', 'USAGE')
  AND NOT has_schema_privilege('acropolis_app', 'identity', 'CREATE')
  AND has_table_privilege('acropolis_app', 'identity."__EFMigrationsHistory"', 'SELECT')
  AND NOT has_table_privilege('acropolis_app', 'identity."__EFMigrationsHistory"', 'INSERT')
  AND NOT has_table_privilege('acropolis_app', 'identity."Audit"', 'UPDATE')
  AND NOT has_table_privilege('acropolis_app', 'identity."Audit"', 'DELETE')
  AND NOT has_table_privilege('acropolis_app', 'identity."Bootstrap"', 'INSERT')
  AND (SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'identity') = 'acropolis_migrator'
  AND has_schema_privilege('acropolis_app', 'catalog', 'USAGE')
  AND NOT has_schema_privilege('acropolis_app', 'catalog', 'CREATE')
  AND has_table_privilege('acropolis_app', 'catalog."Contents"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."Contents"', 'INSERT')
  AND has_table_privilege('acropolis_app', 'catalog."Contents"', 'UPDATE')
  AND NOT has_table_privilege('acropolis_app', 'catalog."Contents"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'catalog."__EFMigrationsHistory"', 'SELECT')
  AND NOT has_table_privilege('acropolis_app', 'catalog."__EFMigrationsHistory"', 'INSERT')
  AND NOT has_table_privilege('acropolis_app', 'catalog."Audit"', 'UPDATE')
  AND NOT has_table_privilege('acropolis_app', 'catalog."Audit"', 'DELETE')
  AND (SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'catalog') = 'acropolis_migrator'
  AND NOT has_schema_privilege('acropolis_app', 'platform', 'CREATE')
  AND has_table_privilege('acropolis_app', 'platform."__EFMigrationsHistory"', 'SELECT')
  AND NOT has_table_privilege('acropolis_app', 'platform."__EFMigrationsHistory"', 'INSERT')
  AND (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()) = 'acropolis_migrator'
  AND (SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'platform') = 'acropolis_migrator'
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'platform."__EFMigrationsHistory"'::regclass) = 'acropolis_migrator'
THEN 'ok' ELSE 'failed' END;
SQL
)" || return 1
  [[ "$result" == ok ]] || { echo 'Restored ownership or application permissions differ from the expected boundary.' >&2; return 1; }
}
run_step restored_permissions restored_permissions
run_step restore_keyring restore_compose run --rm --no-deps pki restore-keyring
run_step restore_private_ca restore_compose run --rm --no-deps pki restore-caddy
run_step recovery_invalidation restore_compose run --rm --no-deps migrations recovery-invalidate --maintenance
run_step restored_mail_start restore_compose up -d --wait --wait-timeout 60 mailpit
run_step restored_proxy_start restore_compose up -d proxy
restore_proxy_id="$(restore_compose ps -q proxy)"
restore_proxy_ip="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$restore_proxy_id")"
run_step restored_application_start env QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" QA_PROXY_IP="$restore_proxy_ip" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$restore_project" up -d web
run_step restored_readiness restore_compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
recovery_accounts() {
  local result
  result="$(restore_compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL'
SELECT CASE WHEN
 NOT EXISTS (SELECT 1 FROM identity."Users" WHERE NOT "RevalidationRequired")
 AND NOT EXISTS (SELECT 1 FROM identity."Sessions")
 AND NOT EXISTS (SELECT 1 FROM identity."Users" WHERE "TwoFactorEnabled")
 AND NOT EXISTS (SELECT 1 FROM identity."MfaCredentials")
 AND NOT EXISTS (SELECT 1 FROM identity."MfaChallenges")
 AND NOT EXISTS (SELECT 1 FROM identity."MfaRecoveryCodes")
 AND NOT EXISTS (SELECT 1 FROM identity."MfaProofs")
 AND NOT EXISTS (SELECT 1 FROM identity."Flows" WHERE "ConsumedUtc" IS NULL)
 AND NOT EXISTS (SELECT 1 FROM identity."Outbox" WHERE "Status" <> 'cancelled' OR "Payload" <> '')
THEN 'ok' ELSE 'failed' END;
SQL
)"
  [[ "$result" == ok ]] || { echo 'Recovery would reactivate accounts, permissions, sessions, tokens or pending messages.' >&2; return 1; }
}
run_step recovery_security_state recovery_accounts
run_step restored_sessions restore_compose run --rm --no-deps node 'node /qa-tools/identity-state.mjs recovery'
run_step restored_accounts_blocked restore_compose run --rm --no-deps node 'node /qa-tools/identity-recovery.mjs blocked'
run_step restored_maintenance restore_compose stop web
run_step explicit_revalidation restore_compose run --rm --no-deps migrations recovery-revalidate --email qa-load-000000@example.test --maintenance
revalidated_permissions() {
  local result
  result="$(restore_compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL'
SELECT CASE WHEN
 (SELECT count(*) FROM identity."Users" WHERE "Email" = 'qa-load-000000@example.test' AND NOT "RevalidationRequired" AND NOT "EmailConfirmed" AND NOT "UsersManage" AND NOT "ContentManage" AND NOT "TwoFactorEnabled" AND "PasswordHash" IS NULL AND "RevalidatedUtc" IS NOT NULL) = 1
 AND (SELECT array_agg(l."Level"::text ORDER BY l."Level") FROM identity."UserLevels" l JOIN identity."Users" u ON u."Id" = l."UserId" WHERE u."Email" = 'qa-load-000000@example.test') = ARRAY['Externo']
THEN 'ok' ELSE 'failed' END;
SQL
)"
  [[ "$result" == ok ]] || { echo 'Explicit revalidation retained old credentials or privileges.' >&2; return 1; }
}
run_step revalidated_permissions revalidated_permissions
run_step restored_revalidation_start restore_compose up -d web
run_step restored_revalidation_readiness restore_compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step restored_fresh_account_flow restore_compose run --rm --no-deps node 'node /qa-tools/identity-recovery.mjs revalidated'

run_step restored_catalog_boundaries restore_compose run --rm --no-deps node 'node /qa-tools/catalog-state.mjs'
stage=restored_catalog_consistency
[[ "$(QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" catalog_digest)" == "$catalog_before" ]] || { echo 'Restored editorial content or audit differs from the backup source.' >&2; exit 1; }
run_step restored_greeting restore_compose run --rm --no-deps node 'node --input-type=module -e "const r = await fetch(process.env.BASE_URL + \"/api/v1/greeting\", { signal: AbortSignal.timeout(5000) }); const body = await r.json(); if (r.status !== 200 || body.message !== \"Hola mundo\") process.exit(1);"'
run_step restored_migrations env QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$restore_project" run --rm --no-deps migrations
run_step restored_schema_consistency restored_schema_consistency
run_step restored_history_consistency restored_history_consistency
restore_guard() {
  if bash scripts/restore-db-test.sh --project acropolis-channel --database acropolis --input "$QA_ARTIFACTS/database.dump" > "$QA_ARTIFACTS/restore-guard-rejection.log" 2>&1; then
    echo 'Restore guard accepted a production project.' >&2; return 1
  fi
  grep -q 'Restore is restricted to QA projects' "$QA_ARTIFACTS/restore-guard-rejection.log"
}
run_step restore_production_guard restore_guard
stage=checkout_consistency
if [[ "$working_tree" == false ]]; then
  [[ "$(git rev-parse HEAD)" == "$sha" && -z "$(git status --porcelain)" ]] || { echo 'Checkout changed during verification; cannot certify candidate.' >&2; exit 1; }
fi
[[ "$(docker image inspect --format '{{.Id}}' "$QA_IMAGE")" == "$image_id" && "$(docker image inspect --format '{{.Id}}' "$QA_MIGRATION_IMAGE")" == "$migration_image_id" ]] || { echo 'Candidate image tag changed during verification.' >&2; exit 1; }
stage=complete
