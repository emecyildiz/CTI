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

current_version=$(docker compose exec -T cti-db \
    psql -U "$postgres_user" -d "$postgres_db" -Atc \
    'SELECT COALESCE(max(version), 0) FROM cti.schema_versions;')

for migration in app/cti/migrations/[0-9][0-9][0-9]-*.sql; do
    filename=$(basename "$migration")
    version=${filename%%-*}
    version=$(printf '%s' "$version" | sed 's/^0*//')
    version=${version:-0}
    if [ "$version" -gt "$current_version" ]; then
        printf 'Applying migration %s...\n' "$filename"
        docker compose exec -T cti-db \
            psql -U "$postgres_user" -d "$postgres_db" \
            --file "/opt/cti/migrations/$filename"
        current_version=$version
    fi
done

printf 'Database schema is at version %s.\n' "$current_version"
