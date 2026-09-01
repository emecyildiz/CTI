#!/usr/bin/env sh
set -eu

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

command -v docker >/dev/null 2>&1 || fail "Docker is not installed."
command -v curl >/dev/null 2>&1 || fail "curl is not installed."
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is not available."
docker info >/dev/null 2>&1 || fail "The Docker daemon is not reachable."

[ -f .env ] || fail "Copy .env.example to .env and configure it first."
if grep -q 'REPLACE_WITH_' .env; then
    fail ".env still contains placeholder passwords."
fi

owner_password=$(sed -n 's/^POSTGRES_PASSWORD=//p' .env | head -n 1)
app_password=$(sed -n 's/^CTI_APP_PASSWORD=//p' .env | head -n 1)
dashboard_password=$(sed -n 's/^CTI_DASHBOARD_PASSWORD=//p' .env | head -n 1)
dashboard_bind=$(sed -n 's/^CTI_DASHBOARD_BIND=//p' .env | head -n 1)
managed_n8n=$(sed -n 's/^CTI_MANAGED_N8N=//p' .env | head -n 1)
n8n_encryption_key=$(sed -n 's/^N8N_ENCRYPTION_KEY=//p' .env | head -n 1)

[ -n "$owner_password" ] || fail "POSTGRES_PASSWORD is missing."
[ -n "$app_password" ] || fail "CTI_APP_PASSWORD is missing."
[ -n "$dashboard_password" ] || fail "CTI_DASHBOARD_PASSWORD is missing."
[ "$owner_password" != "$app_password" ] || fail "Owner and n8n passwords must differ."
[ "$owner_password" != "$dashboard_password" ] || fail "Owner and dashboard passwords must differ."
[ "$app_password" != "$dashboard_password" ] || fail "n8n and dashboard passwords must differ."

case "${dashboard_bind:-127.0.0.1}" in
    127.0.0.1|localhost) ;;
    *) fail "The first public release only permits a loopback dashboard binding." ;;
esac

if [ "$managed_n8n" = "true" ] && [ -z "$n8n_encryption_key" ]; then
    fail "N8N_ENCRYPTION_KEY is required when CTI_MANAGED_N8N=true."
fi

docker compose config --quiet

n8n_container=${N8N_CONTAINER:-$(sed -n 's/^N8N_CONTAINER=//p' .env | head -n 1)}
if [ -n "$n8n_container" ]; then
    running=$(docker inspect --format '{{.State.Running}}' "$n8n_container" 2>/dev/null || true)
    if [ "$running" = "true" ]; then
        n8n_version=$(docker exec "$n8n_container" n8n --version 2>/dev/null | tr -d '\r' | head -n 1)
        case "$n8n_version" in
            2.*) ;;
            *) fail "The public workflows are currently tested with n8n 2.x; detected '$n8n_version'." ;;
        esac
        printf 'Detected n8n %s in container %s.\n' "$n8n_version" "$n8n_container"
    elif [ "$managed_n8n" = "true" ]; then
        printf 'Managed n8n container %s will be created during installation.\n' "$n8n_container"
    else
        fail "N8N_CONTAINER does not identify a running container."
    fi
fi

printf 'Preflight passed. Docker, Compose, configuration, and password separation are valid.\n'
