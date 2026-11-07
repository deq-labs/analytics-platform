import { useState, type FormEvent } from 'react'
import { authScreen, inviteTokenFromUrl, useAuthStatus, useLogin, useRegister } from '../auth'

const COPY = {
  setup: {
    title: 'Set up Analytics',
    intro: 'Create the first account. It becomes the administrator and can invite others.',
    button: 'Create admin account',
  },
  register: {
    title: 'Create your account',
    intro: "You've been invited. Choose your email and a password.",
    button: 'Create account',
  },
  login: { title: 'Sign in', intro: '', button: 'Sign in' },
} as const

// Shown instead of the dashboard when nobody is signed in.
export function AuthScreen() {
  const status = useAuthStatus()
  const login = useLogin()
  const register = useRegister()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const inviteToken = inviteTokenFromUrl()

  if (status.isPending) return <main className="auth"><p className="message">Loading…</p></main>
  if (status.error) {
    return (
      <main className="auth">
        <p className="message error" role="alert"><strong>Could not reach the API.</strong> Start it in Visual Studio (F5), then refresh.</p>
      </main>
    )
  }

  const screen = authScreen(status.data, inviteToken)
  const copy = COPY[screen]
  const action = screen === 'login' ? login : register
  const error = action.error?.message

  function submit(e: FormEvent) {
    e.preventDefault()
    if (screen === 'login') login.mutate({ email, password })
    else register.mutate({ email, password, inviteToken })
  }

  return (
    <main className="auth">
      <form className="card auth-card" onSubmit={submit}>
        <h1>{copy.title}</h1>
        {copy.intro && <p className="sub">{copy.intro}</p>}

        <label>
          Email
          <input type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </label>
        <label>
          Password
          <input
            type="password"
            autoComplete={screen === 'login' ? 'current-password' : 'new-password'}
            required
            minLength={screen === 'login' ? undefined : 12}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </label>
        {screen !== 'login' && <p className="hint">At least 12 characters. A few random words work well.</p>}

        {error && <p className="message error" role="alert">{error}</p>}

        <button className="primary" type="submit" disabled={action.isPending}>
          {action.isPending ? 'Please wait…' : copy.button}
        </button>

        {screen === 'login' && (
          <p className="hint">
            {status.data.openRegistration
              ? 'New here? Ask for an invite link, or sign up with one.'
              : 'Accounts are by invite. Ask the administrator for an invite link.'}
          </p>
        )}
      </form>
    </main>
  )
}
