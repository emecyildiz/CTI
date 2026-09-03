#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$script_dir"

use_existing_n8n=false
skip_workflow_import=false
telegram_webhook_url=
telegram_proxy_hops=1
while [ "$#" -gt 0 ]; do
    case "$1" in
        --existing-n8n) use_existing_n8n=true ;;
        --skip-workflow-import) skip_workflow_import=true ;;
        --telegram-webhook-url)
            [ "$#" -ge 2 ] || { printf 'ERROR: --telegram-webhook-url requires an HTTPS URL.\n' >&2; exit 1; }
            telegram_webhook_url=$2
            shift
            ;;
        --n8n-proxy-hops)
            [ "$#" -ge 2 ] || { printf 'ERROR: --n8n-proxy-hops requires a number.\n' >&2; exit 1; }
            telegram_proxy_hops=$2
            shift
            ;;
        --help)
            printf 'Usage: ./setup.sh [--existing-n8n] [--skip-workflow-import] [--telegram-webhook-url https://hooks.example.com/] [--n8n-proxy-hops N]\n'
            exit 0
            ;;
        *)
            printf 'ERROR: unknown option: %s\n' "$1" >&2
            exit 1
            ;;
    esac
    shift
done

fail() {
    printf 'ERROR: %s\n' "$1" >&2
    exit 1
}

secret() {
    od -An -N32 -tx1 /dev/urandom | tr -d ' \n'
}

validate_telegram_webhook() {
    case "$telegram_webhook_url" in
        https://*) ;;
        *) fail 'The interactive Telegram query requires an absolute HTTPS webhook URL.' ;;
    esac
    case "$telegram_webhook_url" in
        *\?*|*\#*|*@*|*[[:space:]]*) fail 'The Telegram webhook URL must not contain credentials, whitespace, a query, or a fragment.' ;;
    esac
    authority=${telegram_webhook_url#https://}
    authority=${authority%%/*}
    [ -n "$authority" ] || fail 'The Telegram webhook URL must contain a public hostname.'
    case "$authority" in
        localhost|localhost:*|127.*|0.0.0.0|0.0.0.0:*|'[::1]'|'[::1]':*)
            fail 'The Telegram webhook URL cannot use a loopback or wildcard host.'
            ;;
    esac
    case "$telegram_proxy_hops" in
        ''|*[!0-9]*) fail 'N8N proxy hops must be a non-negative integer.' ;;
    esac
    [ "$telegram_proxy_hops" -le 16 ] || fail 'N8N proxy hops must be 16 or less.'
    telegram_webhook_url=${telegram_webhook_url%/}/
}

upsert_environment() {
    key=$1
    value=$2
    temporary_env=".env.tmp.$$"
    awk -v key="$key" -v value="$value" '
        BEGIN { found = 0 }
        index($0, key "=") == 1 { print key "=" value; found = 1; next }
        { print }
        END { if (!found) print key "=" value }
    ' .env > "$temporary_env"
    chmod 600 "$temporary_env"
    mv "$temporary_env" .env
}

[ -z "$telegram_webhook_url" ] || validate_telegram_webhook
[ "$use_existing_n8n" != "true" ] || [ -z "$telegram_webhook_url" ] || \
    fail 'Configure WEBHOOK_URL on the existing n8n service itself; --telegram-webhook-url is for managed n8n only.'

command -v docker >/dev/null 2>&1 || {
    printf 'Docker Engine and Docker Compose are required.\n'
    printf 'Install guide: https://docs.docker.com/engine/install/\n'
    fail 'Docker was not found. Install it and rerun setup.sh.'
}
docker compose version >/dev/null 2>&1 || fail 'Docker Compose v2 is unavailable.'
docker info >/dev/null 2>&1 || fail 'The Docker daemon is not running or is not accessible.'

if [ ! -f .env ]; then
    printf 'Creating a private local configuration...\n'
    managed_n8n=true
    n8n_container=cti-n8n
    if [ "$use_existing_n8n" = "true" ]; then
        managed_n8n=false
        n8n_container=
    fi
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
N8N_CONTAINER=$n8n_container
CTI_MANAGED_N8N=$managed_n8n
N8N_PORT=5678
N8N_TIMEZONE=${TZ:-UTC}
N8N_ENCRYPTION_KEY=$(secret)
CTI_TELEGRAM_QUERY_ENABLED=false
N8N_WEBHOOK_URL=http://localhost:5678/
N8N_PROXY_HOPS=0
EOF
    chmod 600 .env
fi

if [ -n "$telegram_webhook_url" ]; then
    upsert_environment CTI_TELEGRAM_QUERY_ENABLED true
    upsert_environment N8N_WEBHOOK_URL "$telegram_webhook_url"
    upsert_environment N8N_PROXY_HOPS "$telegram_proxy_hops"
fi

sh ./scripts/install.sh

managed_n8n=$(sed -n 's/^CTI_MANAGED_N8N=//p' .env | head -n 1)
if [ "$managed_n8n" = "true" ]; then
    n8n_container=$(sed -n 's/^N8N_CONTAINER=//p' .env | head -n 1)
    n8n_container=${n8n_container:-cti-n8n}
    attempt=0
    until docker exec "$n8n_container" n8n --version >/dev/null 2>&1; do
        attempt=$((attempt + 1))
        [ "$attempt" -lt 60 ] || fail 'The managed n8n service did not become ready in time.'
        sleep 2
    done

    if [ "$skip_workflow_import" != "true" ] && ! docker exec "$n8n_container" sh -c \
        'test -f /home/node/.n8n/.cti-workflows-imported-v1'; then
        printf 'Importing disabled CTI workflows...\n'
        CTI_IMPORT_CONFIRM=IMPORT_DISABLED_WORKFLOWS sh ./scripts/import-workflows.sh
        docker exec "$n8n_container" sh -c \
            'touch /home/node/.n8n/.cti-workflows-imported-v1'
    fi
fi

printf '\nCTI Self-Hosted is running.\n'
dashboard_port=$(sed -n 's/^CTI_DASHBOARD_PORT=//p' .env | head -n 1)
n8n_port=$(sed -n 's/^N8N_PORT=//p' .env | head -n 1)
printf 'Dashboard: http://127.0.0.1:%s\n' "${dashboard_port:-8080}"
if [ "$managed_n8n" = "true" ]; then
    printf 'n8n:       http://127.0.0.1:%s\n' "${n8n_port:-5678}"
    printf 'Create the first n8n owner account, then use the protected dashboard setup page to map CTI credentials.\n'
else
    printf 'Connect the existing n8n instance to the CTI network, then follow N8N-SETUP.md.\n'
fi
telegram_query_enabled=$(sed -n 's/^CTI_TELEGRAM_QUERY_ENABLED=//p' .env | head -n 1)
if [ "$telegram_query_enabled" = "true" ]; then
    printf 'Interactive Telegram query: configured for %s\n' "$(sed -n 's/^N8N_WEBHOOK_URL=//p' .env | head -n 1)"
    printf 'The HTTPS route must reach n8n before you activate CTI Telegram Query.\n'
else
    printf 'Interactive Telegram query: disabled (outbound reports and alerts can still be configured).\n'
fi
