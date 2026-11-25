/** A terminal recording (docs/architecture.md §7.51): asciicast v2, as 1.x and asciinema write it. */
export interface Cast {
  width: number
  height: number
  events: CastEvent[]
}

/** Output (`o`, its text) or a resize (`r`, its size), at seconds from the start. */
export interface CastEvent {
  seconds: number
  kind: 'o' | 'r'
  text: string
  columns?: number
  rows?: number
}

/** An asciicast's header and its output and resize events, in order; null when it isn't one. Other events are skipped. */
export function parseCast(text: string): Cast | null {
  const lines = text.split('\n').filter((l) => l.trim() !== '')
  let header: { version?: unknown; width?: unknown; height?: unknown }
  try {
    header = JSON.parse(lines[0] ?? '') as typeof header
  } catch {
    return null
  }
  if (header.version !== 2 || typeof header.width !== 'number' || typeof header.height !== 'number') {
    return null
  }
  const events: CastEvent[] = []
  for (const line of lines.slice(1)) {
    let event: unknown
    try {
      event = JSON.parse(line)
    } catch {
      continue
    }
    if (!Array.isArray(event) || typeof event[0] !== 'number' || typeof event[2] !== 'string') {
      continue
    }
    const [seconds, kind, data] = event as [number, string, string]
    if (kind === 'o') {
      events.push({ seconds, kind, text: data })
    } else if (kind === 'r') {
      const size = /^(\d+)x(\d+)$/.exec(data)
      if (size) {
        events.push({ seconds, kind, text: data, columns: Number(size[1]), rows: Number(size[2]) })
      }
    }
  }
  return { width: header.width, height: header.height, events }
}
