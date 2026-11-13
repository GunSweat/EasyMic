#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="${HOME}/.local/bin"
UNIT_DIR="${HOME}/.config/systemd/user"
AUTOSTART_DIR="${HOME}/.config/autostart"
DOC_DIR="${HOME}/.local/share/doc/easymic"
CONFIG_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}"

info() { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWarnung:\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31mFehler:\033[0m %s\n' "$*" >&2; exit 1; }

command -v dotnet >/dev/null 2>&1 || fail "dotnet wurde nicht gefunden. Bitte das .NET 10 SDK installieren."
command -v pactl >/dev/null 2>&1 || fail "pactl wurde nicht gefunden. Bitte pipewire-pulse/pulseaudio-utils installieren."
command -v parec >/dev/null 2>&1 || fail "parec wurde nicht gefunden. Bitte pulseaudio-utils installieren."
command -v pacat >/dev/null 2>&1 || fail "pacat wurde nicht gefunden. Bitte pulseaudio-utils installieren."

case "$(uname -m)" in
    x86_64|amd64) RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) fail "Nicht unterstützte Architektur: $(uname -m)" ;;
esac

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

info "Baue EasyMic (${RID}) ..."
dotnet publish "${ROOT}/src/EasyMic.Daemon/EasyMic.Daemon.csproj" \
    -c Release -r "${RID}" --self-contained false \
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false \
    -o "${TMP}/daemon" >/dev/null

dotnet publish "${ROOT}/src/EasyMic.Cli/EasyMic.Cli.csproj" \
    -c Release -r "${RID}" --self-contained false \
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false \
    -o "${TMP}/cli" >/dev/null

info "Installiere Binaries nach ${BIN_DIR} ..."
mkdir -p "${BIN_DIR}"
install -Dm755 "${TMP}/daemon/easymicd" "${BIN_DIR}/easymicd"
install -Dm755 "${TMP}/cli/easymic" "${BIN_DIR}/easymic"

info "Installiere systemd-User-Service ..."
mkdir -p "${UNIT_DIR}"
install -Dm644 "${ROOT}/packaging/easymicd.service" "${UNIT_DIR}/easymicd.service"

info "Installiere Login-Benachrichtigung ..."
mkdir -p "${AUTOSTART_DIR}"
sed "s|^Exec=easymic notify$|Exec=${BIN_DIR}/easymic notify|; s|^TryExec=easymic$|TryExec=${BIN_DIR}/easymic|" \
    "${ROOT}/packaging/easymic-notify.desktop" > "${AUTOSTART_DIR}/easymic-notify.desktop"
chmod 644 "${AUTOSTART_DIR}/easymic-notify.desktop"

install -Dm644 "${ROOT}/README.md" "${DOC_DIR}/README.md" 2>/dev/null || true

info "Räume alte MicMonitor-Reste auf ..."
pkill -f 'micmon( |$)' >/dev/null 2>&1 || true
for module in $(pactl list short modules 2>/dev/null | awk '
    ($2 == "module-null-sink" && $0 ~ /sink_name=micmon_virtual/) ||
    ($2 == "module-remap-source" && $0 ~ /source_name=micmon_virtual_mic/) ||
    ($2 == "module-loopback" && $0 ~ /source=micmon_virtual[.]monitor/) { print $1 }'); do
    pactl unload-module "${module}" >/dev/null 2>&1 || true
done

info "Stoppe eine eventuell laufende Instanz ..."
"${BIN_DIR}/easymic" daemon stop >/dev/null 2>&1 || true

info "Aktiviere easymicd.service ..."
if command -v systemctl >/dev/null 2>&1; then
    systemctl --user daemon-reload >/dev/null 2>&1 || true
    if systemctl --user enable --now easymicd.service >/dev/null 2>&1; then
        sleep 1
        if systemctl --user is-active --quiet easymicd.service; then
            info "Daemon läuft."
        else
            warn "Der Service ist aktiviert, läuft aber noch nicht. Prüfe: systemctl --user status easymicd"
        fi
    else
        warn "systemd-User-Service konnte nicht gestartet werden (Session evtl. degraded)."
        warn "Starte den Daemon manuell mit: ${BIN_DIR}/easymic daemon start"
    fi
else
    warn "systemctl wurde nicht gefunden; starte den Daemon manuell: ${BIN_DIR}/easymic daemon start"
fi

info "Fertig."
cat <<EOF

EasyMic ist installiert.

  Oberfläche:   ${BIN_DIR}/easymic
  Status:       ${BIN_DIR}/easymic status
  Daemon:       ${BIN_DIR}/easymic daemon status
  Einstellungen: ${CONFIG_DIR}/easymic/settings.json
  Autostart:    ${AUTOSTART_DIR}/easymic-notify.desktop

Falls ${BIN_DIR} noch nicht im PATH liegt, füge es in deiner Shell-Konfiguration hinzu.
EOF
