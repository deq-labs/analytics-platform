import { describe, expect, it } from 'vitest'
import { curlExample } from './apiKeys'

describe('curlExample', () => {
  it('shows how a server reads stats with a key', () => {
    expect(curlExample('https://analytics.example.com/', 5, 'apk_secret')).toBe(
      'curl -H "Authorization: Bearer apk_secret" "https://analytics.example.com/api/sites/5/stats/top-pages"',
    )
  })

  it('uses a placeholder when the key is not known (it is only shown once)', () => {
    expect(curlExample('http://x', 1)).toContain('Bearer apk_YOUR_KEY')
  })
})
