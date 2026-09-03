#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_dir=$(dirname "$script_dir")
cd "$repository_dir"

for command_name in dotnet sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'ERROR: %s is required to build the Windows installer.\n' "$command_name" >&2
        exit 1
    }
done

version=$(tr -d '\r\n' < VERSION)
output_dir=${1:-dist}
mkdir -p "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)

payload="$output_dir/cti-self-hosted-$version.zip"
test -f "$payload" || {
    printf 'ERROR: release payload not found: %s\n' "$payload" >&2
    exit 1
}

temporary_directory=$(mktemp -d)
cleanup() { rm -rf "$temporary_directory"; }
trap cleanup EXIT HUP INT TERM

dotnet publish installer/windows/CtiInstaller.csproj \
    --configuration Release \
    --runtime win-x64 \
    --self-contained true \
    --output "$temporary_directory/publish" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:Version="$version" \
    -p:PayloadPath="$payload"

installer_name="CTI-Setup-$version-win-x64.exe"
installer_path="$output_dir/$installer_name"
test -f "$temporary_directory/publish/CtiInstaller.exe" || {
    printf 'ERROR: the single-file Windows installer was not produced.\n' >&2
    exit 1
}
cp "$temporary_directory/publish/CtiInstaller.exe" "$installer_path"

checksum=$(sha256sum "$installer_path" | awk '{print $1}')
printf '%s  %s\n' "$checksum" "$installer_name" > "$installer_path.sha256"

printf 'Created %s\n' "$installer_path"
printf 'SHA-256: %s\n' "$checksum"
