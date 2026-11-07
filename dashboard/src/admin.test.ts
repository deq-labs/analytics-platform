import { describe, expect, it } from 'vitest'
import { deleteWarning } from './admin'

describe('deleteWarning', () => {
  it('says what goes with the user', () => {
    expect(deleteWarning({ id: 2, email: 'a@b.test', isAdmin: false, sites: 3, createdAt: '' })).toBe(
      'Deletes a@b.test, their 3 sites and all of those sites’ analytics data.',
    )
  })

  it('handles one site and no sites', () => {
    expect(deleteWarning({ id: 2, email: 'a@b.test', isAdmin: false, sites: 1, createdAt: '' })).toContain('their 1 site and')
    expect(deleteWarning({ id: 2, email: 'a@b.test', isAdmin: false, sites: 0, createdAt: '' })).toBe('Deletes a@b.test.')
  })
})
