import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import type { Catalog } from './catalog'
import { tagNames } from './hosts'
import { buildInventory, fieldLabel, inventoryCsv, type InventoryField } from './inventory'
import type { Host } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import styles from './ui/Reports.module.css'
import { Spinner } from './ui/Spinner'

const MAX_FIELDS = 3
const STORAGE_KEY = 'nkeeper.inventory.fields'

const encode = (field: InventoryField) => (field.kind === 'service' ? '@service' : `tag:${field.name}`)
const decode = (text: string): InventoryField | null =>
  text === '@service' ? { kind: 'service' } : text.startsWith('tag:') && text.length > 4 ? { kind: 'tag', name: text.slice(4) } : null

function remembered(): InventoryField[] {
  try {
    const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '[]') as unknown
    return Array.isArray(saved) ? saved.map((s) => (typeof s === 'string' ? decode(s) : null)).filter((f): f is InventoryField => f !== null).slice(0, MAX_FIELDS) : []
  } catch {
    return []
  }
}

/**
 * 1.x's inventory report (docs/architecture.md §7.47): how many hosts have each combination of values of up to three
 * fields, a tag name or the services' type.
 */
export function InventoryView({ catalog, onShowHost }: { catalog: Catalog; onShowHost: (hostId: string) => void }) {
  const [hosts, setHosts] = useState<Host[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [fields, setFields] = useState<InventoryField[]>(remembered)

  useEffect(() => {
    const controller = new AbortController()
    api.get<Host[]>('/api/hosts', { signal: controller.signal }).then(setHosts, (e: unknown) => {
      if (!controller.signal.aborted) {
        setError(e instanceof ApiError ? e.message : 'The hosts couldn’t be loaded.')
      }
    })
    return () => controller.abort()
  }, [])

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(fields.map(encode)))
    } catch {
      // A preference only: nothing to do without storage.
    }
  }, [fields])

  const report = useMemo(() => buildInventory(hosts ?? [], fields, catalog.serviceTypes), [hosts, fields, catalog])
  const choices: InventoryField[] = [{ kind: 'service' }, ...tagNames(hosts ?? []).map((name): InventoryField => ({ kind: 'tag', name }))]

  if (error && !hosts) {
    return (
      <div className={styles.state}>
        <Alert>{error}</Alert>
      </div>
    )
  }
  if (!hosts) {
    return (
      <div className={styles.state}>
        <Spinner label="Loading hosts" />
      </div>
    )
  }

  function download() {
    const url = URL.createObjectURL(new Blob([inventoryCsv(report)], { type: 'text/csv;charset=utf-8' }))
    const link = document.createElement('a')
    link.href = url
    link.download = 'nkeeper-inventory.csv'
    link.click()
    URL.revokeObjectURL(url)
  }

  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>Inventory</h2>
          <p className={styles.reportSummary}>
            How many hosts have each combination of values. A host with two values counts under each, so rows can add up to more than the {report.hostCount || hosts.length} hosts.
          </p>
        </div>
        <div className={styles.reportActions}>
          <Button size="sm" variant="secondary" disabled={report.rows.length === 0} onClick={download}>
            Download CSV
          </Button>
        </div>
      </header>
      <div className={styles.toolbar} role="group" aria-label="Count by">
        {Array.from({ length: Math.min(fields.length + 1, MAX_FIELDS) }, (_, index) => (
          <label key={index}>
            {index === 0 ? 'Count by ' : 'then '}
            <select
              aria-label={`Field ${index + 1}`}
              value={fields[index] ? encode(fields[index]) : ''}
              onChange={(e) => {
                const chosen = decode(e.target.value)
                setFields((current) => (chosen ? [...current.slice(0, index), chosen] : current.slice(0, index)))
              }}
            >
              <option value="">{index === 0 ? 'Choose a field' : '(nothing more)'}</option>
              {choices
                .filter((c) => !fields.slice(0, index).some((f) => encode(f) === encode(c)))
                .map((c) => (
                  <option key={encode(c)} value={encode(c)}>
                    {fieldLabel(c)}
                  </option>
                ))}
            </select>
          </label>
        ))}
      </div>
      {fields.length === 0 ? <p className={styles.state}>Choose a field to count hosts by, such as a tag or the service type.</p> : null}
      {report.rows.length > 0 ? (
        <div className={styles.tableScroll}>
          <table className={styles.healthTable} aria-label="Inventory">
            <thead>
              <tr>
                {report.fields.map((f) => (
                  <th key={encode(f)} scope="col">
                    {fieldLabel(f)}
                  </th>
                ))}
                <th scope="col">Hosts</th>
                <th scope="col">Which</th>
              </tr>
            </thead>
            <tbody>
              {report.rows.map((row) => (
                <tr key={row.key}>
                  {row.values.map((v, i) => (
                    <td key={i} className={v.missing ? styles.rowNote : undefined}>
                      {v.label}
                    </td>
                  ))}
                  <td className={styles.mono}>{row.hosts.length}</td>
                  <td>
                    {row.hosts.map((h, i) => (
                      <span key={h.id}>
                        {i > 0 ? ', ' : ''}
                        <button type="button" className={styles.hostLink} onClick={() => onShowHost(h.id)}>
                          {h.name}
                        </button>
                      </span>
                    ))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </>
  )
}
