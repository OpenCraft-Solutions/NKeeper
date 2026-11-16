import Guacamole from './vendor/guacamole-common-js/guacamole.js'

type GuacamoleStatus = InstanceType<typeof Guacamole.Status>

/** Friendly text for guacd's status codes (Guacamole.Status.Code). Ported from 1.x. */
export function describeDesktopStatus(status: GuacamoleStatus, protocol: 'rdp' | 'vnc'): string {
  const server = protocol === 'rdp' ? 'Remote Desktop server' : 'VNC server'
  const detail = status.message ? ` (${status.message})` : ''
  switch (status.code) {
    case 0x0301:
    case 0x0303:
      return protocol === 'rdp' ? 'Sign-in failed. Check the username, password and domain.' : 'Sign-in failed. Check the VNC password.'
    case 0x0207:
      return /certificate|SSL|TLS/i.test(status.message ?? '')
        ? 'The server’s certificate didn’t match the trusted one, so the connection was refused.'
        : `Couldn’t connect to the ${server}${detail}.`
    case 0x0208:
      return `The ${server} is unavailable or refused the connection${detail}.`
    case 0x0202:
      return `The ${server} stopped responding.`
    case 0x0209:
      return 'Disconnected because this account signed in from somewhere else.'
    case 0x020a:
      return 'Disconnected after a period of inactivity.'
    case 0x020b:
      return 'The session was closed on the server, e.g. by an administrator.'
    case 0x0201:
      return `The ${server} is too busy right now.`
    default:
      return status.message || 'The session ended unexpectedly.'
  }
}
