#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_dir=$(dirname "$script_dir")
cd "$repository_dir"

for command_name in git tar zip sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'ERROR: %s is required to build a release.\n' "$command_name" >&2
        exit 1
    }
done

version=$(tr -d '\r\n' < VERSION)
case "$version" in
    [0-9]*.[0-9]*.[0-9]*-rc.[0-9]*|[0-9]*.[0-9]*.[0-9]*) ;;
    *) printf 'ERROR: invalid VERSION value: %s\n' "$version" >&2; exit 1 ;;
esac

output_dir=${1:-dist}
mkdir -p "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)

package_name="cti-self-hosted-$version"
archive_name="$package_name.zip"
temporary_directory=$(mktemp -d)
cleanup() { rm -rf "$temporary_directory"; }
trap cleanup EXIT HUP INT TERM

mkdir -p "$temporary_directory/$package_name"
git archive --format=tar HEAD | tar -xf - -C "$temporary_directory/$package_name"

(
    cd "$temporary_directory"
    zip -qr "$output_dir/$archive_name" "$package_name"
)

checksum=$(sha256sum "$output_dir/$archive_name" | awk '{print $1}')
printf '%s  %s\n' "$checksum" "$archive_name" \
    > "$output_dir/$archive_name.sha256"

commit=$(git rev-parse HEAD)
generated_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')
cat > "$output_dir/$package_name.manifest.json" <<EOF
{
  "name": "CTI Self-Hosted",
  "version": "$version",
  "commit": "$commit",
  "generated_at": "$generated_at",
  "archive": "$archive_name",
  "sha256": "$checksum"
}
EOF

printf 'Created %s\n' "$output_dir/$archive_name"
printf 'SHA-256: %s\n' "$checksum"
