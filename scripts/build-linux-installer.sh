#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_dir=$(dirname "$script_dir")
cd "$repository_dir"

for command_name in sed sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'ERROR: %s is required to build the Linux installer.\n' "$command_name" >&2
        exit 1
    }
done

version=$(tr -d '\r\n' < VERSION)
template=installer/linux/cti-setup.sh
grep -Fq '__CTI_VERSION__' "$template" || {
    printf 'ERROR: the Linux installer version placeholder is missing.\n' >&2
    exit 1
}

output_dir=${1:-dist}
mkdir -p "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)
installer_name="CTI-Setup-$version-linux.sh"
installer_path="$output_dir/$installer_name"

sed "s/__CTI_VERSION__/$version/g" "$template" > "$installer_path"
chmod 755 "$installer_path"
grep -Fq '__CTI_VERSION__' "$installer_path" && {
    printf 'ERROR: the generated Linux installer still contains a placeholder.\n' >&2
    exit 1
}

checksum=$(sha256sum "$installer_path" | awk '{print $1}')
printf '%s  %s\n' "$checksum" "$installer_name" > "$installer_path.sha256"

printf 'Created %s\n' "$installer_path"
printf 'SHA-256: %s\n' "$checksum"
