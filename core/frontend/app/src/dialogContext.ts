import { createContext, useContext } from 'react'
import type { HostKeyPrompt, TrustPrompt } from '@nkeeper/sdk'

/** What core's dialogs (Dialogs.tsx) offer the rest of the shell. */

export interface TypedCredential {
  username: string
  domain: string | null
  password: string | null
  privateKey: string | null
}

/** Each request waits its turn. Aborting its signal withdraws it: a key is then not trusted, and no credential is given. */
export interface Dialogs {
  /** A key, or (with its kind) a certificate: the dialog says which, with a certificate's subject and expiry. */
  confirmHostKey(prompt: HostKeyPrompt | TrustPrompt, hostName: string, signal?: AbortSignal): Promise<boolean>
  askCredential(hostName: string, serviceName: string, signal?: AbortSignal): Promise<TypedCredential | null>
}

export const DialogContext = createContext<Dialogs | null>(null)

export function useDialogs(): Dialogs {
  const dialogs = useContext(DialogContext)
  if (!dialogs) {
    throw new Error('useDialogs needs a DialogProvider.')
  }
  return dialogs
}
