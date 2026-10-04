#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
project= database= input= maintenance=false
while (($#)); do
  case "$1" in
    --project) project="${2:?Missing project}"; shift 2 ;;
    --database) database="${2:?Missing database}"; shift 2 ;;
    --input) input="${2:?Missing input}"; shift 2 ;;
    --maintenance) maintenance=true; shift ;;
    --help) echo 'Usage: restore-db-test.sh --project acropolis_test_RUN --database acropolis_test_RUN --input ABSOLUTE_DUMP_PATH --maintenance'; exit 0 ;;
    *) echo 'Unknown restore argument.' >&2; exit 2 ;;
  esac
done
[[ "$project" =~ ^acropolis_test_[a-z0-9_]+$ && "$database" =~ ^acropolis_test_[a-z0-9_]+$ ]] || { echo 'Restore is restricted to QA projects and QA databases named acropolis_test_*.' >&2; exit 2; }
[[ "$maintenance" == true ]] || { echo 'QA recovery requires --maintenance and an offline application.' >&2; exit 2; }
[[ "$input" == /* && -f "$input" ]] || { echo 'Input must be an existing absolute backup path.' >&2; exit 2; }
input="$(realpath -- "$input")"
case "$input" in
  "$project_dir/.local/backups/"*|"$project_dir/.local/qa/"*) ;;
  *) echo 'Backup must be inside this checkout .local/backups or .local/qa.' >&2; exit 2 ;;
esac
mapfile -t containers < <(docker ps -q --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=db')
((${#containers[@]} == 1)) || { echo 'Expected exactly one running QA database container.' >&2; exit 1; }
mapfile -t web_containers < <(docker ps -q --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=web')
(("${#web_containers[@]}" == 0)) || { echo 'Restore requires the QA application to be offline.' >&2; exit 2; }
container="${containers[0]}"
[[ "$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$container")" == "$project" ]] || exit 2
docker exec "$container" sh -ec 'test "$POSTGRES_DB" = "$1"' sh "$database" || { echo 'QA database does not match the container database.' >&2; exit 2; }
docker exec -i "$container" sh -ec 'exec pg_restore -U "$POSTGRES_USER" -d "$1" --clean --if-exists --no-owner --role=acropolis_migrator --exit-on-error --single-transaction' sh "$database" < "$input"
echo "Backup restored only into offline QA database: $database"
echo 'Before starting web, restore its QA key ring/protector and run recovery-invalidate --maintenance.'
