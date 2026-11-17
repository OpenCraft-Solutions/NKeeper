# A minimal VNC server for integration tests: TigerVNC's Xvnc, password protected (VNC authentication), with an
# xterm on its display. 1.x used x11vnc, whose libvncserver now drops guacd's connection as a failed WebSocket.
FROM alpine:3.22
RUN apk add --no-cache tigervnc xterm
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
