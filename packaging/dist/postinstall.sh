#!/bin/sh
set -e

cat <<'EOF'
EasyMic installed.

Start the user daemon manually with:
  systemctl --user daemon-reload
  systemctl --user enable --now easymicd

At the next login the "Open EasyMic" notification starts the daemon
automatically. Configure the virtual microphone in your applications as
"EasyMic-Virtual-Mic".
EOF

exit 0
