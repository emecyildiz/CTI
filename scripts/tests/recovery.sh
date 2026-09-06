#!/usr/bin/env sh
# Destructive only inside a fresh, uniquely named local Docker Compose project.
set -eu
repository_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
test_parent=${CTI_TEST_PARENT:-${TMPDIR:-/tmp}}
test_dir=$(mktemp -d "$test_parent/cti-recovery-test.XXXXXX")
project="cti-recovery-$(basename "$test_dir" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9')"
phase=prepare-fixtures
cleanup() {
    result=$?
    if [ "$result" -ne 0 ]; then
        # Fixed stage names and numeric status only: never include env or secrets
        # in the public Actions annotation. This remains readable without logs.
        printf '::error title=Recovery regression failed::stage=%s exit=%s\n' "$phase" "$result" >&2
        for log in migrations.log sql-test.log failed-restore.log successful-restore.log; do
            if [ -f "$test_dir/$log" ]; then
                printf '\nDiagnostic log: %s\n' "$log" >&2
                tail -n 30 "$test_dir/$log" >&2 || true
            fi
        done
        (cd "$test_dir" && docker compose logs --no-color --tail 80 cti-db) >&2 || true
    fi
    (cd "$test_dir" && docker compose down --volumes --remove-orphans >/dev/null 2>&1) || true
    case "$test_dir" in "$test_parent"/cti-recovery-test.*) rm -rf -- "$test_dir" ;; esac
}
trap cleanup EXIT HUP INT TERM
umask 077
mkdir -p "$test_dir/app" "$test_dir/scripts" "$test_dir/shim"
cp -R "$repository_dir/app/cti" "$test_dir/app/cti"
# These are public schema fixtures, read by PostgreSQL's non-root user. The
# private umask must still protect .env and backups, not hide mounted SQL files.
chmod -R a+rX "$test_dir/app/cti"
cp "$repository_dir/scripts/backup.sh" "$repository_dir/scripts/restore.sh" "$repository_dir/scripts/migrate.sh" "$test_dir/scripts/"
cat > "$test_dir/.env" <<EOF
COMPOSE_PROJECT_NAME=$project
POSTGRES_USER=cti_owner
POSTGRES_DB=cti
EOF
cat > "$test_dir/compose.yml" <<'EOF'
services:
  cti-db:
    image: postgres:16-alpine@sha256:57c72fd2a128e416c7fcc499958864df5301e940bca0a56f58fddf30ffc07777
    environment:
      POSTGRES_USER: cti_owner
      POSTGRES_DB: cti
      POSTGRES_PASSWORD: isolated-test-owner
      CTI_APP_PASSWORD: isolated-test-app
      CTI_DASHBOARD_PASSWORD: isolated-test-dashboard
    volumes:
      - test_data:/var/lib/postgresql/data
      - ./app/cti/init-database.sh:/docker-entrypoint-initdb.d/010-init-database.sh:ro
      - ./app/cti/schema.sql:/opt/cti/schema.sql:ro
      - ./app/cti/migrations:/opt/cti/migrations:ro
    healthcheck:
      # The init-only server listens on a Unix socket, not TCP. Do not release
      # migrations until initialization finishes and the final server starts.
      test: [CMD-SHELL, pg_isready -h 127.0.0.1 -U cti_owner -d cti]
      interval: 1s
      timeout: 3s
      retries: 60
    networks: [isolated]
  cti-dashboard:
    image: alpine:3.23
    command: [sleep, '3600']
    networks: [isolated]
volumes:
  test_data:
networks:
  isolated:
    internal: true
EOF
cd "$test_dir"
phase=start-services
docker compose up -d --wait --wait-timeout 90 >/dev/null
phase=migrate-schema
sh scripts/migrate.sh > migrations.log
for test_file in app/cti/tests/*.sql; do
    phase=create-sql-clone
    test_db="cti_test_$(basename "$test_file" .sql | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9_')"
    docker compose exec -T cti-db psql -U cti_owner -d postgres -v ON_ERROR_STOP=1 \
        -c "CREATE DATABASE \"$test_db\" TEMPLATE cti;" >/dev/null
    phase=run-sql-scenario
    if docker compose exec -T cti-db psql -U cti_owner -d "$test_db" -v ON_ERROR_STOP=1 < "$test_file" > sql-test.log; then
        printf 'PASS SQL: %s\n' "$(basename "$test_file")"
    else
        cat sql-test.log >&2
        exit 1
    fi
    phase=drop-sql-clone
    docker compose exec -T cti-db psql -U cti_owner -d postgres -v ON_ERROR_STOP=1 \
        -c "DROP DATABASE \"$test_db\";" >/dev/null
done

phase=seed-recovery-probe
docker compose exec -T cti-db psql -U cti_owner -d cti -v ON_ERROR_STOP=1 -c \
    "CREATE TABLE public.recovery_probe(id integer PRIMARY KEY, value text); INSERT INTO public.recovery_probe SELECT i, repeat(md5(i::text), 20) FROM generate_series(1,2000) i;" >/dev/null
# Freeze the clock to prove two same-second backups cannot overwrite each other.
cat > shim/date <<'EOF'
#!/usr/bin/env sh
printf '20260905T000000Z\n'
EOF
chmod +x shim/date
phase=first-backup
first=$(PATH="$test_dir/shim:$PATH" sh scripts/backup.sh)
phase=second-backup
second=$(PATH="$test_dir/shim:$PATH" sh scripts/backup.sh)
phase=verify-distinct-backups
[ "$first" != "$second" ] && [ -s "$first" ] && [ -s "$second" ]
printf 'PASS: same-second backups have distinct paths\n'
phase=prepare-corrupt-dump
docker compose exec -T cti-db psql -U cti_owner -d cti -v ON_ERROR_STOP=1 -c \
    "INSERT INTO public.recovery_probe VALUES (9999, 'must-survive-failed-restore');" >/dev/null
bytes=$(wc -c < "$first" | tr -d ' ')
head -c "$((bytes - 128))" "$first" > corrupted.dump
# A readable archive catalog does not prove the archived data is intact.
phase=verify-corrupt-dump-catalog
docker compose exec -T cti-db pg_restore --list < corrupted.dump >/dev/null
phase=reject-corrupt-restore
if CTI_RESTORE_CONFIRM=RESTORE_CTIDB CTI_RESTORE_WORKFLOWS_DISABLED=YES sh scripts/restore.sh corrupted.dump > failed-restore.log 2>&1; then
    printf 'FAIL: a truncated archive was accepted\n' >&2
    exit 1
fi
phase=verify-rollback
survivor=$(docker compose exec -T cti-db psql -U cti_owner -d cti -Atc "SELECT value FROM public.recovery_probe WHERE id=9999;")
[ "$survivor" = 'must-survive-failed-restore' ]
printf 'PASS: failed restore rolled back database changes\n'
phase=restore-valid-backup
CTI_RESTORE_CONFIRM=RESTORE_CTIDB CTI_RESTORE_WORKFLOWS_DISABLED=YES sh scripts/restore.sh "$first" > successful-restore.log
phase=verify-restored-row-count
rows=$(docker compose exec -T cti-db psql -U cti_owner -d cti -Atc 'SELECT count(*) FROM public.recovery_probe;')
[ "$rows" = 2000 ]
phase=verify-dashboard-restart
docker compose exec -T cti-dashboard true
phase=verify-restored-schema-grants
docker compose exec -T cti-db psql -U cti_owner -d cti -v ON_ERROR_STOP=1 < app/cti/tests/verify-live.sql >/dev/null
printf 'PASS: full restore, role grants, schema and dashboard restart\n'
