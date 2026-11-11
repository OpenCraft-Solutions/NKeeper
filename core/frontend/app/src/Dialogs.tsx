import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import type { HostKeyPrompt } from '@nkeeper/sdk'
import { DialogContext, type Dialogs, type TypedCredential } from './dialogContext'

/**
 * Core's dialogs (docs/architecture.md §7.14): a server key no one trusted yet, and a typed credential.
 * They belong to the shell, not to any plugin. Each request is a promise, answered in turn.
 */

type Request =
  | { id: number; kind: 'hostKey'; prompt: HostKeyPrompt; hostName: string; resolve: (trust: boolean) => void }
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

/**
 * A modal dialog: the browser's own, opened with showModal, so focus moves into it, the page behind is inert,
 * and focus returns when it closes. Escape is the same as Cancel.
 */
function Modal({ title, onCancel, children }: { title: string; onCancel: () => void; children: ReactNode }) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const element = dialog.current
    if (element && !element.open) {
      if (typeof element.showModal === 'function') {
        element.showModal()
      } else {
        element.setAttribute('open', '')
      }
    }
    return () => element?.close?.()
  }, [])
  return (
    <dialog
      ref={dialog}
      className="nk-dialog"
      aria-label={title}
      onCancel={(event) => {
        event.preventDefault()
        onCancel()
      }}
    >
      <h2>{title}</h2>
      {children}
    </dialog>
  )
}

function HostKeyDialog({ request, onAnswer }: { request: Extract<Request, { kind: 'hostKey' }>; onAnswer: (trust: boolean) => void }) {
  return (
    <Modal title="Trust this server key?" onCancel={() => onAnswer(false)}>
      <p>
        <strong>{request.hostName}</strong> presented a key no one has trusted yet. Check its fingerprint with the server’s administrator before
        trusting it.
      </p>
      <dl className="nk-facts">
        <dt>Algorithm</dt>
        <dd>{request.prompt.algorithm}</dd>
        <dt>Fingerprint</dt>
        <dd className="nk-mono">SHA256:{request.prompt.fingerprint}</dd>
      </dl>
      <div className="nk-actions">
        <button type="button" onClick={() => onAnswer(false)}>
          Cancel
        </button>
        <button type="button" className="nk-primary" onClick={() => onAnswer(true)}>
          Trust
        </button>
      </div>
    </Modal>
  )
}

function CredentialDialog({ request, onAnswer }: { request: Extract<Request, { kind: 'credential' }>; onAnswer: (credential: TypedCredential | null) => void }) {
  const [username, setUsername] = useState('')
  const [domain, setDomain] = useState('')
  const [secretKind, setSecretKind] = useState<'password' | 'privateKey'>('password')
  const [secret, setSecret] = useState('')

  function submit(event: FormEvent) {
    event.preventDefault()
    onAnswer({
      username: username.trim(),
      domain: domain.trim() || null,
      password: secretKind === 'password' ? secret : null,
      privateKey: secretKind === 'privateKey' ? secret : null,
    })
  }

  return (
    <Modal title="Type a credential" onCancel={() => onAnswer(null)}>
      <form onSubmit={submit} className="nk-form">
        <p>
          For {request.serviceName} on <strong>{request.hostName}</strong>. It’s used for this session only, and isn’t saved.
        </p>
        <label>
          User name
          <input autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} required autoFocus />
        </label>
        <label>
          Domain (optional)
          <input autoComplete="off" value={domain} onChange={(e) => setDomain(e.target.value)} />
        </label>
        <fieldset className="nk-inline">
          <legend>Sign in with</legend>
          <label>
            <input type="radio" checked={secretKind === 'password'} onChange={() => setSecretKind('password')} /> A password
          </label>
          <label>
            <input type="radio" checked={secretKind === 'privateKey'} onChange={() => setSecretKind('privateKey')} /> A private key
          </label>
        </fieldset>
        {secretKind === 'password' ? (
          <label>
            Password
            <input type="password" autoComplete="off" value={secret} onChange={(e) => setSecret(e.target.value)} />
          </label>
        ) : (
          <label>
            Private key
            <textarea className="nk-mono" rows={6} value={secret} onChange={(e) => setSecret(e.target.value)} />
          </label>
        )}
        <div className="nk-actions">
          <button type="button" onClick={() => onAnswer(null)}>
            Cancel
          </button>
          <button type="submit" className="nk-primary">
            Connect
          </button>
        </div>
      </form>
    </Modal>
  )
}
