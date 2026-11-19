import { useCallback, useEffect, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import styles from './ui/Reports.module.css'
import { Spinner } from './ui/Spinner'

/** One audit event, as `GET /api/audit/events` gives it (docs/architecture.md §7.44). */
export interface AuditEvent {
  id: number
  occurredAtUtc: string
  actor: string
  userId: string | null
  clientAddress: string | null
  action: string
  target: string
  hostName: string | null
  detail: string
}

interface AuditPage {
  events: AuditEvent[]
  next: number | null
}

interface Filters {
  q: string
  category: string
  from: string
  to: string
}

const EMPTY: Filters = { q: '', category: '', from: '', to: '' }
const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' })

/** The filters as a query string; dates from the pickers are local days, sent as UTC times. */
function query(filters: Filters, before?: number): string {
  const params = new URLSearchParams()
  if (filters.q.trim()) {
    params.set('q', filters.q.trim())
  }
  if (filters.category) {
    params.set('category', filters.category)
  }
  if (filters.from) {
    params.set('from', new Date(`${filters.from}T00:00:00`).toISOString())
  }
  if (filters.to) {
    // Through the end of the chosen day.
    const end = new Date(`${filters.to}T00:00:00`)
    end.setDate(end.getDate() + 1)
    params.set('to', end.toISOString())
  }
  if (before !== undefined) {
    params.set('before', String(before))
  }
  const text = params.toString()
  return text ? `?${text}` : ''
}

/**
 * 1.x's audit log report (docs/architecture.md §7.44), for administrators: core's audit trail newest first, filtered
 * by text, category and days, a page at a time, and as CSV.
 */
export function AuditView({ onShowHost }: { onShowHost: (hostId: string) => void }) {
  const [draft, setDraft] = useState<Filters>(EMPTY)
  const [filters, setFilters] = useState<Filters>(EMPTY)
  const [categories, setCategories] = useState<string[]>([])
  const [events, setEvents] = useState<AuditEvent[] | null>(null)
  const [next, setNext] = useState<number | null>(null)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<string[]>('/api/audit/categories').then(setCategories, () => setCategories([]))
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    api.get<AuditPage>(`/api/audit/events${query(filters)}`, { signal: controller.signal }).then(
      (page) => {
        setEvents(page.events)
        setNext(page.next)
        setError(null)
      },
      (e: unknown) => {
        if (!controller.signal.aborted) {
          setError(e instanceof ApiError ? e.message : 'The audit log couldn’t be loaded.')
        }
      },
    )
    return () => controller.abort()
  }, [filters])

  const loadOlder = useCallback(async () => {
    if (next === null) {
      return
    }
    setLoadingMore(true)
    try {
      const page = await api.get<AuditPage>(`/api/audit/events${query(filters, next)}`)
      setEvents((current) => [...(current ?? []), ...page.events])
      setNext(page.next)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Older events couldn’t be loaded.')
    } finally {
      setLoadingMore(false)
    }
  }, [filters, next])

  async function download() {
    try {
      const blob = await api.get<Blob>(`/api/audit/events.csv${query(filters)}`, { responseType: 'blob' })
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `nkeeper-audit-${new Date().toISOString().slice(0, 10)}.csv`
      link.click()
      URL.revokeObjectURL(url)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'The CSV couldn’t be downloaded.')
    }
  }

  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>Audit log</h2>
          <p className={styles.reportSummary}>What happened on this server, and who did it, newest first.</p>
        </div>
        <div className={styles.reportActions}>
          <Button size="sm" variant="secondary" onClick={() => void download()}>
            Download CSV
          </Button>
        </div>
      </header>
      <form
        className={styles.toolbar}
        aria-label="Filter the audit log"
        onSubmit={(e) => {
          e.preventDefault()
          setEvents(null)
          setFilters(draft)
        }}
      >
        <input type="search" aria-label="Search" placeholder="Search user, action, target or detail" value={draft.q} maxLength={256} onChange={(e) => setDraft({ ...draft, q: e.target.value })} />
        <select aria-label="Category" value={draft.category} onChange={(e) => setDraft({ ...draft, category: e.target.value })}>
          <option value="">All categories</option>
          {categories.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
        <label>
          From <input type="date" value={draft.from} onChange={(e) => setDraft({ ...draft, from: e.target.value })} />
        </label>
        <label>
          To <input type="date" value={draft.to} onChange={(e) => setDraft({ ...draft, to: e.target.value })} />
        </label>
        <Button type="submit" size="sm">
          Apply
        </Button>
        <Button
          size="sm"
          variant="ghost"
          onClick={() => {
            setDraft(EMPTY)
            setEvents(null)
            setFilters(EMPTY)
          }}
        >
          Clear
        </Button>
      </form>
      {error ? <Alert>{error}</Alert> : null}
      {events === null && !error ? (
        <div className={styles.state}>
          <Spinner label="Loading the audit log" />
        </div>
      ) : null}
      {events && events.length === 0 ? <p className={styles.state}>No events match.</p> : null}
      {events && events.length > 0 ? (
        <div className={styles.tableScroll}>
          <table className={styles.healthTable} aria-label="Audit events">
            <thead>
              <tr>
                <th scope="col">Time</th>
                <th scope="col">User</th>
                <th scope="col">Action</th>
                <th scope="col">Target</th>
                <th scope="col">Detail</th>
                <th scope="col">From</th>
              </tr>
            </thead>
            <tbody>
              {events.map((e) => (
                <tr key={e.id}>
                  <td className={styles.mono}>
                    <time dateTime={e.occurredAtUtc}>{dateTimeFormat.format(new Date(e.occurredAtUtc))}</time>
                  </td>
                  <td>{e.actor}</td>
                  <td className={styles.mono}>{e.action}</td>
                  <td>
                    {e.hostName ? (
                      <button type="button" className={styles.hostLink} onClick={() => onShowHost(e.target)} title={e.target}>
                        {e.hostName}
                      </button>
                    ) : (
                      <span className={styles.mono}>{e.target}</span>
                    )}
                  </td>
                  <td>{e.detail}</td>
                  <td className={styles.mono}>{e.clientAddress ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      {next !== null && events ? (
        <div>
          <Button size="sm" variant="secondary" loading={loadingMore} onClick={() => void loadOlder()}>
            Load older
          </Button>
        </div>
      ) : null}
    </>
  )
}
