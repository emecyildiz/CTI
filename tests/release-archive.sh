#!/usr/bin/env sh
# Small local archive check: no containers, deployment, tagging or publication.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/cti-release-check.XXXXXX")
trap 'rm -rf -- "$test_dir"' EXIT HUP INT TERM
cd "$root"
sh scripts/build-release.sh "$test_dir"
version=$(tr -d '\r\n' < VERSION)
name="cti-self-hosted-$version"
archive="$test_dir/$name.zip"
(cd "$test_dir" && sha256sum -c "$name.zip.sha256")
unzip -t "$archive" > "$test_dir/integrity.log"
unzip -Z1 "$archive" > "$test_dir/entries"
for file in VERSION LICENSE compose.yml setup.sh setup.ps1 manage.ps1 scripts/windows-lifecycle.ps1 .env.example \
    app/cti/schema.sql app/cti-dashboard/Program.cs \
    workflows/n8n-workflow-error-alerts.json scripts/restore.sh \
    installer/windows/PackageUpdate.cs installer/windows/RecentInstallations.cs; do
    grep -Fx "$name/$file" "$test_dir/entries" >/dev/null
done
while IFS= read -r entry; do
    case "$entry" in
        "$name/.env.example") ;;
        */.env|*/.env.*|*/.git/*|*/.codex/*|*/backups/*|*/dist/*|*.dump|*OBSİDİAN*)
            printf 'FAIL: private/generated entry in archive: %s\n' "$entry" >&2; exit 1 ;;
    esac
done < "$test_dir/entries"
archived_version=$(unzip -p "$archive" "$name/VERSION" | tr -d '\r\n')
[ "$archived_version" = "$version" ]
grep -F "\"version\": \"$version\"" "$test_dir/$name.manifest.json" >/dev/null
grep -F "\"commit\": \"$(git rev-parse HEAD)\"" "$test_dir/$name.manifest.json" >/dev/null
printf 'PASS: ZIP integrity, checksum, required files, version/commit and private-path exclusions\n'
