EasyMic for Windows (compile-only build)
========================================

This ZIP contains easymic.exe as a standalone Windows application.

IMPORTANT: This is currently only a compile-only build. EasyMic uses
PipeWire/PulseAudio and Linux-specific interfaces for audio (pactl, parec,
pacat, libc, Unix sockets). The Windows executable starts, but it cannot
process a microphone yet. A complete Windows port (WASAPI + virtual audio
device) is not included.

The Linux build is the functional version.
