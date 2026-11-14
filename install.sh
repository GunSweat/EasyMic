#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="${HOME}/.local/bin"
UNIT_DIR="${HOME}/.config/systemd/user"
AUTOSTART_DIR="${HOME}/.config/autostart"
DOC_DIR="${HOME}/.local/share/doc/easymic"
CONFIG_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}"

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWarning:\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31mError:\033[0m %s\n' "$*" >&2; exit 1; }

command -v dotnet >/dev/null 2>&1 || fail "dotnet was not found. Please install the .NET 10 SDK."
command -v pactl >/dev/null 2>&1 || fail "pactl was not found. Please install pipewire-pulse/pulseaudio-utils."
command -v parec >/dev/null 2>&1 || fail "parec was not found. Please install pulseaudio-utils."
command -v pacat >/dev/null 2>&1 || fail "pacat was not found. Please install pulseaudio-utils."

case "$(uname -m)" in
    x86_64|amd64) RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) fail "Unsupported architecture: $(uname -m)" ;;
esac

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

info "Building EasyMic (${RID}) ..."
dotnet publish "${ROOT}/src/EasyMic.Daemon/EasyMic.Daemon.csproj" \
    -c Release -r "${RID}" --self-contained false \
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false \
    -o "${TMP}/daemon" >/dev/null

dotnet publish "${ROOT}/src/EasyMic.Cli/EasyMic.Cli.csproj" \
    -c Release -r "${RID}" --self-contained false \
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false \
    -o "${TMP}/cli" >/dev/null

info "Installing binaries to ${BIN_DIR} ..."
mkdir -p "${BIN_DIR}"
install -Dm755 "${TMP}/daemon/easymicd" "${BIN_DIR}/easymicd"
install -Dm755 "${TMP}/cli/easymic" "${BIN_DIR}/easymic"

info "Installing systemd user service ..."
mkdir -p "${UNIT_DIR}"
install -Dm644 "${ROOT}/packaging/easymicd.service" "${UNIT_DIR}/easymicd.service"

info "Installing login notification ..."
mkdir -p "${AUTOSTART_DIR}"
sed "s|^Exec=easymic notify$|Exec=${BIN_DIR}/easymic notify|; s|^TryExec=easymic$|TryExec=${BIN_DIR}/easymic|" \
    "${ROOT}/packaging/easymic-notify.desktop" > "${AUTOSTART_DIR}/easymic-notify.desktop"
chmod 644 "${AUTOSTART_DIR}/easymic-notify.desktop"

install -Dm644 "${ROOT}/README.md" "${DOC_DIR}/README.md" 2>/dev/null || true

info "Cleaning up old MicMonitor remnants ..."
pkill -f 'micmon( |$)' >/dev/null 2>&1 || true
for module in $(pactl list short modules 2>/dev/null | awk '
    ($2 == "module-null-sink" && $0 ~ /sink_name=micmon_virtual/) ||
    ($2 == "module-remap-source" && $0 ~ /source_name=micmon_virtual_mic/) ||
    ($2 == "module-loopback" && $0 ~ /source=micmon_virtual[.]monitor/) { print $1 }'); do
    pactl unload-module "${module}" >/dev/null 2>&1 || true
done

info "Stopping any running instance ..."
"${BIN_DIR}/easymic" daemon stop >/dev/null 2>&1 || true

info "Enabling easymicd.service ..."
if command -v systemctl >/dev/null 2>&1; then
    systemctl --user daemon-reload >/dev/null 2>&1 || true
    if systemctl --user enable --now easymicd.service >/dev/null 2>&1; then
        sleep 1
        if systemctl --user is-active --quiet easymicd.service; then
            info "Daemon is running."
        else
            warn "The service is enabled but not running yet. Check: systemctl --user status easymicd"
        fi
    else
        warn "The systemd user service could not be started (session may be degraded)."
        warn "Start the daemon manually with: ${BIN_DIR}/easymic daemon start"
    fi
else
    warn "systemctl was not found; start the daemon manually: ${BIN_DIR}/easymic daemon start"
fi

info "Done."
cat <<EOF

EasyMic is installed.

  UI:           ${BIN_DIR}/easymic
  Status:       ${BIN_DIR}/easymic status
  Daemon:       ${BIN_DIR}/easymic daemon status
  Settings:     ${CONFIG_DIR}/easymic/settings.json
  Autostart:    ${AUTOSTART_DIR}/easymic-notify.desktop

If ${BIN_DIR} is not in your PATH yet, add it to your shell configuration.
EOF
