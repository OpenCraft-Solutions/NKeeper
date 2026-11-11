#!/bin/sh
set -e
# Listen on the port the test publishes, so the same address works inside and outside Docker.
sed -i "s/^port=3389$/port=${XRDP_PORT:-3389}/" /etc/xrdp/xrdp.ini
openssl req -x509 -newkey rsa:2048 -nodes -keyout /etc/xrdp/key.pem -out /etc/xrdp/cert.pem -days 30 -subj /CN=nkeeper-xrdp-test 2>/dev/null
xrdp-sesman
exec xrdp --nodaemon
