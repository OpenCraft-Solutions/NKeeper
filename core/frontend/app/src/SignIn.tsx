import { useState, type FormEvent } from 'react'
import { api, ApiError, resetAntiforgeryToken } from '@nkeeper/sdk'
import type { CurrentUser } from './types'

/** The sign-in form (docs/architecture.md §6.1). */
export function SignIn({ onSignedIn }: { onSignedIn: (user: CurrentUser) => void }) {
  const [userNameOrEmail, setUserNameOrEmail] = useState('')
  const [password, setPassword] = useState('')
  const [rememberMe, setRememberMe] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      // A failed sign-in isn't an expired session, so it mustn't notify the 401 listeners.
      const user = await api.post<CurrentUser>('/api/auth/login', { userNameOrEmail, password, rememberMe }, { suppressUnauthorizedEvent: true })
      // The antiforgery token is bound to the user, so the next unsafe request needs a new one.
      resetAntiforgeryToken()
      onSignedIn(user)
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'Sign-in failed.')
      setPassword('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="nk-form nk-signin" onSubmit={(event) => void submit(event)} aria-label="Sign in">
      <h2>Sign in</h2>
      <p>
        <label>
          User name or email <input name="userNameOrEmail" autoComplete="username" value={userNameOrEmail} onChange={(e) => setUserNameOrEmail(e.target.value)} required />
        </label>
      </p>
      <p>
        <label>
          Password <input name="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
      </p>
      <p>
        <label>
          <input type="checkbox" checked={rememberMe} onChange={(e) => setRememberMe(e.target.checked)} /> Remember me
        </label>
      </p>
      {error && <p role="alert">{error}</p>}
      <button type="submit" disabled={busy}>
        Sign in
      </button>
    </form>
  )
}
