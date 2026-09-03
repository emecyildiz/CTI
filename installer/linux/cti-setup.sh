#!/usr/bin/env sh
set -eu

version='__CTI_VERSION__'
repository='emecyildiz/CTI'
install_dir=
use_existing_n8n=false
non_interactive=false
prepare_only=false

fail() {
    printf '\nERROR: %s\n' "$1" >&2
    exit 1
}

usage() {
    cat <<'EOF'
CTI Self-Hosted Linux installer

Usage:
  ./CTI-Setup-<version>-linux.sh [options]

Options:
  --install-dir PATH  Installation directory
  --existing-n8n      Do not deploy the bundled n8n container
  --non-interactive   Accept defaults without prompting
  --prepare-only      Verify and copy files without starting services
  --help              Show this help
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --install-dir)
            [ "$#" -ge 2 ] || fail '--install-dir requires a path.'
            install_dir=$2
            shift
            ;;
        --existing-n8n) use_existing_n8n=true ;;
        --non-interactive) non_interactive=true ;;
        --prepare-only) prepare_only=true ;;
        --help) usage; exit 0 ;;
        *) fail "Unknown option: $1" ;;
    esac
    shift
done

[ "$(uname -s 2>/dev/null || true)" = "Linux" ] || fail 'This installer supports Linux only. Use the release ZIP on other systems.'
case "$(uname -m 2>/dev/null || true)" in
    x86_64|amd64|aarch64|arm64) ;;
    *) fail 'This installer supports x86_64 and ARM64 Linux hosts.' ;;
esac

if [ "$prepare_only" != "true" ]; then
    command -v docker >/dev/null 2>&1 || {
        printf 'Docker Engine is required before CTI can be installed.\n'
        printf 'Official guide: https://docs.docker.com/engine/install/\n'
        fail 'Docker was not found. Install Docker Engine, then rerun this installer.'
    }
    docker compose version >/dev/null 2>&1 || fail 'Docker Compose v2 is unavailable. Update the Docker installation and retry.'
    docker info >/dev/null 2>&1 || {
        printf 'The current user must be allowed to access the Docker daemon.\n'
        printf 'Post-install guide: https://docs.docker.com/engine/install/linux-postinstall/\n'
        fail 'The Docker daemon is stopped or inaccessible.'
    }
fi
command -v sha256sum >/dev/null 2>&1 || fail 'sha256sum is required.'
command -v unzip >/dev/null 2>&1 || fail 'unzip is required. Install the unzip package and retry.'

if command -v curl >/dev/null 2>&1; then
    download() { curl --fail --location --silent --show-error --output "$2" "$1"; }
elif command -v wget >/dev/null 2>&1; then
    download() { wget --quiet --output-document="$2" "$1"; }
else
    fail 'curl or wget is required to download the versioned release package.'
fi

if [ -z "$install_dir" ]; then
    if [ "$(id -u)" -eq 0 ]; then
        default_dir=/opt/cti-self-hosted
    else
        default_dir=${HOME:?HOME is unavailable}/.local/share/cti-self-hosted
    fi
    install_dir=$default_dir
    if [ "$non_interactive" != "true" ] && [ -t 0 ]; then
        printf 'Installation directory [%s]: ' "$default_dir"
        IFS= read -r answer
        [ -z "$answer" ] || install_dir=$answer
        printf 'Deploy a managed local n8n container? [Y/n]: '
        IFS= read -r answer
        case "$answer" in n|N|no|NO|No) use_existing_n8n=true ;; esac
    fi
fi

case "$install_dir" in
    ''|/) fail 'The installation directory cannot be empty or the filesystem root.' ;;
esac

temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/cti-installer.XXXXXX")
cleanup() {
    case "$temporary_directory" in
        "${TMPDIR:-/tmp}"/cti-installer.*) rm -rf -- "$temporary_directory" ;;
    esac
}
trap cleanup EXIT HUP INT TERM

archive_name="cti-self-hosted-$version.zip"
checksum_name="$archive_name.sha256"
default_release_base="https://github.com/$repository/releases/download/v$version"
release_base=${CTI_RELEASE_BASE:-$default_release_base}
case "$release_base" in
    https://*|file://*) ;;
    *) fail 'CTI_RELEASE_BASE must use HTTPS or a local file URL.' ;;
esac
release_base=${release_base%/}

printf '\nDownloading CTI Self-Hosted %s...\n' "$version"
download "$release_base/$archive_name" "$temporary_directory/$archive_name"
download "$release_base/$checksum_name" "$temporary_directory/$checksum_name"

printf 'Verifying SHA-256 checksum...\n'
(cd "$temporary_directory" && sha256sum -c "$checksum_name")

mkdir -p "$temporary_directory/extracted"
unzip -q "$temporary_directory/$archive_name" -d "$temporary_directory/extracted"
package_dir="$temporary_directory/extracted/cti-self-hosted-$version"
[ -f "$package_dir/setup.sh" ] || fail 'The downloaded release package has an unexpected layout.'
[ -f "$package_dir/compose.yml" ] || fail 'The downloaded release package is incomplete.'
[ "$(tr -d '\r\n' < "$package_dir/VERSION")" = "$version" ] || fail 'The downloaded package version does not match the installer.'

if [ -d "$install_dir" ] && [ -n "$(find "$install_dir" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]; then
    [ -f "$install_dir/compose.yml" ] && [ -f "$install_dir/setup.sh" ] && [ -f "$install_dir/VERSION" ] \
        || fail 'The destination is not empty and is not an existing CTI Self-Hosted installation.'
fi

mkdir -p "$install_dir"
preserved_environment=
if [ -f "$install_dir/.env" ]; then
    preserved_environment="$temporary_directory/existing.env"
    cp "$install_dir/.env" "$preserved_environment"
fi

printf 'Copying installation files to %s...\n' "$install_dir"
cp -R "$package_dir/." "$install_dir/"
if [ -n "$preserved_environment" ]; then
    cp "$preserved_environment" "$install_dir/.env"
    chmod 600 "$install_dir/.env"
fi
chmod +x "$install_dir/setup.sh" "$install_dir"/scripts/*.sh

if [ "$prepare_only" = "true" ]; then
    printf '\nPackage verification and preparation complete. Services were not started.\n'
    printf 'Files: %s\n' "$install_dir"
    printf 'Review the files, then run: cd %s && sh ./setup.sh\n' "$install_dir"
    exit 0
fi

printf 'Starting CTI setup...\n'
if [ "$use_existing_n8n" = "true" ]; then
    (cd "$install_dir" && sh ./setup.sh --existing-n8n --skip-workflow-import)
else
    (cd "$install_dir" && sh ./setup.sh)
fi

printf '\nInstallation complete.\n'
printf 'Files:     %s\n' "$install_dir"
printf 'Dashboard: http://127.0.0.1:8080\n'
if [ "$use_existing_n8n" != "true" ]; then
    printf 'n8n:       http://127.0.0.1:5678\n'
fi
printf '\nFor a remote server, keep both services private and open an SSH tunnel from your computer:\n'
printf 'ssh -L 8080:127.0.0.1:8080 -L 5678:127.0.0.1:5678 USER@SERVER\n'
