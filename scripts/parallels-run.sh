#!/usr/bin/env bash
#
# Build and launch the app in a Parallels Windows VM, from the Mac.
#
#   ./scripts/fetch-core.sh            # once: the core into core/
#   ./scripts/parallels-run.sh         # build in the VM, start the app on its desktop
#
# The working tree is zipped and served to the VM over Parallels' shared
# network (the host's address on it, 10.211.55.2 by default); the VM builds
# it with the .NET 10 SDK (installed to C:\dotnet on first run) and the app
# starts in the signed-in user's session. Works on Apple Silicon: the VM is
# Windows on ARM and runs the x64 build under emulation.
#
# Env: VM ("Windows 11"), HOST_IP (10.211.55.2), PORT (8765), CONFIG (Debug).
#
# Wine / CrossOver can't run the app: Windows App SDK calls an NT API Wine
# doesn't implement (NtCreateWaitCompletionPacket) and aborts at startup.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VM="${VM:-Windows 11}"
HOST_IP="${HOST_IP:-10.211.55.2}"
PORT="${PORT:-8765}"
CONFIG="${CONFIG:-Debug}"

[ -f "$ROOT/core/veltrio_core.dll" ] || { echo "core/ is missing: run ./scripts/fetch-core.sh first" >&2; exit 1; }
command -v prlctl >/dev/null || { echo "prlctl not found: is Parallels Desktop installed?" >&2; exit 1; }

case "$(prlctl status "$VM" | awk '{print $NF}')" in
  running) ;;
  suspended) prlctl resume "$VM" ;;
  *) prlctl start "$VM" ;;
esac

SERVE="$(mktemp -d)"
cleanup() { [ -n "${SERVER:-}" ] && kill "$SERVER" 2>/dev/null; rm -rf "$SERVE"; }
trap cleanup EXIT

echo "==> Packing the working tree"
(cd "$ROOT" && zip -qr "$SERVE/src.zip" . -x ".git/*" "*/bin/*" "*/obj/*" "dist/*")

cat > "$SERVE/build.ps1" <<EOF
\$ErrorActionPreference = 'Stop'
\$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
\$env:DOTNET_NOLOGO = '1'
# A VM resumed after days has a stale clock, and TLS then fails.
Set-Date -Date ([DateTime]::Parse('$(date -u +%Y-%m-%dT%H:%M:%SZ)')) | Out-Null
if (-not (Test-Path C:\dotnet\dotnet.exe)) {
  Write-Output '==> Installing the .NET 10 SDK to C:\dotnet'
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile C:\dotnet-install.ps1 -UseBasicParsing
  & C:\dotnet-install.ps1 -Channel 10.0 -InstallDir C:\dotnet | Out-Null
}
Stop-Process -Name Veltrio -Force -ErrorAction SilentlyContinue
\$src = 'C:\src\veltrio-windows'
Invoke-WebRequest http://$HOST_IP:$PORT/src.zip -OutFile C:\src.zip -UseBasicParsing
if (Test-Path "\$src\src") { Remove-Item -Recurse -Force "\$src\src" }
Expand-Archive C:\src.zip -DestinationPath \$src -Force
Set-Location \$src
& C:\dotnet\dotnet.exe build Veltrio.Windows.sln -c $CONFIG -p:Platform=x64 -nologo -v:q -clp:ErrorsOnly
exit \$LASTEXITCODE
EOF

echo "==> Serving it on $HOST_IP:$PORT"
python3 -I -m http.server "$PORT" --bind "$HOST_IP" --directory "$SERVE" >/dev/null 2>&1 &
SERVER=$!
disown "$SERVER"
sleep 1

echo "==> Building in \"$VM\""
prlctl exec "$VM" powershell -NoProfile -ExecutionPolicy Bypass -Command \
  "Invoke-WebRequest http://$HOST_IP:$PORT/build.ps1 -OutFile C:\\veltrio-build.ps1 -UseBasicParsing; & C:\\veltrio-build.ps1"

echo "==> Starting Veltrio on the VM's desktop"
EXE="C:\\src\\veltrio-windows\\src\\Veltrio\\bin\\x64\\$CONFIG\\net10.0-windows10.0.19041.0\\win-x64\\Veltrio.exe"
# `start` returns at once; prlctl can still hang on the detached window, so don't wait for it.
(prlctl exec "$VM" --current-user cmd /c "start \"\" $EXE" >/dev/null 2>&1 &)
sleep 3
echo "==> Done. Screenshot: prlctl capture \"$VM\" --file shot.png"
