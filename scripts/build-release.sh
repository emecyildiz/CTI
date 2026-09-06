#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_dir=$(dirname "$script_dir")
cd "$repository_dir"

for command_name in git grep sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'ERROR: %s is required to build a release.\n' "$command_name" >&2
        exit 1
    }
done

version=$(tr -d '\r\n' < VERSION)
printf '%s\n' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-rc\.[0-9]+)?$' || {
    printf 'ERROR: invalid VERSION value.\n' >&2
    exit 1
}
committed_version=$(git show HEAD:VERSION | tr -d '\r\n')
[ "$version" = "$committed_version" ] || {
    printf 'ERROR: VERSION differs from HEAD; commit release preparation before packaging.\n' >&2
    exit 1
}
git diff --quiet HEAD -- || {
    printf 'ERROR: tracked changes are not committed; refusing an ambiguous release build.\n' >&2
    exit 1
}

output_dir=${1:-dist}
mkdir -p "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)

package_name="cti-self-hosted-$version"
archive_name="$package_name.zip"
# Archive only committed files; ignored and personal untracked notes stay out.
git archive --format=zip --prefix="$package_name/" \
    --output="$output_dir/$archive_name" HEAD

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
