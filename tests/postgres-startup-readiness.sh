#!/usr/bin/env sh
# Short, ephemeral proof of the PostgreSQL socket-only initialization boundary.
# No host ports, mounted files, persistent volume or external network.
set -eu
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' docker run --rm -i --network none --user postgres \
    --tmpfs /test:rw,size=128m,mode=1777 -e PGDATA=/test/data \
    postgres:16-alpine@sha256:57c72fd2a128e416c7fcc499958864df5301e940bca0a56f58fddf30ffc07777 \
    sh -s <<'EOF'
set -eu
trap 'pg_ctl -D "$PGDATA" -m fast -w stop >/dev/null 2>&1 || true' EXIT
initdb -D "$PGDATA" -A trust -U cti_owner >/dev/null
pg_ctl -D "$PGDATA" -o "-k /test -c listen_addresses=''" -w start > /test/start.log
pg_isready -h /test -U cti_owner -d postgres >/dev/null
if pg_isready -h 127.0.0.1 -U cti_owner -d postgres >/dev/null; then
    printf 'FAIL: initialization-only server passed TCP readiness\n' >&2
    exit 1
fi
printf 'PASS: socket probe is premature; TCP probe rejects initialization-only server\n'
pg_ctl -D "$PGDATA" -m fast -w stop >/dev/null
pg_ctl -D "$PGDATA" -o '-k /test -c listen_addresses=127.0.0.1' -w start > /test/start.log
pg_isready -h 127.0.0.1 -U cti_owner -d postgres >/dev/null
printf 'PASS: TCP readiness accepts the final PostgreSQL server\n'
EOF
