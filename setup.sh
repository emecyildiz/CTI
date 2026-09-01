#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$script_dir"

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

secret() {
    od -An -N32 -tx1 /dev/urandom | tr -d ' \n'
}

command -v docker >/dev/null 2>&1 || {
    printf 'Docker Engine and Docker Compose are required.\n'
    printf 'Install guide: https://docs.docker.com/engine/install/\n'
    fail 'Docker was not found. Install it and rerun setup.sh.'
}
docker compose version >/dev/null 2>&1 || fail 'Docker Compose v2 is unavailable.'
docker info >/dev/null 2>&1 || fail 'The Docker daemon is not running or is not accessible.'

if [ ! -f .env ]; then
    printf 'Creating a private local configuration...\n'
    cat > .env <<EOF
POSTGRES_DB=cti
POSTGRES_USER=cti_owner
POSTGRES_PASSWORD=$(secret)
CTI_APP_PASSWORD=$(secret)
CTI_DASHBOARD_PASSWORD=$(secret)
CTI_DASHBOARD_BIND=127.0.0.1
CTI_DASHBOARD_PORT=8080
CTI_NETWORK_NAME=cti-self-hosted
CTI_COMPOSE_PROJECT_NAME=cti-self-hosted
CTI_N8N_API_URL=http://cti-n8n:5678/api/v1
N8N_CONTAINER=cti-n8n
CTI_MANAGED_N8N=true
N8N_PORT=5678
N8N_TIMEZONE=${TZ:-UTC}
N8N_ENCRYPTION_KEY=$(secret)
EOF
    chmod 600 .env
fi

sh ./scripts/install.sh

n8n_container=$(sed -n 's/^N8N_CONTAINER=//p' .env | head -n 1)
n8n_container=${n8n_container:-cti-n8n}
attempt=0
until docker exec "$n8n_container" n8n --version >/dev/null 2>&1; do
    attempt=$((attempt + 1))
    [ "$attempt" -lt 60 ] || fail 'The managed n8n service did not become ready in time.'
    sleep 2
done

if ! docker exec "$n8n_container" sh -c \
    'test -f /home/node/.n8n/.cti-workflows-imported-v1'; then
    printf 'Importing disabled CTI workflows...\n'
    CTI_IMPORT_CONFIRM=IMPORT_DISABLED_WORKFLOWS sh ./scripts/import-workflows.sh
    docker exec "$n8n_container" sh -c \
        'touch /home/node/.n8n/.cti-workflows-imported-v1'
fi

printf '\nCTI Self-Hosted is running.\n'
dashboard_port=$(sed -n 's/^CTI_DASHBOARD_PORT=//p' .env | head -n 1)
n8n_port=$(sed -n 's/^N8N_PORT=//p' .env | head -n 1)
printf 'Dashboard: http://127.0.0.1:%s\n' "${dashboard_port:-8080}"
printf 'n8n:       http://127.0.0.1:%s\n' "${n8n_port:-5678}"
printf 'Create the first n8n owner account, then map PostgreSQL and optional AI/Telegram credentials.\n'
