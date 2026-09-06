#!/usr/bin/env sh
# Offline regression: no Docker daemon, containers, network or credentials used.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/cti-recovery-diagnostics.XXXXXX")
trap 'rm -rf -- "$test_dir"' EXIT HUP INT TERM
mkdir -p "$test_dir/bin" "$test_dir/runs"
cat > "$test_dir/bin/docker" <<'EOF'
#!/usr/bin/env sh
case "$*" in
    'compose up '*) exit 42 ;;
    'compose logs '*) printf 'Mock startup failure; no Docker daemon used.\n' ;;
    'compose down '*) printf 'cleanup\n' >> "$MOCK_CLEANUP_MARKER" ;;
    *) printf 'Unexpected mock Docker invocation\n' >&2; exit 99 ;;
esac
EOF
chmod +x "$test_dir/bin/docker"
status=0
PATH="$test_dir/bin:$PATH" CTI_TEST_PARENT="$test_dir/runs" \
    MOCK_CLEANUP_MARKER="$test_dir/cleanup" \
    sh "$root/scripts/tests/recovery.sh" > "$test_dir/output" 2>&1 || status=$?
[ "$status" = 42 ] || { cat "$test_dir/output"; exit 1; }
grep -F '::error title=Recovery regression failed::stage=start-services exit=42' "$test_dir/output" >/dev/null
[ "$(cat "$test_dir/cleanup")" = cleanup ]
for path in "$test_dir/runs"/cti-recovery-test.*; do
    [ ! -e "$path" ] || { printf 'Temporary test directory leaked\n' >&2; exit 1; }
done
printf 'PASS: failure stage, original exit status and cleanup (offline)\n'
