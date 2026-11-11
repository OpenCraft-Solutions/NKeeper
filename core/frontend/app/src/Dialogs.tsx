import { useCallback, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import type { HostKeyPrompt, TrustPrompt } from '@nkeeper/sdk'
import { DialogContext, type Dialogs, type TypedCredential } from './dialogContext'
import { Button } from './ui/Button'
import { Checkbox } from './ui/Checkbox'
import prompts from './ui/CredentialPromptDialog.module.css'
import { Dialog } from './ui/Dialog'
import { PasswordField } from './ui/PasswordField'
import session from './ui/Session.module.css'
import field from './ui/TextField.module.css'
import { TextField } from './ui/TextField'

/**
 * Core's dialogs (docs/architecture.md §7.14): a server key no one trusted yet, and a typed credential.
 * They belong to the shell, not to any plugin. Each request is a promise, answered in turn.
 */

type Request =
  | { id: number; kind: 'hostKey'; prompt: HostKeyPrompt | TrustPrompt; hostName: string; resolve: (trust: boolean) => void }
  | { id: number; kind: 'credential'; hostName: string; serviceName: string; resolve: (credential: TypedCredential | null) => void }

/** A request without its ID, for each kind of request. */
type NewRequest = Request extends infer R ? (R extends Request ? Omit<R, 'id'> : never) : never

export function DialogProvider({ children }: { children: ReactNode }) {
  const [queue, setQueue] = useState<Request[]>([])
  const nextId = useRef(0)

  const done = useCallback((id: number) => setQueue((current) => current.filter((r) => r.id !== id)), [])

  /** Queues a request; aborting `signal` withdraws it, answering with `withdrawn`. */
  const push = useCallback(
    <T,>(request: NewRequest, withdrawn: T, signal: AbortSignal | undefined, settle: (answer: T) => void) => {
      if (signal?.aborted) {
        settle(withdrawn)
        return
      }
      const id = ++nextId.current
      const withdraw = () => {
        settle(withdrawn)
        done(id)
      }
      signal?.addEventListener('abort', withdraw, { once: true })
      const resolve = (answer: T) => {
        signal?.removeEventListener('abort', withdraw)
        settle(answer)
      }
      setQueue((current) => [...current, { ...request, id, resolve } as Request])
    },
    [done],
  )

  const dialogs = useMemo<Dialogs>(
    () => ({
      confirmHostKey: (prompt, hostName, signal) =>
        new Promise((settle) => push({ kind: 'hostKey', prompt, hostName, resolve: () => {} }, false, signal, settle)),
      askCredential: (hostName, serviceName, signal) =>
        new Promise((settle) => push({ kind: 'credential', hostName, serviceName, resolve: () => {} }, null, signal, settle)),
    }),
    [push],
  )

  const current = queue[0]
  return (
    <DialogContext.Provider value={dialogs}>
      {children}
      {current?.kind === 'hostKey' && (
        <HostKeyDialog
          key={current.id}
          request={current}
          onAnswer={(trust) => {
            current.resolve(trust)
            done(current.id)
          }}
        />
      )}
      {current?.kind === 'credential' && (
        <CredentialDialog
          key={current.id}
          request={current}
          onAnswer={(credential) => {
            current.resolve(credential)
            done(current.id)
          }}
        />
      )}
    </DialogContext.Provider>
  )
}

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

/** Trust on first use, as 1.x's trust card asked it: the user checks the server's identity before it's used. */
function HostKeyDialog({ request, onAnswer }: { request: Extract<Request, { kind: 'hostKey' }>; onAnswer: (trust: boolean) => void }) {
  const prompt = request.prompt as Partial<TrustPrompt> & HostKeyPrompt
  const certificate = prompt.kind === 'tls-certificate'
  return (
    <Dialog
      title={certificate ? 'Trust this certificate?' : 'Trust this host key?'}
      onClose={() => onAnswer(false)}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={() => onAnswer(false)}>
            Cancel
          </Button>
          <Button size="sm" onClick={() => onAnswer(true)} data-autofocus>
            Trust and connect
          </Button>
        </>
      }
    >
      <div className={prompts.form}>
        <p>
          This is the first connection to <strong>{request.hostName}</strong>. Check that the fingerprint matches the server’s before trusting it; NKeeper will refuse the
          connection if it ever changes.
          {certificate ? ' Self-signed certificates are normal for Remote Desktop and for many devices’ web pages.' : null}
        </p>
        <dl className={session.fingerprint}>
          <dt>{certificate ? 'SHA-256 fingerprint' : prompt.algorithm}</dt>
          <dd>SHA256:{prompt.fingerprint}</dd>
          {certificate && prompt.subject ? (
            <>
              <dt>Issued to</dt>
              <dd>{prompt.subject}</dd>
            </>
          ) : null}
          {certificate && prompt.notAfterUtc ? (
            <>
              <dt>Expires</dt>
              <dd>{dateFormat.format(new Date(prompt.notAfterUtc))}</dd>
            </>
          ) : null}
        </dl>
        {certificate ? <p className={session.hint}>On Windows, compare with the “Remote Desktop” certificate in the computer’s certificate store (certlm.msc).</p> : null}
      </div>
    </Dialog>
  )
}

/** A credential for this session only, as 1.x's "Enter credentials" asked it; 2.0 also takes a domain or a private key. */
function CredentialDialog({ request, onAnswer }: { request: Extract<Request, { kind: 'credential' }>; onAnswer: (credential: TypedCredential | null) => void }) {
  const [username, setUsername] = useState('')
  const [domain, setDomain] = useState('')
  const [withKey, setWithKey] = useState(false)
  const [secret, setSecret] = useState('')

  function submit(event: FormEvent) {
    event.preventDefault()
    onAnswer({
      username: username.trim(),
      domain: domain.trim() || null,
      password: withKey ? null : secret,
      privateKey: withKey ? secret : null,
    })
  }

  return (
    <Dialog
      title="Enter credentials"
      description={`${request.hostName} · ${request.serviceName}`}
      onClose={() => onAnswer(null)}
      footer={
        <>
          <Button variant="secondary" onClick={() => onAnswer(null)}>
            Cancel
          </Button>
          <Button type="submit" form="credential-prompt">
            Connect
          </Button>
        </>
      }
    >
      <form id="credential-prompt" className={prompts.form} onSubmit={submit} noValidate aria-label="Enter credentials">
        <TextField label="Username" value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" spellCheck={false} maxLength={256} required data-autofocus />
        <TextField label="Domain (optional)" value={domain} onChange={(e) => setDomain(e.target.value)} autoComplete="off" spellCheck={false} maxLength={256} />
        {withKey ? (
          <div className={field.field}>
            <label htmlFor="credential-key" className={field.label}>
              Private key
            </label>
            <textarea id="credential-key" className={field.input} rows={6} value={secret} onChange={(e) => setSecret(e.target.value)} spellCheck={false} />
          </div>
        ) : (
          <PasswordField label="Password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="off" maxLength={1024} />
        )}
        <Checkbox
          label="Sign in with a private key instead"
          checked={withKey}
          onChange={(e) => {
            setWithKey(e.target.checked)
            setSecret('')
          }}
        />
        <p className={prompts.hint}>Used this time only and not saved. To save it, add a credential to the host.</p>
      </form>
    </Dialog>
  )
}
