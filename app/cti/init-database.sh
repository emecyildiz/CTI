#!/usr/bin/env sh
set -eu

if [ -z "${CTI_APP_PASSWORD:-}" ]; then
    echo "CTI_APP_PASSWORD is required." >&2
    exit 1
fi

if [ -z "${CTI_DASHBOARD_PASSWORD:-}" ]; then
    echo "CTI_DASHBOARD_PASSWORD is required." >&2
    exit 1
fi

psql \
    --set=ON_ERROR_STOP=1 \
    --set=cti_app_password="$CTI_APP_PASSWORD" \
    --set=cti_dashboard_password="$CTI_DASHBOARD_PASSWORD" \
    --username "$POSTGRES_USER" \
    --dbname "$POSTGRES_DB" \
    --file /opt/cti/schema.sql
