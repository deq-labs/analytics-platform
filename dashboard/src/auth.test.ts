import { describe, expect, it } from 'vitest'
import { authScreen } from './auth'

// Which sign-in screen to show, from GET /api/auth/status and the URL.
describe('authScreen', () => {
  it('first run: create the admin account', () => {
    expect(authScreen({ setupRequired: true, openRegistration: false }, null)).toBe('setup')
  })

  it('setup wins even if there is an invite in the URL', () => {
    expect(authScreen({ setupRequired: true, openRegistration: false }, 'abc')).toBe('setup')
  })

  it('an invite link opens the sign-up form', () => {
    expect(authScreen({ setupRequired: false, openRegistration: false }, 'abc')).toBe('register')
  })

  it('otherwise: log in', () => {
    expect(authScreen({ setupRequired: false, openRegistration: false }, null)).toBe('login')
    expect(authScreen({ setupRequired: false, openRegistration: true }, null)).toBe('login')
  })
})
