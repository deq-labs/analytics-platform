// Runs tracker.js inside a simulated browser page (jsdom) and records every
// request it makes instead of sending it.
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { JSDOM } from 'jsdom'

const TRACKER = readFileSync(
  fileURLToPath(new URL('../../src/AnalyticsPlatform.Api/wwwroot/tracker.js', import.meta.url)),
  'utf8',
)

export const API = 'https://analytics.example'

/**
 * @param {object} [options]
 * @param {string} [options.url]          the tracked page's URL
 * @param {string} [options.referrer]
 * @param {string} [options.body]         page HTML
 * @param {Record<string,string>} [options.attributes]  attributes on the <script> tag
 * @param {object|null} [options.config]  what /config answers (null = 404)
 */
export async function loadTracker({
  url = 'https://shop.test/products?id=1',
  referrer = 'https://search.test/q',
  body = '',
  attributes = { 'data-tracking-id': 'ap_0123456789abcdef' },
  config = { visitorIdMode: 'hash', standardEvents: [] },
} = {}) {
  const dom = new JSDOM(`<!doctype html><html><body>${body}</body></html>`, {
    url,
    referrer,
    runScripts: 'outside-only',
    pretendToBeVisual: true, // gives requestAnimationFrame
  })
  const { window } = dom

  /** @type {{url: string, init: any, body: any}[]} */
  const requests = []
  window.fetch = (requestUrl, init = {}) => {
    requests.push({ url: requestUrl, init, body: init.body ? JSON.parse(init.body) : undefined })
    if (requestUrl.endsWith('/config')) {
      return Promise.resolve({ ok: config !== null, json: () => Promise.resolve(config) })
    }
    return Promise.resolve({ ok: true, status: 202 })
  }

  // tracker.js reads its settings from document.currentScript, which only
  // exists while a real <script> runs -- provide a stand-in.
  const script = window.document.createElement('script')
  script.setAttribute('src', `${API}/tracker.js`)
  for (const [name, value] of Object.entries(attributes)) script.setAttribute(name, value)
  Object.defineProperty(window.document, 'currentScript', { configurable: true, get: () => script })

  window.eval(TRACKER)
  await settle(window)

  return {
    window,
    document: window.document,
    requests,
    /** Events sent to /api/events, in order. */
    events: () => requests.filter((r) => r.url.endsWith('/api/events')).map((r) => r.body),
    settle: () => settle(window),
  }
}

// Let pending promises and animation frames run.
async function settle(window) {
  for (let i = 0; i < 3; i++) {
    await new Promise((resolve) => window.setTimeout(resolve, 20))
  }
}
