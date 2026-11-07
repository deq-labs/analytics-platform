import { describe, expect, it } from 'vitest'
import { API, loadTracker } from './harness.js'

const ALL_STANDARD = [
  'scroll', 'outbound_click', 'file_download', 'form_start', 'form_submit',
  'video_start', 'video_progress', 'video_complete',
]

describe('loading', () => {
  it('fetches the site config from the server the script came from', async () => {
    const t = await loadTracker()

    expect(t.requests[0].url).toBe(`${API}/api/tracking/ap_0123456789abcdef/config`)
    expect(t.requests[0].init.credentials).toBe('omit')
  })

  it('sends a pageview with the page, referrer and screen size', async () => {
    const t = await loadTracker()

    const [pageview] = t.events()
    expect(pageview).toMatchObject({
      trackingId: 'ap_0123456789abcdef',
      type: 'pageview',
      url: 'https://shop.test/products?id=1',
      referrer: 'https://search.test/q',
    })
    expect(typeof pageview.screenWidth).toBe('number')
  })

  it('posts as text/plain without cookies, so no CORS preflight and no visitor login state', async () => {
    const t = await loadTracker()

    const request = t.requests.find((r) => r.url === `${API}/api/events`)
    expect(request.init).toMatchObject({
      method: 'POST',
      headers: { 'Content-Type': 'text/plain' },
      credentials: 'omit',
      keepalive: true,
    })
  })

  it('does nothing without a tracking ID', async () => {
    const t = await loadTracker({ attributes: {} })

    expect(t.requests).toEqual([])
  })

  it('still sends pageviews if the config request fails', async () => {
    const t = await loadTracker({ config: null })

    expect(t.events().map((e) => e.type)).toEqual(['pageview'])
  })
})

describe('single-page apps', () => {
  it('counts pushState navigations, without the external referrer', async () => {
    const t = await loadTracker()

    t.window.history.pushState({}, '', '/checkout')
    await t.settle()

    const second = t.events()[1]
    expect(second).toMatchObject({ type: 'pageview', url: 'https://shop.test/checkout' })
    expect(second.referrer).toBeUndefined()
  })

  it('ignores #anchor changes and repeats of the same page', async () => {
    const t = await loadTracker()

    t.window.history.pushState({}, '', '/products?id=1#reviews')
    t.window.history.pushState({}, '', '/products?id=1')
    await t.settle()

    expect(t.events()).toHaveLength(1)
  })
})

describe('visitor IDs', () => {
  it('sends none in cookie-less (hash) mode and stores nothing', async () => {
    const t = await loadTracker()

    expect(t.events()[0].visitorId).toBeUndefined()
    expect(t.window.localStorage.length).toBe(0)
  })

  it('stores and reuses a random UUID in persistent mode', async () => {
    const t = await loadTracker({ config: { visitorIdMode: 'persistent', standardEvents: [] } })
    t.window.analyticsPlatform.track('second')
    await t.settle()

    const [first, second] = t.events()
    expect(first.visitorId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(second.visitorId).toBe(first.visitorId)
    expect(t.window.localStorage.getItem('ap_visitor_id')).toBe(first.visitorId)
  })

  it('waits for consent when asked to, and forgets the ID when consent is revoked', async () => {
    const t = await loadTracker({
      attributes: { 'data-tracking-id': 'ap_x', 'data-wait-for-consent': '' },
      config: { visitorIdMode: 'persistent', standardEvents: [] },
    })
    expect(t.events()[0].visitorId).toBeUndefined()

    t.window.analyticsPlatform.consent()
    t.window.analyticsPlatform.track('after_consent')
    await t.settle()
    expect(t.events()[1].visitorId).toBeTruthy()

    t.window.analyticsPlatform.revokeConsent()
    t.window.analyticsPlatform.track('after_revoke')
    await t.settle()
    expect(t.events()[2].visitorId).toBeUndefined()
    expect(t.window.localStorage.getItem('ap_visitor_id')).toBeNull()
  })
})

describe('custom events', () => {
  it('converts property values to strings, drops empty ones and clips long ones', async () => {
    const t = await loadTracker()

    t.window.analyticsPlatform.track('signup', { plan: 'pro', seats: 3, empty: '', none: null, long: 'x'.repeat(600) })
    await t.settle()

    const event = t.events()[1]
    expect(event).toMatchObject({ type: 'custom', name: 'signup' })
    expect(event.properties).toEqual({ plan: 'pro', seats: '3', long: 'x'.repeat(500) })
  })

  it('queues events fired before the config arrives', async () => {
    // Synchronously after loading, before any promise settles.
    const t = await loadTracker()
    expect(t.events()[0].type).toBe('pageview') // flushed in order after config
  })
})

describe('standard events', () => {
  const page = `
    <a id="out" href="https://other.test/page">Partner</a>
    <a id="same" href="/about">About</a>
    <a id="pdf" href="/files/Report.PDF?v=2">Report</a>
    <a id="dl" href="/export" download>Export</a>
    <form id="signup" name="signup" action="/join"><input name="email"><button>Go</button></form>
  `

  it('are not sent when the site has them turned off', async () => {
    const t = await loadTracker({ body: page })

    t.document.getElementById('out').dispatchEvent(new t.window.MouseEvent('click', { bubbles: true, cancelable: true }))
    await t.settle()

    expect(t.events().map((e) => e.type)).toEqual(['pageview'])
  })

  it('track outbound links, downloads, but not internal links', async () => {
    const t = await loadTracker({ body: page, config: { visitorIdMode: 'hash', standardEvents: ALL_STANDARD } })
    // jsdom would try to navigate on a real click; stop that, the tracker
    // listens in the capture phase so it still sees the click.
    t.document.addEventListener('click', (e) => e.preventDefault())

    for (const id of ['out', 'same', 'pdf', 'dl']) {
      t.document.getElementById(id).dispatchEvent(new t.window.MouseEvent('click', { bubbles: true, cancelable: true }))
    }
    await t.settle()

    const standard = t.events().filter((e) => e.type === 'standard')
    expect(standard.map((e) => [e.name, e.properties])).toEqual([
      ['outbound_click', { url: 'https://other.test/page', domain: 'other.test' }],
      ['file_download', { url: 'https://shop.test/files/Report.PDF?v=2', extension: 'pdf' }],
      ['file_download', { url: 'https://shop.test/export' }], // no extension: empty values are dropped
    ])
  })

  it('report form starts once and submits, never what was typed', async () => {
    const t = await loadTracker({ body: page, config: { visitorIdMode: 'hash', standardEvents: ALL_STANDARD } })
    const input = t.document.querySelector('input')
    const form = t.document.getElementById('signup')
    form.addEventListener('submit', (e) => e.preventDefault())

    input.value = 'secret@example.test'
    input.dispatchEvent(new t.window.Event('input', { bubbles: true }))
    input.dispatchEvent(new t.window.Event('input', { bubbles: true }))
    form.dispatchEvent(new t.window.Event('submit', { bubbles: true, cancelable: true }))
    await t.settle()

    const standard = t.events().filter((e) => e.type === 'standard')
    const formProps = { form_id: 'signup', form_name: 'signup', form_action: '/join' }
    expect(standard.map((e) => [e.name, e.properties])).toEqual([
      ['form_start', formProps],
      ['form_submit', formProps],
    ])
    expect(JSON.stringify(t.requests)).not.toContain('secret@example.test')
  })

  it('report each scroll depth once per page', async () => {
    const t = await loadTracker({ config: { visitorIdMode: 'hash', standardEvents: ALL_STANDARD } })
    // jsdom has no layout engine: pretend the page is 2000px tall in a 1000px window.
    Object.defineProperty(t.document.documentElement, 'scrollHeight', { configurable: true, value: 2000 })
    Object.defineProperty(t.window, 'innerHeight', { configurable: true, value: 1000 })
    const scrollTo = async (y) => {
      Object.defineProperty(t.window, 'scrollY', { configurable: true, value: y })
      t.window.dispatchEvent(new t.window.Event('scroll'))
      await t.settle()
    }

    await scrollTo(600) // 60%
    await scrollTo(400) // back up: nothing new
    await scrollTo(1000) // 100%

    const depths = t.events().filter((e) => e.name === 'scroll').map((e) => e.properties.depth)
    expect(depths).toEqual(['25', '50', '75', '90'])
  })
})
