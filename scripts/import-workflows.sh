#!/usr/bin/env sh
set -eu

[ "${CTI_IMPORT_CONFIRM:-}" = "IMPORT_DISABLED_WORKFLOWS" ] || {
    printf 'ERROR: set CTI_IMPORT_CONFIRM=IMPORT_DISABLED_WORKFLOWS for the one-time import.\n' >&2
    exit 1
}

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
package_dir=$(dirname "$script_dir")
cd "$package_dir"

[ -f .env ] || {
    printf 'ERROR: .env is missing.\n' >&2
    exit 1
}

n8n_container=${N8N_CONTAINER:-$(sed -n 's/^N8N_CONTAINER=//p' .env | head -n 1)}
[ -n "$n8n_container" ] || {
    printf 'ERROR: set N8N_CONTAINER in .env.\n' >&2
    exit 1
}

cti_network=$(sed -n 's/^CTI_NETWORK_NAME=//p' .env | head -n 1)
cti_network=${cti_network:-cti-self-hosted}

N8N_CONTAINER="$n8n_container" sh ./scripts/preflight.sh
docker network inspect "$cti_network" >/dev/null 2>&1 || {
    printf 'ERROR: CTI Docker network does not exist. Run install.sh first.\n' >&2
    exit 1
}

if ! docker network inspect "$cti_network" --format '{{json .Containers}}' |
    grep -Fq "\"Name\":\"$n8n_container\""; then
    docker network connect "$cti_network" "$n8n_container"
fi

container_directory="/tmp/cti-self-hosted-workflows-$$"
cleanup() {
    docker exec "$n8n_container" rm -rf "$container_directory" >/dev/null 2>&1 || true
}
trap cleanup EXIT HUP INT TERM

docker exec "$n8n_container" mkdir -p "$container_directory"
docker cp "$package_dir/workflows/." "$n8n_container:$container_directory"
docker exec "$n8n_container" \
    n8n import:workflow --separate --input="$container_directory"

trap - EXIT HUP INT TERM
cleanup
printf 'Imported eight disabled CTI workflows into %s. Map credentials before activation.\n' \
    "$n8n_container"

