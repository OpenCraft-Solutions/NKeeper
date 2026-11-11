import { useState, type FormEvent } from 'react'
import { api, ApiError, resetAntiforgeryToken } from '@nkeeper/sdk'
import type { CurrentUser } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import { Checkbox } from './ui/Checkbox'
import form from './ui/Form.module.css'
import { PasswordField } from './ui/PasswordField'
import { TextField } from './ui/TextField'

/** The sign-in form (docs/architecture.md §6.1), as 1.x's: it sits on the sign-in card. */
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
    <form className={form.form} onSubmit={(event) => void submit(event)} noValidate aria-label="Sign in">
      {error && <Alert>{error}</Alert>}
      <TextField
        label="Username or email"
        name="username"
        type="text"
        autoComplete="username"
        autoCapitalize="none"
        autoCorrect="off"
        spellCheck={false}
        inputMode="email"
        required
        value={userNameOrEmail}
        onChange={(e) => setUserNameOrEmail(e.target.value)}
      />
      <PasswordField label="Password" name="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
      <div className={form.row}>
        <Checkbox label="Remember me" name="rememberMe" checked={rememberMe} onChange={(e) => setRememberMe(e.target.checked)} />
      </div>
      <Button type="submit" fullWidth loading={busy}>
        {busy ? 'Signing in…' : 'Sign in'}
      </Button>
    </form>
  )
}
