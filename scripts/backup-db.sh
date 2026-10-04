#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
project= database= output=
while (($#)); do
  case "$1" in
    --project) project="${2:?Missing project}"; shift 2 ;;
    --database) database="${2:?Missing database}"; shift 2 ;;
    --output) output="${2:?Missing output}"; shift 2 ;;
    --help) echo 'Usage: backup-db.sh --project COMPOSE_PROJECT --database DATABASE --output ABSOLUTE_DUMP_PATH'; exit 0 ;;
    *) echo 'Unknown backup argument.' >&2; exit 2 ;;
  esac
done
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]+$ && "$database" =~ ^[a-zA-Z0-9_]+$ ]] || { echo 'Invalid project or database.' >&2; exit 2; }
[[ "$output" == /* ]] || { echo 'Output must be an absolute path.' >&2; exit 2; }
output="$(realpath -m -- "$output")"
case "$output" in
  "$project_dir/.local/backups/"*|"$project_dir/.local/qa/"*) ;;
  *) echo 'Backups must stay inside this checkout .local/backups or .local/qa.' >&2; exit 2 ;;
esac
[[ ! -e "$output" ]] || { echo 'Refusing to overwrite an existing backup.' >&2; exit 2; }
mapfile -t containers < <(docker ps -q --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=db')
((${#containers[@]} == 1)) || { echo 'Expected exactly one running database container for this project.' >&2; exit 1; }
container="${containers[0]}"
docker exec "$container" sh -ec 'test "$POSTGRES_DB" = "$1"' sh "$database" || { echo 'Database does not match the container database.' >&2; exit 2; }
mkdir -p -- "$(dirname -- "$output")"
temporary="$(mktemp -- "$(dirname -- "$output")/.backup.XXXXXX")"
trap 'rm -f -- "$temporary"' EXIT
docker exec "$container" sh -ec 'exec pg_dump -U "$POSTGRES_USER" -d "$1" --format=custom --no-owner' sh "$database" > "$temporary"
docker exec -i "$container" pg_restore --list < "$temporary" > /dev/null
[[ -s "$temporary" ]] || { echo 'Backup is empty.' >&2; exit 1; }
mv --no-clobber -- "$temporary" "$output"
[[ ! -e "$temporary" ]] || { echo 'Backup destination appeared while writing; refusing overwrite.' >&2; exit 1; }
echo "Backup created: $output"
