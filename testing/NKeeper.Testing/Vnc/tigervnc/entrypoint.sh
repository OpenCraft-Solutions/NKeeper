#!/bin/sh
set -e
# Listen on the port the test publishes, so the same address works inside and outside Docker.
mkdir -p /root/.vnc
echo "${VNC_PASSWORD:?}" | vncpasswd -f > /root/.vnc/passwd
chmod 600 /root/.vnc/passwd
Xvnc :1 -geometry 1024x768 -depth 24 -rfbport "${VNC_PORT:-5900}" -SecurityTypes VncAuth -PasswordFile /root/.vnc/passwd -AlwaysShared &
sleep 1
DISPLAY=:1 exec xterm
