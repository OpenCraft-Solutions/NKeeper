import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError, mirrorInjectedStyles } from '@nkeeper/sdk'
import { Terminal } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'
import { parseCast, type Cast } from './recordings'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import { Dialog } from './ui/Dialog'
import styles from './ui/Reports.module.css'
import { Spinner } from './ui/Spinner'

/** `GET /api/audit/recordings`: a terminal session's recording (docs/architecture.md §7.51). */
export interface Recording {
  id: string
  sessionId: string
  userName: string
  hostId: string | null
  hostName: string
  sessionType: string
  startedAtUtc: string
  endedAtUtc: string
  size: number
  truncated: boolean
}

const PAGE = 50
const SPEEDS = [1, 2, 4, 8]
/** Pauses longer than this are shortened when playing, as asciinema's idle limit does. */
const IDLE_LIMIT_SECONDS = 2

const time = (iso: string) => new Date(iso).toLocaleString()
const duration = (seconds: number) => (seconds < 60 ? `${Math.round(seconds)} s` : `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`)

/**
 * 1.x's terminal recordings, on the audit log page: what each SSH or Telnet session showed, newest first, played back
 * in a terminal. Watching one is audited.
 */
export function RecordingsView({ onShowHost }: { onShowHost: (hostId: string) => void }) {
  const [recordings, setRecordings] = useState<Recording[] | null>(null)
  const [more, setMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [playing, setPlaying] = useState<Recording | null>(null)

  const load = useCallback(async (before?: string) => {
    try {
      const page = await api.get<Recording[]>(`/api/audit/recordings?limit=${PAGE}${before ? `&before=${encodeURIComponent(before)}` : ''}`)
      setRecordings((current) => (before ? [...(current ?? []), ...page] : page))
      setMore(page.length === PAGE)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'The recordings couldn’t be loaded.')
    }
  }, [])

  useEffect(() => {
    queueMicrotask(() => void load())
  }, [load])

  if (error && !recordings) {
    return <Alert>{error}</Alert>
  }
  if (!recordings) {
    return <Spinner label="Loading recordings" />
  }
  return (
    <section aria-label="Recordings">
      {recordings.length === 0 ? (
        <p className={styles.state}>No terminal sessions were recorded yet.</p>
      ) : (
        <div className={styles.tableScroll}>
          <table className={styles.healthTable} aria-label="Recordings">
            <thead>
              <tr>
                <th scope="col">Started</th>
                <th scope="col">User</th>
                <th scope="col">Host</th>
                <th scope="col">Session</th>
                <th scope="col">Length</th>
                <th scope="col" />
              </tr>
            </thead>
            <tbody>
              {recordings.map((r) => (
                <tr key={r.id}>
                  <td>{time(r.startedAtUtc)}</td>
                  <td>{r.userName}</td>
                  <td>
                    {r.hostId ? (
                      <button type="button" className={styles.hostLink} onClick={() => onShowHost(r.hostId!)}>
                        {r.hostName}
                      </button>
                    ) : (
                      r.hostName
                    )}
                  </td>
                  <td>{r.sessionType}</td>
                  <td>
                    {duration((Date.parse(r.endedAtUtc) - Date.parse(r.startedAtUtc)) / 1000)}
                    {r.truncated ? <span className={styles.rowNote}> · cut short</span> : null}
                  </td>
                  <td>
                    <Button size="sm" variant="secondary" onClick={() => setPlaying(r)} aria-label={`Play ${r.userName}’s session on ${r.hostName}`}>
                      Play
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {more ? (
        <Button size="sm" variant="secondary" onClick={() => void load(recordings.at(-1)!.startedAtUtc)}>
          Older recordings
        </Button>
      ) : null}
      {playing ? <Player recording={playing} onClose={() => setPlaying(null)} /> : null}
    </section>
  )
}

/** Plays a recording in a read-only terminal: play, pause, restart and speed, with long pauses shortened. */
function Player({ recording, onClose }: { recording: Recording; onClose: () => void }) {
  const host = useRef<HTMLDivElement>(null)
  const terminal = useRef<Terminal | null>(null)
  const timer = useRef<number | null>(null)
  const position = useRef(0)
  const [cast, setCast] = useState<Cast | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [paused, setPaused] = useState(false)
  const [speed, setSpeed] = useState(1)
  const [at, setAt] = useState(0)
  // Each restart plays again from the start.
  const [run, setRun] = useState(0)

  useEffect(() => {
    let cancelled = false
    api.get<Blob>(`/api/audit/recordings/${recording.id}/cast`, { responseType: 'blob' }).then(
      async (blob) => {
        const parsed = parseCast(await blob.text())
        if (!cancelled) {
          if (parsed) {
            setCast(parsed)
          } else {
            setError('This recording can’t be read.')
          }
        }
      },
      (e: unknown) => !cancelled && setError(e instanceof ApiError ? e.message : 'The recording couldn’t be loaded.'),
    )
    return () => {
      cancelled = true
    }
  }, [recording.id])

  useEffect(() => {
    if (!cast || !host.current) {
      return
    }
    // xterm's renderer injects <style> elements, which the shell's policy blocks: they're mirrored as adopted sheets (§7.14).
    const stopMirroring = mirrorInjectedStyles(host.current)
    const term = new Terminal({ cols: cast.width, rows: cast.height, disableStdin: true, convertEol: false, scrollback: 5000 })
    term.open(host.current)
    terminal.current = term
    position.current = 0
    return () => {
      stopMirroring()
      term.dispose()
      terminal.current = null
    }
  }, [cast])

  const stop = () => {
    if (timer.current !== null) {
      window.clearTimeout(timer.current)
      timer.current = null
    }
  }

  // Plays from the current event onwards, at the chosen speed, until paused.
  useEffect(() => {
    if (!cast || paused) {
      return
    }
    const step = () => {
      const term = terminal.current
      const event = cast.events[position.current]
      if (!term || !event) {
        timer.current = null
        return
      }
      if (event.kind === 'o') {
        term.write(event.text)
      } else if (event.kind === 'r' && event.columns && event.rows) {
        term.resize(event.columns, event.rows)
      }
      setAt(event.seconds)
      position.current++
      const next = cast.events[position.current]
      if (next) {
        timer.current = window.setTimeout(step, (Math.min(next.seconds - event.seconds, IDLE_LIMIT_SECONDS) * 1000) / speed)
      }
    }
    timer.current = window.setTimeout(step, 0)
    return stop
  }, [cast, paused, speed, run])

  function restart() {
    stop()
    terminal.current?.reset()
    if (cast) {
      terminal.current?.resize(cast.width, cast.height)
    }
    position.current = 0
    setAt(0)
    setPaused(false)
    setRun((n) => n + 1)
  }

  const total = cast?.events.at(-1)?.seconds ?? 0
  return (
    <Dialog
      title={`${recording.userName} on ${recording.hostName}`}
      description={`${recording.sessionType} · ${time(recording.startedAtUtc)}${recording.truncated ? ' · cut short at the size limit' : ''}`}
      onClose={onClose}
      size="lg"
      footer={
        <>
          <label>
            Speed{' '}
            <select value={speed} onChange={(e) => setSpeed(Number(e.target.value))} aria-label="Speed">
              {SPEEDS.map((s) => (
                <option key={s} value={s}>
                  {s}×
                </option>
              ))}
            </select>
          </label>
          <span role="status">
            {duration(at)} of {duration(total)}
          </span>
          <Button variant="secondary" onClick={restart} disabled={!cast}>
            Restart
          </Button>
          <Button onClick={() => setPaused((p) => !p)} disabled={!cast}>
            {paused ? 'Play' : 'Pause'}
          </Button>
        </>
      }
    >
      {error ? <Alert>{error}</Alert> : !cast ? <Spinner label="Loading the recording" /> : null}
      <div ref={host} aria-label="Recorded terminal" />
    </Dialog>
  )
}
