#!/usr/bin/env sh
set -eu

if [ "$#" -ne 1 ]; then
    printf 'Usage: CTI_RESTORE_CONFIRM=RESTORE_CTIDB CTI_RESTORE_WORKFLOWS_DISABLED=YES sh ./scripts/restore.sh <backup.dump>\n' >&2
    exit 2
fi

[ "${CTI_RESTORE_CONFIRM:-}" = "RESTORE_CTIDB" ] || {
    printf 'ERROR: set CTI_RESTORE_CONFIRM=RESTORE_CTIDB to authorize replacement of the CTI database.\n' >&2
    exit 1
}
[ "${CTI_RESTORE_WORKFLOWS_DISABLED:-}" = "YES" ] || {
    printf 'ERROR: disable CTI workflows, then set CTI_RESTORE_WORKFLOWS_DISABLED=YES.\n' >&2
    exit 1
}

backup_path=$1
[ -f "$backup_path" ] || {
    printf 'ERROR: backup file does not exist: %s\n' "$backup_path" >&2
    exit 1
}
backup_directory=$(CDPATH= cd -- "$(dirname -- "$backup_path")" && pwd)
backup_path="$backup_directory/$(basename "$backup_path")"

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

if [ -f "$backup_path.sha256" ]; then
    (cd "$backup_directory" && sha256sum --check "$(basename "$backup_path").sha256")
fi
docker compose exec -T cti-db pg_restore --list < "$backup_path" >/dev/null

safety_backup=$(sh ./scripts/backup.sh)
printf 'Pre-restore safety backup: %s\n' "$safety_backup"

restart_dashboard() {
    docker compose start cti-dashboard >/dev/null 2>&1 || true
}
trap restart_dashboard EXIT HUP INT TERM

docker compose stop cti-dashboard
docker compose exec -T cti-db \
    pg_restore --clean --if-exists --no-owner --exit-on-error \
    -U "$postgres_user" -d "$postgres_db" < "$backup_path"
docker compose start cti-dashboard

schema_version=$(docker compose exec -T cti-db \
    psql -U "$postgres_user" -d "$postgres_db" -Atc \
    'SELECT max(version) FROM cti.schema_versions;')
enabled_sources=$(docker compose exec -T cti-db \
    psql -U "$postgres_user" -d "$postgres_db" -Atc \
    'SELECT count(*) FROM cti.sources WHERE enabled;')
privileges_ok=$(docker compose exec -T cti-db \
    psql -U "$postgres_user" -d "$postgres_db" -Atc \
    "SELECT has_schema_privilege('cti_n8n', 'cti', 'USAGE') AND has_table_privilege('cti_dashboard', 'cti.dashboard_articles', 'SELECT');")
[ "$privileges_ok" = "t" ] || {
    printf 'ERROR: restore completed but restricted role grants are invalid.\n' >&2
    exit 1
}

trap - EXIT HUP INT TERM
printf 'Restore completed. Schema: %s, enabled sources: %s.\n' \
    "$schema_version" "$enabled_sources"
