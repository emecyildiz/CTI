#!/usr/bin/env sh
# Offline regression tests. Only extracted setup functions run; Docker is never called.
set -eu

repository_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/cti-setup-tests.XXXXXX")
trap 'rm -rf -- "$temporary_directory"' EXIT HUP INT TERM

awk '
    /^(fail|validate_telegram_webhook|validate_telegram_mode|upsert_environment)\(\) \{/ { copying = 1 }
    copying { print }
    copying && /^}/ { copying = 0 }
' "$repository_dir/setup.sh" > "$temporary_directory/functions.sh"
. "$temporary_directory/functions.sh"
cd "$temporary_directory"

assert_valid() {
    actual=$(telegram_webhook_url=$1; telegram_proxy_hops=1; validate_telegram_webhook; printf '%s' "$telegram_webhook_url")
    [ "$actual" = "$2" ] || fail "Unexpected normalized URL: $actual"
}

assert_invalid() {
    if (telegram_webhook_url=$1; telegram_proxy_hops=1; validate_telegram_webhook) >/dev/null 2>&1; then
        fail "Unsafe URL was accepted: $1"
    fi
}

assert_valid 'https://hooks.example.com' 'https://hooks.example.com/'
assert_valid 'https://Hooks.Example.com:8443/n8n-webhooks/v1//' 'https://Hooks.Example.com:8443/n8n-webhooks/v1/'
assert_valid 'https://hooks.example.com/a%20b/~v1' 'https://hooks.example.com/a%20b/~v1/'
assert_invalid "$(printf 'https://hooks.example.com/\nCTI_DASHBOARD_BIND=0.0.0.0\nCTI_TEST=x')"
assert_invalid 'https://hooks.example.com/\nCTI_DASHBOARD_BIND=0.0.0.0\nCTI_TEST=x'
assert_invalid 'https://hooks.example.com/$POSTGRES_PASSWORD'
assert_invalid 'https://hooks.example.com/"quoted"'
assert_invalid "https://hooks.example.com/'quoted'"
assert_invalid 'https://hooks.example.com/path with space'
assert_invalid 'https://user:password@hooks.example.com/'
assert_invalid 'https://hooks.example.com/?query=1'
assert_invalid 'https://hooks.example.com/#fragment'
assert_invalid 'http://hooks.example.com/'
assert_invalid 'https://localhost/'
assert_invalid 'https://hooks.localhost/'
assert_invalid 'https://127.0.0.1/'
assert_invalid 'https://0.0.0.0/'
assert_invalid 'https://[::1]/'
assert_invalid 'https://-invalid.example.com/'
assert_invalid 'https://hooks.example.com:0/'
assert_invalid 'https://hooks.example.com:65536/'
assert_invalid 'https://hooks.example.com/bad%'
assert_invalid 'https://hooks.example.com/bad%2x'
if (telegram_webhook_url='https://hooks.example.com/'; telegram_proxy_hops=17; validate_telegram_webhook) >/dev/null 2>&1; then
    fail 'Out-of-range proxy hops was accepted.'
fi

cat > .env <<'EOF'
# Preserve unrelated settings and secrets.
POSTGRES_PASSWORD=unchanged-owner-secret
CTI_MANAGED_N8N=true
N8N_WEBHOOK_URL=http://old.example.com/
N8N_WEBHOOK_URL=http://duplicate.example.com/
EOF
upsert_environment N8N_WEBHOOK_URL 'https://hooks.example.com/base/'
[ "$(grep -c '^N8N_WEBHOOK_URL=' .env)" -eq 1 ] || fail 'Duplicate target keys were not removed.'
grep -Fxq 'POSTGRES_PASSWORD=unchanged-owner-secret' .env || fail 'Unrelated secret was changed.'
grep -Fxq '# Preserve unrelated settings and secrets.' .env || fail 'Comment was changed.'
upsert_environment CTI_TEST_LITERAL 'literal\nnot-an-assignment'
grep -Fxq 'CTI_TEST_LITERAL=literal\nnot-an-assignment' .env || fail 'The environment writer interpreted a backslash escape.'

telegram_webhook_url='https://hooks.example.com/'
use_existing_n8n=false
validate_telegram_mode
upsert_environment CTI_MANAGED_N8N false
cp .env expected.env
if (validate_telegram_mode) >/dev/null 2>&1; then
    fail 'A saved existing-n8n configuration was accepted for managed webhook updates.'
fi
cmp .env expected.env || fail 'Rejected configuration was changed.'
use_existing_n8n=true
if (validate_telegram_mode) >/dev/null 2>&1; then
    fail 'The existing-n8n flag was accepted for managed webhook updates.'
fi

printf 'PASS: shell setup URL safety, literal environment updates, and existing-n8n guard.\n'
