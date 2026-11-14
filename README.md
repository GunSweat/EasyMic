# EasyMic

[![Build](https://github.com/GunSweat/EasyMic/actions/workflows/build.yml/badge.svg)](https://github.com/GunSweat/EasyMic/actions/workflows/build.yml)

EasyMic is a microphone effect processor for Linux with PipeWire/PulseAudio.
A small daemon processes the input signal, provides a virtual microphone and
saves all settings. The terminal UI `easymic` controls the daemon and can be
closed at any time without stopping audio processing.

## Features

- **Noise gate** with threshold, attack, release, hold and damping
- **Spectral denoise** with noise floor, smoothing, reduction and mix
- **Pitch shift** ±1 octave (SoundTouch, runtime length unchanged)
- **Volume** 0–200 %
- **Tone/tilt** ±12 dB (lows versus highs)
- **Output limiter** with adjustable ceiling
- **Virtual microphone** `EasyMic-Virtual-Mic` for all applications
- **Monitoring** to the default output, disabled by default
- **Automatic saving** to `~/.config/easymic/settings.json`
- **Autostart** of the daemon via a systemd user service
- **Login notification** “Open EasyMic”; one click opens the UI

All effects apply both to the virtual microphone and to monitoring. Monitoring
is taken directly from the virtual microphone so both paths sound identical.

## Requirements

- Linux with PipeWire (or PulseAudio) and `pactl`, `parec`, `pacat`
- .NET 10 SDK (to build) or the .NET 10 runtime (to run)
- `notify-send` from libnotify for the login notification
- A terminal such as Konsole, Alacritty, Kitty or Foot for the notification click

On Arch and derivatives:

```bash
sudo pacman -S dotnet-sdk pipewire pipewire-pulse libnotify konsole
```

On Debian/Ubuntu:

```bash
sudo apt install dotnet-sdk-10.0 pipewire pipewire-pulse libnotify-bin pulseaudio-utils konsole
```

## Installation

```bash
cd Util/MicNoiseRemover
./install.sh
```

The script:

1. builds `easymicd` and `easymic` as single-file binaries,
2. installs them into `~/.local/bin`,
3. installs `easymicd.service` into `~/.config/systemd/user`,
4. installs the XDG autostart entry into `~/.config/autostart`,
5. removes old `micmon_*` modules and processes,
6. enables and starts the daemon.

Afterwards add `~/.local/bin` to your `PATH` if it is not there yet.

### Start manually

```bash
easymic              # UI
easymic daemon start # daemon only
```

The CLI starts the daemon on demand if it is not running yet.

### Distribution packages

Every release produces ready-to-install packages:

| System | File | Installation |
|--------|------|--------------|
| Debian/Ubuntu | `easymic_<version>_amd64.deb` | `sudo apt install ./easymic_<version>_amd64.deb` |
| Fedora/RHEL | `easymic-<version>-1.x86_64.rpm` | `sudo dnf install --nogpgcheck ./easymic-<version>-1.x86_64.rpm` |
| openSUSE | `easymic-<version>-1.x86_64.rpm` | `sudo zypper --allow-unsigned-rpm install ./easymic-<version>-1.x86_64.rpm` |
| Arch/Manjaro | `easymic-<version>-1-x86_64.pkg.tar.zst` | `sudo pacman -U easymic-<version>-1-x86_64.pkg.tar.zst` |
| Any Linux | `easymic-<version>-linux-x64.tar.gz` | extract and install binaries/unit manually |
| Windows | `easymic-<version>-windows-x64.zip` | compile-only build, see `README-Windows.txt` |

After installing a package, run this once:

```bash
systemctl --user daemon-reload
systemctl --user enable --now easymicd
```

At the next login the “Open EasyMic” notification also starts the daemon
automatically.

## Controls

The UI shows eight main sliders, one per line with a center marker:

| Control | Range | Meaning |
|---------|-------|---------|
| `PITCH` | −1 … +1 oct. | Pitch |
| `VOLUME` | 0 … 200 % | Gain |
| `TONE` | −12 … +12 dB | Tilt: highs versus lows |
| `GATE` | 0 … 100 % | Noise gate strength |
| `THRESHOLD` | −80 … −10 dB | Gate threshold |
| `DENOISE` | 0 … 100 % | Spectral denoise strength |
| `REDUCTION` | 6 … 40 dB | Maximum denoise attenuation |
| `MIX` | 0 … 100 % | Dry/wet raw → denoise (0 % = denoise off) |

Below that is the **Noise Gate & Denoise** box with separate switches for gate
and denoise, the gate details (Attack, Release, Hold, Damping), the denoise
details (Noise Floor, Smoothing) and the output limiter. Threshold, reduction
and mix only appear as main sliders, not duplicated in the box. The noise floor
is the minimum level assumed by the adaptive noise estimator; the estimate
follows the actual background over about 2 seconds.

### Keyboard and mouse

| Input | Action |
|-------|--------|
| `↑` / `↓` | Select a slider or switch |
| `←` / `→` | Change a value, toggle a switch |
| `Shift` + `←` / `→` | Large steps |
| `Enter` | Toggle a switch |
| `M` or `Space` | Monitoring on/off |
| `PageUp` / `PageDown` | Scroll |
| Mouse wheel | Scroll |
| Click a slider | Set the value to the click position |
| Drag a slider | Change the value continuously |
| Click `[● Gate ON]`, `[○ Denoise OFF]`, `[● Limiter ON]` | Toggle a switch |
| Click a detail slider | Set the detail value directly |
| Click bottom left | Monitoring on/off |
| Click `Q Quit` | Close the UI (daemon keeps running) |
| `R` | Reset controls to defaults |
| `Q` / `Esc` | Close the UI (daemon keeps running) |

Everything is reachable with the mouse: sliders by click or drag, switches by
click, scrolling with the wheel, and quit via `Q Quit` in the bottom right.

At low window heights the middle section scrolls; header and footer stay
visible. The monitor switch is fixed at the bottom left.

### Defaults

- Gate: 30 %
- Denoise: 50 %
- Noise mix: 100 % (0 % = denoise off)
- Limiter: on
- Monitoring: off

## Command line

```bash
easymic status [--json]
easymic monitor [on|off]        # no argument: toggle
easymic set <name> <value>
easymic get [name] [--json]
easymic devices [source|sink]
easymic source <name>
easymic sink <name>
easymic latency <ms>
easymic reset
easymic daemon <start|stop|restart|status>
easymic notify
```

Examples:

```bash
easymic set volume 120        # 120 %
easymic set gate 45%          # 45 %
easymic set tone -6           # −6 dB tilt
easymic set threshold -48     # −48 dBFS
easymic set limiter-on off
easymic status --json
```

Supported names: `pitch`, `volume`, `tone`, `gate`, `threshold`, `denoise`,
`reduction`, `mix`, `gate-attack`, `gate-release`, `gate-hold`, `gate-damping`,
`denoise-floor`, `denoise-smoothing`, `gate-on`, `denoise-on`, `limiter-on`.

## Using the virtual microphone

Select **EasyMic-Virtual-Mic** as the input in the application of your choice.
Alternatively, set it as the default source:

```bash
pactl set-default-source easymic_virtual_mic
```

Internally the virtual microphone is a pipe source (`module-pipe-source`) with
a FIFO at `$XDG_RUNTIME_DIR/easymic-$USER.mic`. It therefore does not appear as
a playback stream and screen-sharing tools cannot accidentally send it as
application audio.

The module is removed when the daemon stops. After a crash, the next start
cleans up orphaned `easymic_*` and old `micmon_*` modules.

## Files and paths

| Path | Content |
|------|---------|
| `~/.config/easymic/settings.json` | Settings |
| `~/.local/state/easymic/daemon.log` | Log for manual starts |
| `$XDG_RUNTIME_DIR/easymic.sock` | IPC socket |
| `$XDG_RUNTIME_DIR/easymic-$USER.mic` | Virtual microphone FIFO |
| `$XDG_RUNTIME_DIR/easymic.lock` | Daemon lock |
| `/tmp/easymic-$USER.lock` | Virtual microphone lock |
| `~/.config/systemd/user/easymicd.service` | systemd user service |
| `~/.config/autostart/easymic-notify.desktop` | Login notification |

Settings are saved atomically 400 ms after the last change
(`settings.json.tmp` → `settings.json`).

## Architecture

```
EasyMic.Core      Audio, DSP, devices, settings, IPC, DaemonHost
EasyMic.Daemon    easymicd – owns audio, virtual microphone, IPC server
EasyMic.Cli       easymic  – TUI and control CLI, starts the daemon on demand
```

DSP order:

```
Spectral denoise → Dry/wet mix → Gate → Tone → Volume → Pitch → Limiter
```

The denoiser uses its own radix-2 FFT (1024 samples, hop 256, Hann window,
overlap-add) and adds about 21 ms of latency. Noise estimation uses minimum
statistics over roughly 2 seconds, so the background is suppressed even while
speaking. Together with SoundTouch the complete chain stays around 50 ms.

The IPC channel is a Unix socket with line-delimited JSON. The TUI subscribes
to a status stream at roughly 30 frames per second and shows levels, devices,
monitoring and all controls live.

## Service

```bash
systemctl --user status easymicd
systemctl --user restart easymicd
journalctl --user -u easymicd -f
```

The service starts automatically at login (`WantedBy=default.target`). The TUI
is **not** started automatically; the notification is used for that.

## Troubleshooting

**No virtual microphone:** Check that `pactl load-module module-pipe-source`
works and that `easymicd` is running. Show the modules with
`pactl list short modules | grep easymic`. The pipe is located at
`$XDG_RUNTIME_DIR/easymic-$USER.mic`.

**No sound while monitoring:** Monitoring is intentionally off after login.
Turn it on with `M`, the switch in the bottom left or `easymic monitor on`.

**Double voice in screen shares:** The virtual microphone is a pipe source and
is not captured as application audio. During screen sharing, keep monitoring
off, otherwise the monitor loopback can be captured as an additional playback
stream.

**Daemon does not start:** Run `easymic daemon start`, then check
`journalctl --user -u easymicd -n 50` or `~/.local/state/easymic/daemon.log`.

**Notification does not appear:** Install `notify-send` and check the autostart
entry. Test manually with `easymic notify`.

**Old MicMonitor modules:** `install.sh` removes them. Manually:
`pactl list short modules | grep micmon` and unload the IDs with
`pactl unload-module <id>`.

## Development

```bash
dotnet build EasyMic.slnx
dotnet run --project src/EasyMic.Daemon
dotnet run --project src/EasyMic.Cli
```

The daemon and CLI binaries are named `easymicd` and `easymic` in the build.

GitHub Actions builds Linux **and** Windows on every push. The Windows build is
currently compile-only – the runtime requires Linux with PipeWire/PulseAudio.
You can create the Windows build locally like this:

```bash
dotnet publish src/EasyMic.Cli/EasyMic.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

## Creating a release

```bash
./release.sh 1.2.3
```

The script creates and pushes the tag `v1.2.3`. The release workflow then builds
the `.deb`, `.rpm`, Arch package, Linux tarball and Windows ZIP and uploads
everything to the GitHub release. Alternatively, run the workflow manually with
a version (Actions → Release → Run workflow).

## License

MIT – see [LICENSE](LICENSE).
