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
export QA_OWNER_EMAIL="qa-load-000090@example.test"
# Observation recording opt-in is confined to this isolated QA project.
export QA_RECORDING_ENABLED=true
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
supervisor_nonce="${QA_SUPERVISOR_NONCE:-}"
[[ -z "$supervisor_nonce" || "$supervisor_nonce" =~ ^[a-f0-9]{32}$ ]] || { echo 'Invalid QA supervisor attribution.' >&2; exit 2; }
mkdir -p -- "$QA_ARTIFACTS" "$QA_TLS/trust" "$QA_TLS/smtp" "$QA_TLS/protection"
printf '{"schemaVersion":1,"project":"%s","sha":"%s","supervisor_nonce":"%s","processId":%s}\n' "$QA_PROJECT" "$sha" "$supervisor_nonce" "$$" > "$QA_ARTIFACTS/execution-contract.json"
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
  local own_project="$1" own_database="$2"
  python3 scripts/qa-cleanup.py --project "$own_project" --check-ownership || return $?
  QA_PROJECT="$own_project" QA_DATABASE="$own_database" compose --profile tools --profile preview down --volumes --remove-orphans --timeout 10 || return 1
  python3 scripts/qa-cleanup.py --project "$own_project" --verify-absent
}

capture_diagnostics() {
  local diagnostic_stage="$1"
  if ! python3 scripts/qa-diagnostics.py --project "$QA_PROJECT" \
    --output "$QA_ARTIFACTS/diagnostics-$diagnostic_stage.json" \
    --stage "$diagnostic_stage" --since "$started_at" \
    > "$QA_ARTIFACTS/diagnostics-$diagnostic_stage.log" 2>&1; then
    echo "QA diagnostics partial or unavailable: $diagnostic_stage" >&2
  fi
}
cleanup() {
  local exit_code=$? cleanup_failed=false cleanup_complete=true status=failed eligible=false
  trap - EXIT INT TERM
  capture_diagnostics "cleanup-$stage"
  : > "$QA_ARTIFACTS/cleanup.log"
  if [[ "$reference_created" == true ]] && ! drop_schema_reference >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  if ! safe_cleanup_project "$QA_PROJECT" "$QA_DATABASE" >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  if ! safe_cleanup_project "$restore_project" "$restore_database" >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  if ! safe_cleanup_project "$integration_project" "$integration_database" >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  redact_log "$QA_ARTIFACTS/cleanup.log"
  if [[ "$cleanup_failed" == true ]]; then exit_code=1; stage=cleanup; cleanup_complete=false; fi
  if [[ "$exit_code" == 0 && ( "$stage" != complete || -z "$image_id" || -z "$migration_image_id" ) ]]; then exit_code=1; stage=incomplete; fi
  if [[ "$exit_code" == 0 ]]; then
    status=passed
    [[ "$working_tree" == true ]] || eligible=true
  fi
  local gate_eligible="$eligible" supervisor_review_pending=false
  if [[ -n "$supervisor_nonce" ]]; then supervisor_review_pending=true; eligible=false; fi
  cat > "$QA_ARTIFACTS/report.json.tmp" <<JSON
{"sha":"$sha","image":"$QA_IMAGE","image_id":"$image_id","migration_image":"$QA_MIGRATION_IMAGE","migration_image_id":"$migration_image_id","status":"$status","deployment_eligible":$eligible,"gate_eligible":$gate_eligible,"supervisor_review_pending":$supervisor_review_pending,"working_tree":$working_tree,"cleanup_complete":$cleanup_complete,"run_id":"$run_id","path":"$QA_ARTIFACTS/report.json","started_at":"$started_at","completed_at":"$(date -u +%FT%TZ)","last_stage":"$stage","passed_steps":$steps_json}
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
run_step smtp_network_contract env PYTHONDONTWRITEBYTECODE=1 nice -n 10 python3 -m unittest discover -s scripts/tests -p 'test_smtp_network.py' -v
run_step identity_runtime_preflight env PYTHONDONTWRITEBYTECODE=1 nice -n 10 python3 -m unittest discover -s scripts/tests -p 'test_identity_runtime.py' -v
run_step compose_validation compose config --quiet
run_step build_candidate docker build --label "org.opencontainers.image.revision=$sha" --build-arg "REVISION=$sha" --tag "$QA_IMAGE" .
image_id="$(docker image inspect --format '{{.Id}}' "$QA_IMAGE")"
runtime_image_check() {
  local runtime_user
  runtime_user="$(docker image inspect --format '{{.Config.User}}' "$QA_IMAGE")"
  case "$runtime_user" in ''|root|root:*|0|0:*) echo 'Candidate image must declare a non-root user.' >&2; return 1 ;; esac
  docker run --rm --name "${QA_PROJECT}_runtime_image_check" --label "acropolis.qa.run=$QA_PROJECT" --network none --read-only --cap-drop ALL --security-opt no-new-privileges:true --cpus 0.5 --memory 256m --entrypoint /bin/sh "$QA_IMAGE" -ec '
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
export QA_APP_UID="$(docker run --rm --name "${QA_PROJECT}_runtime_uid" --label "acropolis.qa.run=$QA_PROJECT" --network none --read-only --entrypoint id "$QA_IMAGE" -u)"
run_step private_proxy_start compose up -d proxy
proxy_id="$(compose ps -q proxy)"
[[ "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$proxy_id")" == "$QA_PROJECT" ]] || exit 1
export QA_PROXY_IP="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$proxy_id")"
[[ "$QA_PROXY_IP" =~ ^[0-9.]+$ ]] || { echo 'QA proxy has no trusted private address.' >&2; exit 1; }
run_step private_pki compose run --rm --no-deps pki prepare
run_step private_mail_start compose up -d --wait --wait-timeout 60 mailpit mailpit-starttls
run_step smtp_strict_tls compose run --rm --no-deps migrations smtp-check
smtp_untrusted_tls() {
  if compose run --rm --no-deps -e SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt migrations smtp-check; then
    echo 'SMTP accepted an untrusted QA CA.' >&2; return 1
  fi
  echo 'SMTP rejects the same endpoint without its trusted private CA.'
}
run_step smtp_untrusted_tls smtp_untrusted_tls
smtp_rejects_invalid_auth() {
  if compose run --rm --no-deps -e Identity__Smtp__Password=invalid-synthetic-qa-password migrations smtp-check; then
    echo 'SMTP aceptó una contraseña incorrecta.' >&2; return 1
  fi
  echo 'SMTP TLS implícito rechaza la autenticación incorrecta.'
}
smtp_rejects_invalid_hostname() {
  if compose run --rm --no-deps -e Identity__Smtp__Host=mailpit-wrong-host migrations smtp-check; then
    echo 'SMTP aceptó un nombre de servidor fuera del certificado.' >&2; return 1
  fi
  echo 'SMTP TLS implícito rechaza un hostname que no coincide con el certificado.'
}
smtp_starttls_untrusted_ca() {
  if compose run --rm --no-deps -e Identity__Smtp__Host=mailpit-starttls -e Identity__Smtp__Port=1025 -e Identity__Smtp__Security=starttls -e SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt migrations smtp-check; then
    echo 'SMTP STARTTLS aceptó una CA no confiable.' >&2; return 1
  fi
  echo 'SMTP STARTTLS conserva la validación estricta de la CA.'
}
run_step smtp_ssl_invalid_auth smtp_rejects_invalid_auth
run_step smtp_ssl_invalid_hostname smtp_rejects_invalid_hostname
run_step smtp_ssl_plaintext_rejected compose run --rm --no-deps node 'node /qa-tools/smtp-transport.mjs plaintext-rejected'
run_step smtp_starttls_strict_tls compose run --rm --no-deps -e Identity__Smtp__Host=mailpit-starttls -e Identity__Smtp__Port=1025 -e Identity__Smtp__Security=starttls migrations smtp-check
run_step smtp_starttls_untrusted_ca smtp_starttls_untrusted_ca
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
  dotnet restore AcropolisChannel.slnx --locked-mode --disable-parallel -warnaserror:NU1903,NU1904 -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high -p:TreatWarningsAsErrors=true
  dotnet build AcropolisChannel.slnx -c Release --no-restore -m:1 -p:BuildInParallel=false
  dotnet format AcropolisChannel.slnx --verify-no-changes --no-restore
  dotnet list AcropolisChannel.slnx package --vulnerable --include-transitive --format json > /artifacts/dotnet-audit.json
  dotnet test tests/backend/Acropolis.Platform.UnitTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/platform-unit -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Platform.Application]*"
  dotnet test tests/backend/Acropolis.Api.Tests -c Release --no-build --no-restore --results-directory /artifacts/backend/api
  dotnet test tests/backend/Acropolis.Architecture.Tests -c Release --no-build --no-restore --results-directory /artifacts/backend/architecture
  dotnet test tests/backend/Acropolis.Platform.IntegrationTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/platform-integration -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Platform.Infrastructure]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Migrations/*.cs"
  dotnet test tests/backend/Acropolis.Identity.UnitTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/identity-unit -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Identity.Application]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Acropolis.Identity.Application/Contracts.cs"
  dotnet test tests/backend/Acropolis.Identity.IntegrationTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/identity-integration -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Identity.Infrastructure]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Migrations/*.cs"
  dotnet test tests/backend/Acropolis.Subscriptions.UnitTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/subscriptions-unit -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Subscriptions.Application]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Acropolis.Subscriptions.Application/Contracts.cs"
  dotnet test tests/backend/Acropolis.Subscriptions.IntegrationTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/subscriptions-integration -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Subscriptions.Infrastructure]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Migrations/*.cs"
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
run_step subscriptions_unit_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/subscriptions-unit 80 Acropolis.Subscriptions.Application'
run_step subscriptions_integration_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/subscriptions-integration 80 Acropolis.Subscriptions.Infrastructure'
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
 'catalog', (SELECT json_agg(json_build_object('id', "MigrationId", 'version', "ProductVersion") ORDER BY "MigrationId") FROM catalog."__EFMigrationsHistory"),
 'subscriptions', (SELECT json_agg(json_build_object('id', "MigrationId", 'version', "ProductVersion") ORDER BY "MigrationId") FROM subscriptions."__EFMigrationsHistory"));
SQL
}
history_digest() { history_json | sha256sum | cut -d ' ' -f 1; }
python3 - "$QA_ARTIFACTS/expected-migrations.json" <<'PY_MIGRATIONS'
import json,re,sys
from pathlib import Path
manifest={}
for module in ("Platform","Identity","Catalog","Subscriptions"):
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
run_step bootstrap_qa_owner compose run --rm --no-deps migrations bootstrap-owner --email "$QA_OWNER_EMAIL"
run_step playwright_desktop compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/desktop -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/desktop playwright '
  cd /source/frontend
  node -e "if (process.versions.node.split(\".\")[0] !== \"22\") process.exit(1)"
  npm run test:e2e -- --project desktop-chromium --grep-invert "@preview|@email-deferred|@catalog|@mfa|@modernization"
'
# Public register/confirmation/login/reset endpoints retain their Production rate limit.
run_step browser_rate_window sleep 61
run_step playwright_mobile compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/mobile -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/mobile playwright '
  cd /source/frontend
  npm run test:e2e -- --project mobile-chromium --grep-invert "@preview|@email-deferred|@catalog|@mfa|@modernization"
'
run_step catalog_browser_rate_window sleep 61
run_step catalog_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/catalog -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/catalog playwright 'cd /source/frontend && npm run test:e2e -- --grep @catalog'
run_step mfa_browser_rate_window sleep 61
run_step mfa_desktop_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/mfa-desktop -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/mfa-desktop playwright 'cd /source/frontend && npm run test:e2e -- --project desktop-chromium --grep @mfa'
run_step mfa_mobile_rate_window sleep 61
run_step mfa_mobile_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/mfa-mobile -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/mfa-mobile playwright 'cd /source/frontend && npm run test:e2e -- --project mobile-chromium --grep @mfa'
run_step modernization_desktop_rate_window sleep 61
run_step modernization_desktop_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/modernization-desktop -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/modernization-desktop playwright 'cd /source/frontend && npm run test:e2e -- --project desktop-chromium --grep @modernization --grep-invert @youtube-smoke'
run_step modernization_mobile_rate_window sleep 61
run_step modernization_mobile_e2e compose run --rm --no-deps -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/modernization-mobile -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/modernization-mobile playwright 'cd /source/frontend && npm run test:e2e -- --project mobile-chromium --grep @modernization --grep-invert @youtube-smoke'
if [[ "${QA_YOUTUBE_SMOKE:-0}" == 1 ]]; then
  run_step youtube_provider_smoke_rate_window sleep 61
  run_step youtube_provider_smoke compose run --rm --no-deps -e QA_YOUTUBE_SMOKE=1 -e PLAYWRIGHT_OUTPUT_DIR=/artifacts/playwright/youtube-provider -e PLAYWRIGHT_HTML_REPORT=/artifacts/playwright-report/youtube-provider playwright 'cd /source/frontend && npm run test:e2e -- --project desktop-chromium --grep @youtube-smoke'
fi
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
capture_diagnostics catalog-load-before
run_step catalog_load compose run --rm --no-deps k6 run --summary-export /artifacts/k6-catalog-summary.json /qa-tools/catalog.js
capture_diagnostics catalog-load-after
capture_diagnostics identity-load-before
run_step identity_load compose run --rm --no-deps k6 run --summary-export /artifacts/k6-identity-summary.json /qa-tools/identity.js
capture_diagnostics identity-load-after
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
subscription_digest() {
  compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL' | sha256sum | cut -d ' ' -f 1
SELECT row_to_json(s)::text FROM subscriptions."Subscriptions" s ORDER BY "Id";
SELECT row_to_json(a)::text FROM subscriptions."Audit" a ORDER BY "Id";
SQL
}
capture_subscription_digest() {
  subscriptions_before="$(subscription_digest)" || return 1
  [[ "$subscriptions_before" =~ ^[0-9a-f]{64}$ ]]
}
assert_catalog_writers_stopped() {
  [[ "$QA_PROJECT" =~ ^acropolis_test_[a-z0-9_]+$ && "$QA_DATABASE" == "$QA_PROJECT" ]] ||
    { echo 'Catalogue snapshot is restricted to an isolated QA project.' >&2; return 2; }
  local containers container state
  containers="$(compose ps -a -q web)" || return 1
  for container in $containers; do
    [[ "$container" =~ ^[a-f0-9]{64}$ ]] || { echo 'Invalid QA web container identity.' >&2; return 2; }
    state="$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}} {{index .Config.Labels "com.docker.compose.service"}} {{.State.Running}}' "$container")" || return 1
    [[ "$state" == "$QA_PROJECT web false" ]] || { echo 'Catalogue snapshot requires all owned web/worker writers stopped.' >&2; return 1; }
  done
}
catalog_digest() {
  compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL' | sha256sum | cut -d ' ' -f 1
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL TIME ZONE 'UTC';
SELECT row_to_json(c)::text FROM catalog."Contents" c ORDER BY "Id";
SELECT row_to_json(a)::text FROM catalog."Audit" a ORDER BY "Id";
SELECT json_build_array('topic',"Id","Slug","Name","Status","Position","Version","CreatedUtc","UpdatedUtc")::text FROM catalog."Topics" ORDER BY "Id";
SELECT json_build_array('contentTopic',"ContentId","TopicId")::text FROM catalog."ContentTopics" ORDER BY "ContentId","TopicId";
SELECT json_build_array('topicDirectory',"Id","Version")::text FROM catalog."TopicDirectory" ORDER BY "Id";
SELECT json_build_array('topicAudit',"Id","ActorId","TopicId","Action","Changes","CreatedUtc")::text FROM catalog."TopicAudit" ORDER BY "Id";
SELECT json_build_array('session',"Id","AccountId","AuthenticationBindingHash","VisitId","ContentId","ContentVersion","CategoryAtStart","SourceKind","StartedUtc","LastReceivedUtc","LastSequence","CoverageJson","DurationMs","DurationChanged","CoverageIncomplete","EndedReported")::text FROM catalog."ConsumptionSessions" ORDER BY "Id";
SELECT json_build_array('pulse',"SessionId","Sequence","CanonicalHash","ReceivedUtc","CreditedMs","ProgressBasisPoints","CoverageIncomplete","EndedReported","EndedNow")::text FROM catalog."ConsumptionPulses" ORDER BY "SessionId","Sequence";
SELECT json_build_array('accountDaily',"AccountId","DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind","Starts","RecordedPulses","CreditedMs","EndedReports","KnownProgressSamples","UnknownProgressSamples","ProgressBasisPointsSum")::text FROM catalog."ConsumptionAccountDaily" ORDER BY "AccountId","DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind";
SELECT json_build_array('daily',"DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind","Starts","RecordedPulses","CreditedMs","EndedReports","KnownProgressSamples","UnknownProgressSamples","ProgressBasisPointsSum")::text FROM catalog."ConsumptionDaily" ORDER BY "DayUtc","ContentId","ContentVersion","CategoryAtStart","SourceKind";
COMMIT;
SQL
}
capture_catalog_digest() {
  catalog_before="$(catalog_digest)" || return 1
  [[ "$catalog_before" =~ ^[0-9a-f]{64}$ ]]
}
account_access_digest() {
  compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL' | sha256sum | cut -d ' ' -f 1
SELECT json_build_object('userId', "UserId", 'lastSignInUtc', to_char("LastSignInUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'))::text
FROM identity."AccountAccess" ORDER BY "UserId";
SQL
}
capture_account_access_digest() {
  account_access_before="$(account_access_digest)" || return 1
  [[ "$account_access_before" =~ ^[0-9a-f]{64}$ ]]
}
run_step catalog_public_boundaries compose run --rm --no-deps node 'node /qa-tools/catalog-state.mjs'
run_step backup_maintenance compose stop web
run_step catalog_source_writers_stopped assert_catalog_writers_stopped
run_step catalog_source_digest capture_catalog_digest
run_step subscriptions_source_digest capture_subscription_digest
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
run_step account_access_source_digest capture_account_access_digest
run_step backup bash scripts/backup-db.sh --project "$QA_PROJECT" --database "$QA_DATABASE" --output "$QA_ARTIFACTS/database.dump"
run_step schema_reference create_schema_reference
run_step schema_reference_cleanup drop_schema_reference
run_step restore_database_start env QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$restore_project" up -d --wait --wait-timeout 90 db
run_step restore bash scripts/restore-db-test.sh --project "$restore_project" --database "$restore_database" --input "$QA_ARTIFACTS/database.dump" --maintenance
run_step restored_schema_before_runner restored_schema_consistency before
run_step restored_history_before_runner restored_history_consistency before
restored_subscriptions_consistency() {
  local actual
  actual="$(QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" subscription_digest)" || return 1
  [[ "$actual" == "$subscriptions_before" ]] || { echo 'Restored subscriptions or audit differs from the backup before security invalidation.' >&2; return 1; }
}
run_step restored_subscriptions_before_invalidation restored_subscriptions_consistency
restored_account_access_consistency() {
  local actual
  actual="$(QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" account_access_digest)" || return 1
  [[ "$actual" == "$account_access_before" ]] || { echo 'Restored last-sign-in records differ from the backup source.' >&2; return 1; }
}
run_step restored_account_access_before_invalidation restored_account_access_consistency
restored_catalog_consistency() {
  local actual
  actual="$(QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" catalog_digest)" || return 1
  [[ "$actual" == "$catalog_before" ]] || { echo 'Restored editorial content or audit differs from the backup source.' >&2; return 1; }
}
restored_catalog_writers_stopped() {
  QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" assert_catalog_writers_stopped
}
run_step restored_catalog_before_invalidation_writers_stopped restored_catalog_writers_stopped
run_step restored_catalog_before_invalidation restored_catalog_consistency
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
  AND has_table_privilege('acropolis_app', 'identity."AccountAccess"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'identity."AccountAccess"', 'INSERT')
  AND has_table_privilege('acropolis_app', 'identity."AccountAccess"', 'UPDATE')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'identity."AccountAccess"'::regclass) = 'acropolis_migrator'
  AND (SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'identity') = 'acropolis_migrator'
  AND has_schema_privilege('acropolis_app', 'subscriptions', 'USAGE')
  AND NOT has_schema_privilege('acropolis_app', 'subscriptions', 'CREATE')
  AND has_table_privilege('acropolis_app', 'subscriptions."Subscriptions"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'subscriptions."Subscriptions"', 'INSERT')
  AND has_table_privilege('acropolis_app', 'subscriptions."Subscriptions"', 'UPDATE')
  AND NOT has_table_privilege('acropolis_app', 'subscriptions."Subscriptions"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'subscriptions."__EFMigrationsHistory"', 'SELECT')
  AND NOT has_table_privilege('acropolis_app', 'subscriptions."__EFMigrationsHistory"', 'INSERT')
  AND NOT has_table_privilege('acropolis_app', 'subscriptions."Audit"', 'UPDATE')
  AND NOT has_table_privilege('acropolis_app', 'subscriptions."Audit"', 'DELETE')
  AND (SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'subscriptions') = 'acropolis_migrator'
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
  AND has_table_privilege('acropolis_app', 'catalog."Topics"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."Topics"', 'INSERT')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'catalog."Topics"'::regclass) = 'acropolis_migrator'
  AND has_table_privilege('acropolis_app', 'catalog."Topics"', 'UPDATE')
  AND NOT has_table_privilege('acropolis_app', 'catalog."Topics"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'catalog."ContentTopics"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."ContentTopics"', 'INSERT')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'catalog."ContentTopics"'::regclass) = 'acropolis_migrator'
  AND has_table_privilege('acropolis_app', 'catalog."ContentTopics"', 'UPDATE')
  AND has_table_privilege('acropolis_app', 'catalog."ContentTopics"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'catalog."TopicDirectory"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."TopicDirectory"', 'INSERT')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'catalog."TopicDirectory"'::regclass) = 'acropolis_migrator'
  AND has_table_privilege('acropolis_app', 'catalog."TopicDirectory"', 'UPDATE')
  AND NOT has_table_privilege('acropolis_app', 'catalog."TopicDirectory"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'catalog."TopicAudit"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."TopicAudit"', 'INSERT')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'catalog."TopicAudit"'::regclass) = 'acropolis_migrator'
  AND NOT has_table_privilege('acropolis_app', 'catalog."TopicAudit"', 'UPDATE')
  AND NOT has_table_privilege('acropolis_app', 'catalog."TopicAudit"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionSessions"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionSessions"', 'INSERT')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'catalog."ConsumptionSessions"'::regclass) = 'acropolis_migrator'
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionSessions"', 'UPDATE')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionSessions"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionPulses"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionPulses"', 'INSERT')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'catalog."ConsumptionPulses"'::regclass) = 'acropolis_migrator'
  AND NOT has_table_privilege('acropolis_app', 'catalog."ConsumptionPulses"', 'UPDATE')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionPulses"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionAccountDaily"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionAccountDaily"', 'INSERT')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'catalog."ConsumptionAccountDaily"'::regclass) = 'acropolis_migrator'
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionAccountDaily"', 'UPDATE')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionAccountDaily"', 'DELETE')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionDaily"', 'SELECT')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionDaily"', 'INSERT')
  AND (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'catalog."ConsumptionDaily"'::regclass) = 'acropolis_migrator'
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionDaily"', 'UPDATE')
  AND has_table_privilege('acropolis_app', 'catalog."ConsumptionDaily"', 'DELETE')
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
run_step restored_account_access_after_invalidation restored_account_access_consistency
run_step restored_catalog_after_invalidation_writers_stopped restored_catalog_writers_stopped
run_step restored_catalog_after_invalidation restored_catalog_consistency
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
 NOT EXISTS (SELECT 1 FROM identity."Users" WHERE NOT "RevalidationRequired" OR "IsOwner" OR "UsersManage" OR "ContentManage" OR "SubscriptionsManage")
 AND NOT EXISTS (SELECT 1 FROM subscriptions."Subscriptions" WHERE "Status" = 'active')
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
 (SELECT count(*) FROM identity."Users" WHERE "Email" = 'qa-load-000000@example.test' AND NOT "RevalidationRequired" AND NOT "EmailConfirmed" AND NOT "UsersManage" AND NOT "ContentManage" AND NOT "SubscriptionsManage" AND NOT "IsOwner" AND NOT "TwoFactorEnabled" AND "PasswordHash" IS NULL AND "RevalidatedUtc" IS NOT NULL) = 1
 AND (SELECT array_agg(l."Level"::text ORDER BY l."Level") FROM identity."UserLevels" l JOIN identity."Users" u ON u."Id" = l."UserId" WHERE u."Email" = 'qa-load-000000@example.test') = ARRAY['Externo']
THEN 'ok' ELSE 'failed' END;
SQL
)"
  [[ "$result" == ok ]] || { echo 'Explicit revalidation retained old credentials or privileges.' >&2; return 1; }
}
run_step revalidated_permissions revalidated_permissions
run_step restored_account_access_after_revalidation restored_account_access_consistency
run_step restored_revalidation_start restore_compose up -d web
run_step restored_revalidation_readiness restore_compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step restored_fresh_account_flow restore_compose run --rm --no-deps node 'node /qa-tools/identity-recovery.mjs revalidated'

run_step restored_catalog_boundaries restore_compose run --rm --no-deps node 'node /qa-tools/catalog-state.mjs'
run_step restored_catalog_snapshot_maintenance restore_compose stop web
run_step restored_catalog_snapshot_writers_stopped restored_catalog_writers_stopped
run_step restored_catalog_consistency restored_catalog_consistency
run_step restored_catalog_snapshot_resume restore_compose up -d web
run_step restored_catalog_snapshot_readiness restore_compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http "$BASE_URL/health/ready" 200 ok'
run_step restored_greeting restore_compose run --rm --no-deps node 'node --input-type=module -e "const r = await fetch(process.env.BASE_URL + \"/api/v1/greeting\", { signal: AbortSignal.timeout(5000) }); const body = await r.json(); if (r.status !== 200 || body.message !== \"Hola mundo\") process.exit(1);"'
run_step restored_runner_maintenance restore_compose stop web
run_step restored_runner_writers_stopped restored_catalog_writers_stopped
run_step restored_migrations env QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$restore_project" run --rm --no-deps migrations
run_step restored_schema_consistency restored_schema_consistency
run_step restored_history_consistency restored_history_consistency
run_step restored_catalog_after_runner restored_catalog_consistency
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
