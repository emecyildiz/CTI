#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
package_dir=$(dirname "$script_dir")
cd "$package_dir"

[ -f .env ] || {
    printf 'ERROR: .env is missing.\n' >&2
    exit 1
}

postgres_user=$(sed -n 's/^POSTGRES_USER=//p' .env | head -n 1)
postgres_db=$(sed -n 's/^POSTGRES_DB=//p' .env | head -n 1)
postgres_user=${postgres_user:-cti_owner}
postgres_db=${postgres_db:-cti}

docker compose exec -T cti-db pg_isready -U "$postgres_user" -d "$postgres_db" >/dev/null

umask 077
backup_dir=${CTI_BACKUP_DIR:-"$package_dir/backups"}
mkdir -p "$backup_dir"

timestamp=$(date -u '+%Y%m%dT%H%M%SZ')
# Unique names also protect a just-created input dump from the safety backup
# performed by restore.sh within the same second.
temporary_path=$(mktemp "$backup_dir/.cti-$timestamp-XXXXXX")
suffix=${temporary_path##*-}
final_path="$backup_dir/cti-$timestamp-$suffix.dump"

cleanup() {
    rm -f "$temporary_path"
}
trap cleanup EXIT HUP INT TERM

docker compose exec -T cti-db \
    pg_dump --format=custom --no-owner \
    -U "$postgres_user" -d "$postgres_db" > "$temporary_path"

[ -s "$temporary_path" ] || {
    printf 'ERROR: pg_dump produced an empty file.\n' >&2
    exit 1
}

docker compose exec -T cti-db pg_restore --list < "$temporary_path" >/dev/null
mv "$temporary_path" "$final_path"
(cd "$backup_dir" && sha256sum "$(basename "$final_path")" > "$(basename "$final_path").sha256")
trap - EXIT HUP INT TERM

printf '%s\n' "$final_path"
