# A minimal xrdp server for integration tests: TLS with a fresh self-signed certificate, no NLA
# (xrdp doesn't support it), and an xterm session for user rdpuser.
FROM alpine:3.22
RUN apk add --no-cache xrdp xorgxrdp xorg-server xterm dbus openssl \
 && adduser -D -s /bin/sh rdpuser \
 && echo 'rdpuser:Rdp-Test-Passw0rd' | chpasswd \
 && printf '#!/bin/sh\nexec xterm\n' > /etc/xrdp/startwm.sh && chmod +x /etc/xrdp/startwm.sh \
 && xrdp-keygen xrdp auto
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
