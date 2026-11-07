import { describe, expect, it } from 'vitest'
import { STANDARD_EVENTS, changedSettings, inviteLink, snippetFor, type SiteDetails } from './sites'

const site: SiteDetails = {
  id: 7,
  name: 'Blog',
  domain: 'blog.test',
  trackingId: 'ap_0123456789abcdef',
  visitorIdMode: 'hash',
  standardEvents: Object.fromEntries(STANDARD_EVENTS.map((e) => [e.key, true])),
}

describe('snippetFor', () => {
  it('loads tracker.js from the dashboard origin with the tracking ID', () => {
    expect(snippetFor('https://analytics.example.com', site.trackingId)).toBe(
      '<script defer src="https://analytics.example.com/tracker.js" data-tracking-id="ap_0123456789abcdef"></script>',
    )
  })

  it('does not double a trailing slash', () => {
    expect(snippetFor('http://localhost:5173/', 'ap_x')).toContain('src="http://localhost:5173/tracker.js"')
  })
})

describe('inviteLink', () => {
  it('opens the dashboard with ?invite=', () => {
    expect(inviteLink('https://analytics.example.com', 'abc-_123')).toBe('https://analytics.example.com/?invite=abc-_123')
  })

  it('encodes the token', () => {
    expect(inviteLink('http://x', 'a+b')).toBe('http://x/?invite=a%2Bb')
  })
})

describe('changedSettings', () => {
  it('is empty when nothing changed', () => {
    expect(changedSettings(site, { ...site })).toEqual({})
  })

  it('sends only what changed', () => {
    const edited: SiteDetails = {
      ...site,
      name: '  New name ',
      standardEvents: { ...site.standardEvents, scroll: false },
    }
    expect(changedSettings(site, edited)).toEqual({ name: 'New name', standardEvents: { scroll: false } })
  })

  it('includes the visitor-ID mode when switched', () => {
    expect(changedSettings(site, { ...site, visitorIdMode: 'persistent' })).toEqual({ visitorIdMode: 'persistent' })
  })
})

describe('STANDARD_EVENTS', () => {
  it('lists the 8 standard events the API knows', () => {
    expect(STANDARD_EVENTS.map((e) => e.key).sort()).toEqual([
      'file_download', 'form_start', 'form_submit', 'outbound_click', 'scroll',
      'video_complete', 'video_progress', 'video_start',
    ])
  })
})
