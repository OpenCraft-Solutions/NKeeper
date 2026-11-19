import { createElement, useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import type { Catalog } from './catalog'
import { credentialName, serviceName, usedWith } from './hosts'
import type { Host } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import page from './ui/Page.module.css'
import { PluginBoundary } from './ui/PluginBoundary'

/** One plugin's integration, as `GET /api/plugins/integrations` reports it (docs/architecture.md §7.39). */
export interface IntegrationInfo {
  pluginId: string
  name: string
  label: string
  serviceTypes: string[]
  binding: {
    hostId: string
    hostName: string
    serviceId: string
    serviceLabel: string
    credentialId: string
    credentialLabel: string
    setAtUtc: string
  } | null
}

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/**
 * The plugins' integrations and settings panels (§7.39, SDK 1.13): for each plugin with either, its bindings, set
 * with core's own host, service and credential pickers, then the plugin's own section.
 */
export function PluginIntegrations({ catalog }: { catalog: Catalog }) {
  const [integrations, setIntegrations] = useState<IntegrationInfo[] | null>(null)
  const [hosts, setHosts] = useState<Host[]>([])
  const [error, setError] = useState<string | null>(null)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    let live = true
    Promise.all([api.get<IntegrationInfo[]>('/api/plugins/integrations'), api.get<Host[]>('/api/hosts')]).then(
      ([found, all]) => {
        if (live) {
          setIntegrations(found)
          setHosts(all)
        }
      },
      () => live && setError('Couldn’t load the plugins’ integrations.'),
    )
    return () => {
      live = false
    }
  }, [version])

  const plugins = useMemo(() => {
    const ids = new Set([...(integrations ?? []).map((i) => i.pluginId), ...catalog.settingsPanels.keys()])
    return [...ids].sort()
  }, [integrations, catalog])

  if (error) {
    return <Alert>{error}</Alert>
  }
  if (!integrations) {
    return null
  }
  return plugins.map((pluginId) => {
    const own = integrations.filter((i) => i.pluginId === pluginId)
    const Panel = catalog.settingsPanels.get(pluginId)
    return (
      <section key={pluginId} className={page.panel} aria-label={`${pluginId} settings`}>
        <h2 className={page.panelTitle}>{pluginId}</h2>
        {own.map((integration) => (
          <Binding key={integration.name} integration={integration} hosts={hosts} catalog={catalog} onChange={() => setVersion((v) => v + 1)} />
        ))}
        {Panel ? (
          <PluginBoundary pluginId={pluginId}>
            {createElement(Panel, { integrations: own.map((i) => ({ name: i.name, label: i.label, bound: i.binding !== null })), version })}
          </PluginBoundary>
        ) : null}
      </section>
    )
  })
}

/** One integration's binding: what it's bound to, and choosing a host, one of its services and a credential. */
function Binding({ integration, hosts, catalog, onChange }: { integration: IntegrationInfo; hosts: Host[]; catalog: Catalog; onChange: () => void }) {
  const fits = (host: Host) => host.services.some((s) => integration.serviceTypes.includes(s.type))
  const [hostId, setHostId] = useState(integration.binding?.hostId ?? '')
  const [serviceId, setServiceId] = useState(integration.binding?.serviceId ?? '')
  const [credentialId, setCredentialId] = useState(integration.binding?.credentialId ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const host = hosts.find((h) => h.id === hostId)
  const services = host?.services.filter((s) => integration.serviceTypes.includes(s.type)) ?? []
  const serviceIndex = host?.services.findIndex((s) => s.id === serviceId) ?? -1
  const credentials = host && serviceIndex >= 0 ? host.credentials.filter((c) => usedWith(c, serviceIndex)) : []
  const path = `/api/plugins/${encodeURIComponent(integration.pluginId)}/integrations/${encodeURIComponent(integration.name)}`

  async function run(action: () => Promise<unknown>, fallback: string) {
    setBusy(true)
    setError(null)
    try {
      await action()
      onChange()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : fallback)
    } finally {
      setBusy(false)
    }
  }

  const bound = integration.binding
  return (
    <form
      aria-label={integration.label}
      onSubmit={(e) => {
        e.preventDefault()
        void run(() => api.put(path, { hostId, serviceId, credentialId }), `Couldn’t set ${integration.label}.`)
      }}
    >
      <h3 className={page.legend}>{integration.label}</h3>
      <p className={page.hint}>
        {bound
          ? `${bound.hostName} · ${bound.serviceLabel} · ${bound.credentialLabel}, set ${dateTimeFormat.format(new Date(bound.setAtUtc))}. Only users who may use ${bound.hostName} get anything through it.`
          : 'Not set. Choose the host, its service and the credential to use with it.'}
      </p>
      {error ? <Alert>{error}</Alert> : null}
      <label className={page.hint}>
        Host{' '}
        <select
          value={hostId}
          onChange={(e) => {
            setHostId(e.target.value)
            setServiceId('')
            setCredentialId('')
          }}
        >
          <option value="">Choose a host</option>
          {hosts.filter(fits).map((h) => (
            <option key={h.id} value={h.id}>
              {h.name}
            </option>
          ))}
        </select>
      </label>{' '}
      <label className={page.hint}>
        Service{' '}
        <select
          value={serviceId}
          disabled={!host}
          onChange={(e) => {
            setServiceId(e.target.value)
            setCredentialId('')
          }}
        >
          <option value="">Choose a service</option>
          {services.map((s) => (
            <option key={s.id} value={s.id}>
              {serviceName(s, catalog.serviceTypes)}
            </option>
          ))}
        </select>
      </label>{' '}
      <label className={page.hint}>
        Credential{' '}
        <select value={credentialId} disabled={!serviceId} onChange={(e) => setCredentialId(e.target.value)}>
          <option value="">Choose a credential</option>
          {credentials.map((c) => (
            <option key={c.id} value={c.id}>
              {credentialName(c)}
            </option>
          ))}
        </select>
      </label>{' '}
      <Button type="submit" size="sm" variant="secondary" loading={busy} disabled={!hostId || !serviceId || !credentialId}>
        Save
      </Button>{' '}
      {bound ? (
        <Button size="sm" variant="ghost" disabled={busy} onClick={() => void run(() => api.delete(path), `Couldn’t clear ${integration.label}.`)}>
          Clear
        </Button>
      ) : null}
    </form>
  )
}
