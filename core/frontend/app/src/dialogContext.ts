import { createContext, useContext } from 'react'
import type { HostKeyPrompt } from '@nkeeper/sdk'

/** What core's dialogs (Dialogs.tsx) offer the rest of the shell. */

export interface TypedCredential {
  username: string
  domain: string | null
  password: string | null
  privateKey: string | null
}

export interface Dialogs {
  confirmHostKey(prompt: HostKeyPrompt, hostName: string): Promise<boolean>
  askCredential(hostName: string, serviceName: string): Promise<TypedCredential | null>
}

export const DialogContext = createContext<Dialogs | null>(null)

export function useDialogs(): Dialogs {
  const dialogs = useContext(DialogContext)
  if (!dialogs) {
    throw new Error('useDialogs needs a DialogProvider.')
  }
  return dialogs
}
