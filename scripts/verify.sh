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
export QA_ARTIFACTS="$project_dir/.local/qa/$sha/$run_id"
export QA_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_APP_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_MIGRATION_PASSWORD="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export QA_IMAGE="acropolis-channel:$sha"
[[ "$working_tree" == false ]] || QA_IMAGE="acropolis-channel:working-${sha:0:12}-${run_id,,}"
export QA_MIGRATION_IMAGE="acropolis-channel-migrations:$sha"
[[ "$working_tree" == false ]] || QA_MIGRATION_IMAGE="acropolis-channel-migrations:working-${sha:0:12}-${run_id,,}"
export QA_SDK_IMAGE="acropolis-channel-qa-sdk:${run_id,,}"
export QA_NODE_IMAGE="acropolis-channel-qa-node:${run_id,,}"
export QA_PLAYWRIGHT_IMAGE="acropolis-channel-qa-playwright:${run_id,,}"
restore_project="${QA_PROJECT}_restore"
restore_database="${QA_DATABASE}_restore"
integration_project="${QA_PROJECT}_integration"
integration_database="${QA_DATABASE}_integration"
mkdir -p -- "$QA_ARTIFACTS"
started_at="$(date -u +%FT%TZ)"
stage=initialization
image_id= migration_image_id=
steps_json='[]'
compose() { docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$QA_PROJECT" "$@"; }
redact_log() {
  sed -i -e "s/$QA_PASSWORD/[redacted]/g" -e "s/$QA_APP_PASSWORD/[redacted]/g" -e "s/$QA_MIGRATION_PASSWORD/[redacted]/g" "$1"
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
cleanup() {
  local exit_code=$? cleanup_failed=false status=failed eligible=false
  trap - EXIT INT TERM
  # Unique projects created by this run only; production Compose is never selected.
  if ! compose down --volumes --timeout 10 > "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  if ! QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" compose down --volumes --timeout 10 >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
  if ! QA_PROJECT="$integration_project" QA_DATABASE="$integration_database" compose down --volumes --timeout 10 >> "$QA_ARTIFACTS/cleanup.log" 2>&1; then cleanup_failed=true; fi
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
run_step build_candidate docker build --label "org.opencontainers.image.revision=$sha" --build-arg "VERSION=$sha" --tag "$QA_IMAGE" .
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
run_step build_migrations docker build --target migrations --label "org.opencontainers.image.revision=$sha" --build-arg "VERSION=$sha" --tag "$QA_MIGRATION_IMAGE" .
migration_image_id="$(docker image inspect --format '{{.Id}}' "$QA_MIGRATION_IMAGE")"
run_step build_sdk_runner docker build --target sdk --label "org.opencontainers.image.revision=$sha" --tag "$QA_SDK_IMAGE" .
run_step build_node_runner docker build --target node --label "org.opencontainers.image.revision=$sha" --tag "$QA_NODE_IMAGE" .
run_step build_playwright_runner docker build --target playwright --label "org.opencontainers.image.revision=$sha" --tag "$QA_PLAYWRIGHT_IMAGE" .
run_step database_start compose up -d --wait --wait-timeout 90 db
run_step application_before_migrations compose up -d web
run_step readiness_before_migrations compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http http://web:8080/health/ready 503 not_ready'
run_step liveness_before_migrations compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs http http://web:8080/health 200 ok'
run_step integration_database_start env QA_PROJECT="$integration_project" QA_DATABASE="$integration_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$integration_project" up -d --wait --wait-timeout 90 db
run_step backend_quality env QA_PROJECT="$integration_project" QA_DATABASE="$integration_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$integration_project" run --rm --no-deps sdk '
  mkdir -p /workspace && cp -a /source/. /workspace/ && cd /workspace
  dotnet restore AcropolisChannel.slnx --locked-mode -warnaserror:NU1903,NU1904 -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=high -p:TreatWarningsAsErrors=true
  dotnet build AcropolisChannel.slnx -c Release --no-restore -m:1 -p:BuildInParallel=false
  dotnet format AcropolisChannel.slnx --verify-no-changes --no-restore
  dotnet list AcropolisChannel.slnx package --vulnerable --include-transitive --format json > /artifacts/dotnet-audit.json
  dotnet test tests/backend/Acropolis.Platform.UnitTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/unit -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Platform.Application]*"
  dotnet test tests/backend/Acropolis.Api.Tests -c Release --no-build --no-restore --results-directory /artifacts/backend/api
  dotnet test tests/backend/Acropolis.Architecture.Tests -c Release --no-build --no-restore --results-directory /artifacts/backend/architecture
  dotnet test tests/backend/Acropolis.Platform.IntegrationTests -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory /artifacts/backend/integration -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[Acropolis.Platform.Infrastructure]*" DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile="**/Migrations/*.cs"
'
run_step backend_audit compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs dotnet-audit /artifacts/dotnet-audit.json'
run_step backend_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/unit 80'
run_step integration_coverage compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs coverage /artifacts/backend/integration 80'
run_step integration_database_cleanup env QA_PROJECT="$integration_project" QA_DATABASE="$integration_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$integration_project" down --volumes --timeout 10
run_step frontend_quality compose run --rm --no-deps node '
  mkdir -p /workspace && cp -a /source/frontend/. /workspace/ && cd /workspace
  node -e "if (process.versions.node.split(\".\")[0] !== \"22\") process.exit(1)"
  npm ci --no-audit --no-fund
  npm run typecheck
  npm run lint
  npm run format:check
  FRONTEND_COVERAGE_DIR=/artifacts/frontend-coverage npm run test:coverage
  npm run build
  npm audit --audit-level=high --json > /artifacts/npm-audit.json
'
run_step migrations_first compose run --rm --no-deps migrations
schema_digest() {
  compose exec -T db sh -ec 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --schema-only --no-owner --no-privileges' |
    sed '/^\\restrict /d; /^\\unrestrict /d' | sha256sum | cut -d ' ' -f 1
}
history_digest() {
  compose exec -T db sh -ec 'exec psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -v ON_ERROR_STOP=1' <<'SQL'
SELECT count(*) || ':' || md5(string_agg("MigrationId" || ':' || "ProductVersion", ',' ORDER BY "MigrationId")) FROM platform."__EFMigrationsHistory";
SQL
}
schema_before="$(schema_digest)"
history_before="$(history_digest)"
[[ "$history_before" == 1:* ]] || { echo 'Expected exactly one initial platform migration.' >&2; exit 1; }
run_step migrations_repeat compose run --rm --no-deps migrations
stage=migrations_idempotence
[[ "$(schema_digest)" == "$schema_before" && "$(history_digest)" == "$history_before" ]] || { echo 'Repeated migrations changed schema or migration history.' >&2; exit 1; }
run_step application_start compose up -d web
run_step readiness_initial compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http http://web:8080/health/ready 200 ok'
run_step playwright_e2e compose run --rm --no-deps playwright '
  mkdir -p /workspace && cp -a /source/frontend/. /workspace/ && cd /workspace
  node -e "if (process.versions.node.split(\".\")[0] !== \"22\") process.exit(1)"
  npm ci --no-audit --no-fund
  npm run test:e2e
'
run_step database_stop compose stop db
run_step readiness_database_down compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs http http://web:8080/health/ready 503 not_ready'
run_step liveness_database_down compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs http http://web:8080/health 200 ok'
run_step database_recovery compose up -d --wait --wait-timeout 90 db
run_step readiness_database_recovery compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http http://web:8080/health/ready 200 ok'
run_step persistence_restart compose restart db web
run_step readiness_after_restart compose run --rm --no-deps node 'node /qa-tools/assert-quality.mjs wait-http http://web:8080/health/ready 200 ok'
stage=persistence_consistency
[[ "$(schema_digest)" == "$schema_before" && "$(history_digest)" == "$history_before" ]] || { echo 'Database schema or migration state did not persist after restart.' >&2; exit 1; }
run_step backup bash scripts/backup-db.sh --project "$QA_PROJECT" --database "$QA_DATABASE" --output "$QA_ARTIFACTS/database.dump"
run_step restore_database_start env QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$restore_project" up -d --wait --wait-timeout 90 db
run_step restore bash scripts/restore-db-test.sh --project "$restore_project" --database "$restore_database" --input "$QA_ARTIFACTS/database.dump"
run_step restored_migrations env QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" docker compose --env-file /dev/null -f "$project_dir/compose.qa.yml" -p "$restore_project" run --rm --no-deps migrations
stage=restore_consistency
[[ "$(QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" schema_digest)" == "$schema_before" && "$(QA_PROJECT="$restore_project" QA_DATABASE="$restore_database" history_digest)" == "$history_before" ]] || { echo 'Restored schema or data does not match the backup source.' >&2; exit 1; }
restore_guard() {
  if bash scripts/restore-db-test.sh --project acropolis-channel --database acropolis --input "$QA_ARTIFACTS/database.dump" > "$QA_ARTIFACTS/restore-guard-rejection.log" 2>&1; then
    echo 'Restore guard accepted a production project.' >&2; return 1
  fi
  grep -q 'Restore is restricted to QA projects' "$QA_ARTIFACTS/restore-guard-rejection.log"
}
run_step restore_production_guard restore_guard
run_step load compose run --rm --no-deps k6 run --summary-export /artifacts/k6-summary.json /qa-tools/smoke.js
stage=checkout_consistency
if [[ "$working_tree" == false ]]; then
  [[ "$(git rev-parse HEAD)" == "$sha" && -z "$(git status --porcelain)" ]] || { echo 'Checkout changed during verification; cannot certify candidate.' >&2; exit 1; }
fi
[[ "$(docker image inspect --format '{{.Id}}' "$QA_IMAGE")" == "$image_id" && "$(docker image inspect --format '{{.Id}}' "$QA_MIGRATION_IMAGE")" == "$migration_image_id" ]] || { echo 'Candidate image tag changed during verification.' >&2; exit 1; }
stage=complete
