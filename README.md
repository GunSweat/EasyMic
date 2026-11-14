# EasyMic

[![Build](https://github.com/GunSweat/EasyMic/actions/workflows/build.yml/badge.svg)](https://github.com/GunSweat/EasyMic/actions/workflows/build.yml)

EasyMic ist ein Mikrofon-Effektprozessor für Linux mit PipeWire/PulseAudio.
Ein kleiner Daemon verarbeitet das Eingangssignal, stellt ein virtuelles
Mikrofon bereit und speichert alle Einstellungen. Die Terminal-Oberfläche
`easymic` steuert den Daemon und kann jederzeit geschlossen werden, ohne die
Audioverarbeitung zu beenden.

## Funktionen

- **Noise-Gate** mit Schwelle, Attack, Release, Hold und Dämpfung
- **Spektrale Denoise** mit Rauschboden, Glättung, Reduktion und Mix
- **Pitch-Shift** ±1 Oktave (SoundTouch, Laufzeit bleibt unverändert)
- **Volume** 0–200 %
- **Ton/Tilt** ±12 dB (Tiefen/Höhen gegeneinander)
- **Ausgangs-Limiter** mit einstellbarem Ceiling
- **Virtuelles Mikrofon** `EasyMic-Virtual-Mic` für alle Anwendungen
- **Monitoring** auf die Standardausgabe, standardmäßig aus
- **Automatisches Speichern** in `~/.config/easymic/settings.json`
- **Autostart** des Daemons per systemd-User-Service
- **Login-Benachrichtigung** „Open EasyMic“; ein Klick öffnet die Oberfläche

Alle Effekte wirken sowohl auf das virtuelle Mikrofon als auch auf das
Monitoring. Das Monitoring wird direkt aus dem virtuellen Ausgang abgegriffen,
damit beide Wege identisch klingen.

## Voraussetzungen

- Linux mit PipeWire (oder PulseAudio) und `pactl`, `parec`, `pacat`
- .NET 10 SDK (zum Bauen) bzw. die .NET-10-Laufzeit (zum Ausführen)
- `notify-send` aus libnotify für die Login-Benachrichtigung
- Ein Terminal wie Konsole, Alacritty, Kitty oder Foot für den Klick auf die
  Benachrichtigung

Auf Arch/Derivaten:

```bash
sudo pacman -S dotnet-sdk pipewire pipewire-pulse libnotify konsole
```

Auf Debian/Ubuntu:

```bash
sudo apt install dotnet-sdk-10.0 pipewire pipewire-pulse libnotify-bin pulseaudio-utils konsole
```

## Installation

```bash
cd Util/MicNoiseRemover
./install.sh
```

Das Skript:

1. baut `easymicd` und `easymic` als Single-File-Binaries,
2. installiert sie nach `~/.local/bin`,
3. installiert `easymicd.service` nach `~/.config/systemd/user`,
4. installiert den XDG-Autostart-Eintrag nach `~/.config/autostart`,
5. entfernt alte `micmon_*`-Module und -Prozesse,
6. aktiviert und startet den Daemon.

Danach `~/.local/bin` in den `PATH` aufnehmen, falls noch nicht geschehen.

### Manuell starten

```bash
easymic              # Oberfläche
easymic daemon start # nur den Daemon
```

Die CLI startet den Daemon bei Bedarf selbst, falls er noch nicht läuft.

### Distributionspakete

Bei jedem Release werden fertige Pakete erzeugt:

| System | Datei | Installation |
|--------|-------|--------------|
| Debian/Ubuntu | `easymic_<version>_amd64.deb` | `sudo apt install ./easymic_<version>_amd64.deb` |
| Fedora/RHEL | `easymic-<version>-1.x86_64.rpm` | `sudo dnf install --nogpgcheck ./easymic-<version>-1.x86_64.rpm` |
| openSUSE | `easymic-<version>-1.x86_64.rpm` | `sudo zypper --allow-unsigned-rpm install ./easymic-<version>-1.x86_64.rpm` |
| Arch/Manjaro | `easymic-<version>-1-x86_64.pkg.tar.zst` | `sudo pacman -U easymic-<version>-1-x86_64.pkg.tar.zst` |
| beliebiges Linux | `easymic-<version>-linux-x64.tar.gz` | entpacken und Binaries/Unit manuell installieren |
| Windows | `easymic-<version>-windows-x64.zip` | Kompilier-Build, siehe `README-Windows.txt` |

Nach der Paketinstallation einmalig:

```bash
systemctl --user daemon-reload
systemctl --user enable --now easymicd
```

Beim nächsten Login startet die „Open EasyMic“-Benachrichtigung den Daemon
ebenfalls automatisch.

## Bedienung

Die Oberfläche zeigt acht Hauptregler, jeweils eine Zeile mit Mittelmarkierung:

| Regler      | Bereich        | Bedeutung                              |
|-------------|----------------|----------------------------------------|
| `PITCH`     | −1 … +1 Okt.   | Tonhöhe                                |
| `VOLUME`    | 0 … 200 %      | Verstärkung                            |
| `TON`       | −12 … +12 dB   | Tilt: Höhen gegen Tiefen               |
| `GATE`      | 0 … 100 %      | Stärke des Noise-Gates                 |
| `SCHWELLE`  | −80 … −10 dB   | Gate-Schwelle                          |
| `DENOISE`   | 0 … 100 %      | Stärke der spektralen Denoise          |
| `REDUKTION` | 6 … 40 dB      | maximale Denoise-Absenkung             |
| `MIX`       | 0 … 100 %      | Dry/Wet Roh → Denoise (0 % = Denoise aus) |

Darunter liegt die Box **Noise-Gate & Denoise** mit getrennten Schaltern für
Gate und Denoise, den Gate-Details (Attack, Release, Hold, Dämpfung), den
Denoise-Details (Rauschboden, Glättung) und dem Ausgangs-Limiter. Schwelle,
Reduktion und Mix erscheinen nur als Hauptregler, nicht doppelt in der Box.
Der Rauschboden ist der Mindestpegel, den die adaptive Rauschschätzung
annimmt; die Schätzung folgt dem tatsächlichen Hintergrund über ca. 2 s.

### Tasten und Maus

| Eingabe                    | Aktion                                      |
|----------------------------|---------------------------------------------|
| `↑` / `↓`                  | Regler oder Schalter auswählen              |
| `←` / `→`                  | Wert ändern, Schalter aus/an                |
| `Shift` + `←` / `→`        | große Schritte                              |
| `Enter`                    | Schalter umlegen                            |
| `M` oder `Leertaste`       | Monitoring ein/aus                          |
| `PageUp` / `PageDown`      | scrollen                                    |
| Mausrad                    | scrollen                                    |
| Klick auf einen Regler     | Wert direkt an die Klickposition setzen     |
| Ziehen auf einem Regler    | Wert stufenlos ändern                       |
| Klick auf `[● Gate AN]`, `[○ Denoise AUS]`, `[● Limiter AN]` | Schalter umlegen |
| Klick auf einen Detailregler | Detailwert direkt setzen                  |
| Klick unten links          | Monitoring ein/aus                          |
| Klick auf `Q Beenden`      | Oberfläche schließen (Daemon läuft weiter)  |
| `R`                        | Regler auf Standardwerte                    |
| `Q` / `Esc`                | Oberfläche schließen (Daemon läuft weiter)  |

Alle Bedienelemente sind mit der Maus erreichbar: Regler per Klick oder
Ziehen, Schalter per Klick, Scrollen per Mausrad, Beenden über `Q Beenden`
unten rechts.

Bei geringer Fensterhöhe scrollt der Mittelteil; Kopf- und Fußzeile bleiben
sichtbar. Der Monitor-Schalter sitzt fest unten links.

### Standardwerte

- Gate: 30 %
- Denoise: 50 %
- Noise-Mix: 100 % (0 % = Denoise aus)
- Limiter: an
- Monitoring: aus

## Kommandozeile

```bash
easymic status [--json]
easymic monitor [on|off]        # ohne Argument: umschalten
easymic set <name> <wert>
easymic get [name] [--json]
easymic devices [source|sink]
easymic source <name>
easymic sink <name>
easymic latency <ms>
easymic reset
easymic daemon <start|stop|restart|status>
easymic notify
```

Beispiele:

```bash
easymic set volume 120        # 120 %
easymic set gate 45%          # 45 %
easymic set ton -6            # −6 dB Tilt
easymic set schwelle -48      # −48 dBFS
easymic set limiter-on off
easymic status --json
```

Unterstützte Namen: `pitch`, `volume`, `ton`, `gate`, `schwelle`, `denoise`,
`reduktion`, `mix`, `gate-attack`, `gate-release`, `gate-hold`,
`gate-damping`, `denoise-floor`, `denoise-smoothing`, `gate-on`,
`denoise-on`, `limiter-on`.

## Virtuelles Mikrofon verwenden

In der Anwendung deiner Wahl als Eingang **EasyMic-Virtual-Mic** auswählen.
Alternativ als Standardquelle setzen:

```bash
pactl set-default-source easymic_virtual_mic
```

Das Modul wird beim Beenden des Daemons entfernt. Nach einem Absturz räumt der
nächste Start verwaiste `easymic_*`- und alte `micmon_*`-Module auf.

## Dateien und Pfade

| Pfad                                             | Inhalt                        |
|--------------------------------------------------|-------------------------------|
| `~/.config/easymic/settings.json`                | Einstellungen                 |
| `~/.local/state/easymic/daemon.log`              | Log beim manuellen Start      |
| `$XDG_RUNTIME_DIR/easymic.sock`                  | IPC-Socket                    |
| `$XDG_RUNTIME_DIR/easymic.lock`                  | Daemon-Sperre                 |
| `/tmp/easymic-$USER.lock`                        | Sperre des virtuellen Mikrofons |
| `~/.config/systemd/user/easymicd.service`        | systemd-User-Service          |
| `~/.config/autostart/easymic-notify.desktop`     | Login-Benachrichtigung        |

Einstellungen werden 400 ms nach der letzten Änderung atomar gespeichert
(`settings.json.tmp` → `settings.json`).

## Architektur

```
EasyMic.Core      Audio, DSP, Geräte, Einstellungen, IPC, DaemonHost
EasyMic.Daemon    easymicd – besitzt Audio, virtuelles Mikrofon, IPC-Server
EasyMic.Cli       easymic  – TUI und Steuer-CLI, startet den Daemon bei Bedarf
```

DSP-Reihenfolge:

```
Spektrale Denoise → Dry/Wet-Mix → Gate → Ton → Volume → Pitch → Limiter
```

Die Denoise arbeitet mit einer eigenen Radix-2-FFT (1024 Samples, Hop 256,
Hann-Fenster, Overlap-Add) und verursacht ca. 21 ms Latenz. Die Rauschschätzung
nutzt Minimum-Statistik über ca. 2 Sekunden, sodass auch während des Sprechens
der Hintergrund unterdrückt wird. Zusammen mit SoundTouch bleibt die gesamte
Kette im Bereich von etwa 50 ms.

Der IPC-Kanal ist ein Unix-Socket mit zeilenweisem JSON. Die TUI abonniert
einen Statusstrom mit ca. 30 Bildern pro Sekunde und zeigt Pegel, Geräte,
Monitoring und alle Regler live an.

## Service

```bash
systemctl --user status easymicd
systemctl --user restart easymicd
journalctl --user -u easymicd -f
```

Der Service startet beim Login automatisch (`WantedBy=default.target`). Die
TUI wird **nicht** automatisch gestartet; dafür ist die Benachrichtigung da.

## Fehlerbehebung

**Kein virtuelles Mikrofon:** Prüfe, ob `pactl load-module module-null-sink`
funktioniert und ob `easymicd` läuft. Rechte Module mit
`pactl list short modules | grep easymic` anzeigen.

**Kein Ton beim Monitoring:** Monitoring ist nach dem Login absichtlich aus.
Mit `M`, dem Schalter unten links oder `easymic monitor on` einschalten.

**Daemon startet nicht:** `easymic daemon start`, danach
`journalctl --user -u easymicd -n 50` bzw. `~/.local/state/easymic/daemon.log`
prüfen.

**Benachrichtigung erscheint nicht:** `notify-send` installieren und den
Autostart-Eintrag prüfen. Manuell testen mit `easymic notify`.

**Alte MicMonitor-Module:** `install.sh` entfernt sie. Manuell:
`pactl list short modules | grep micmon` und die IDs mit
`pactl unload-module <id>` entladen.

## Entwicklung

```bash
dotnet build EasyMic.slnx
dotnet run --project src/EasyMic.Daemon
dotnet run --project src/EasyMic.Cli
```

Die Daemon- und CLI-Binaries heißen im Build `easymicd` und `easymic`.

GitHub Actions baut bei jedem Push Linux **und** Windows. Der Windows-Build ist
derzeit nur eine Kompilierung – die Laufzeit benötigt Linux mit
PipeWire/PulseAudio. Lokal lässt sich der Windows-Build so erzeugen:

```bash
dotnet publish src/EasyMic.Cli/EasyMic.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

## Release erstellen

```bash
./release.sh 1.2.3
```

Das Skript legt den Tag `v1.2.3` an und pusht ihn. Der Release-Workflow baut
dann `.deb`, `.rpm`, das Arch-Paket, den Linux-Tarball und die Windows-ZIP und
lädt alles an das GitHub-Release. Alternativ lässt sich der Workflow manuell
mit einer Version starten (Actions → Release → Run workflow).

## Lizenz

MIT – siehe [LICENSE](LICENSE).
