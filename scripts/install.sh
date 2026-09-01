#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
package_dir=$(dirname "$script_dir")
cd "$package_dir"

sh ./scripts/preflight.sh
docker compose up -d --build

dashboard_bind=$(sed -n 's/^CTI_DASHBOARD_BIND=//p' .env | head -n 1)
dashboard_port=$(sed -n 's/^CTI_DASHBOARD_PORT=//p' .env | head -n 1)
postgres_user=$(sed -n 's/^POSTGRES_USER=//p' .env | head -n 1)
postgres_db=$(sed -n 's/^POSTGRES_DB=//p' .env | head -n 1)
dashboard_bind=${dashboard_bind:-127.0.0.1}
dashboard_port=${dashboard_port:-8080}
postgres_user=${postgres_user:-cti_owner}
postgres_db=${postgres_db:-cti}

attempt=0
until curl --fail --silent --show-error "http://127.0.0.1:${dashboard_port}/health/ready" >/dev/null 2>&1; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 30 ]; then
        docker compose ps
        printf 'ERROR: dashboard readiness timed out.\n' >&2
        exit 1
    fi
    sleep 2
done

schema_version=$(docker compose exec -T cti-db \
    psql -U "$postgres_user" -d "$postgres_db" -Atc \
    'SELECT max(version) FROM cti.schema_versions;')
enabled_sources=$(docker compose exec -T cti-db \
    psql -U "$postgres_user" -d "$postgres_db" -Atc \
    'SELECT count(*) FROM cti.sources WHERE enabled;')

printf 'CTI Self-Hosted is ready. Schema: %s, enabled sources: %s.\n' \
    "$schema_version" "$enabled_sources"
printf 'Dashboard: http://127.0.0.1:%s\n' "$dashboard_port"
printf 'Next: connect n8n to the CTI Docker network and import workflows/.\n'
