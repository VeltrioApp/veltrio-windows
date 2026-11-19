#!/usr/bin/env bash
#
# Put the player's native files where the project expects them (mpv/,
# gitignored):
#
#   mpv/libmpv-2.dll   LGPL libmpv, x86_64
#   mpv/cacert.pem     CA roots for mpv's TLS
#
# Versions and checksums come from mpv.version. Needs curl and 7-Zip
# (7z / 7zz / 7za: preinstalled on GitHub's Windows runners; `brew install
# sevenzip` on a Mac; 7-Zip's folder on PATH on Windows).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/mpv"
# KEY=value lines; values restricted to URL-ish characters before eval.
eval "$(grep -E '^[A-Z0-9_]+=[A-Za-z0-9._:/-]+$' "$ROOT/mpv.version")"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
check() { [ "$(sha256 "$1")" = "$2" ] || { echo "checksum mismatch for $1" >&2; exit 1; }; }

SEVENZIP="$(command -v 7z || command -v 7zz || command -v 7za || true)"
[ -n "$SEVENZIP" ] || { echo "7-Zip not found (7z, 7zz or 7za)" >&2; exit 1; }

echo "==> libmpv $MPV_RELEASE"
curl -fsSL -o "$TMP/mpv.7z" "https://github.com/zhongfly/mpv-winbuild/releases/download/$MPV_RELEASE/$MPV_ASSET"
check "$TMP/mpv.7z" "$MPV_SHA256"
"$SEVENZIP" e -y -bso0 -bsp0 "$TMP/mpv.7z" -o"$TMP" libmpv-2.dll

echo "==> CA bundle"
curl -fsSL -o "$TMP/cacert.pem" "$CACERT_URL"
check "$TMP/cacert.pem" "$CACERT_SHA256"

mkdir -p "$OUT"
mv "$TMP/libmpv-2.dll" "$TMP/cacert.pem" "$OUT/"
echo "==> mpv/ ready"
