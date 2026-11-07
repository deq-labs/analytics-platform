import type { Interval, Range } from './api'

// ---- Date range presets ----
// All ranges are UTC, matching the API. Day ranges start at midnight UTC so
// every bar/point is a whole day; the current (partial) day/hour is included.

export type PresetKey = '24h' | '7d' | '30d' | '90d'

export const PRESETS: { key: PresetKey; label: string }[] = [
  { key: '24h', label: '24 hours' },
  { key: '7d', label: '7 days' },
  { key: '30d', label: '30 days' },
  { key: '90d', label: '90 days' },
]

const HOUR = 3_600_000
const DAY = 24 * HOUR

export function rangeFor(preset: PresetKey, now = new Date()): Range {
  // Round "now" up to the next minute so the range (and so the query key)
  // stays the same for a minute instead of changing on every render.
  const to = new Date(Math.ceil(now.getTime() / 60_000) * 60_000)

  if (preset === '24h') {
    const startOfHour = Math.floor(now.getTime() / HOUR) * HOUR
    return { from: new Date(startOfHour - 23 * HOUR).toISOString(), to: to.toISOString(), interval: 'hour' }
  }

  const days = { '7d': 7, '30d': 30, '90d': 90 }[preset]
  const startOfToday = Math.floor(now.getTime() / DAY) * DAY
  return {
    from: new Date(startOfToday - (days - 1) * DAY).toISOString(),
    to: to.toISOString(),
    interval: 'day',
  }
}

// ---- Axis ticks ----
// Round tick values (0, 500, 1,000, ...) instead of whatever divides the max
// evenly (0, 450, 900, ...). Picks a step of 1, 2, 2.5 or 5 x a power of ten
// that gives about four intervals.
export function niceTicks(max: number, target = 4): number[] {
  if (max <= 0) return [0, 1]
  const rough = max / target
  const power = 10 ** Math.floor(Math.log10(rough))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * power).find((s) => s >= rough)!
  const top = Math.ceil(max / step) * step
  const ticks: number[] = []
  for (let v = 0; v <= top + step / 2; v += step) ticks.push(Math.round(v))
  return ticks
}

// ---- Number and date formatting ----

const full = new Intl.NumberFormat()
const compact = new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 })

export const formatNumber = (n: number) => full.format(n)

// Axis ticks: in full under 10,000, compact above (10K, 20K).
export const formatAxis = (n: number) => (Math.abs(n) >= 10_000 ? compact.format(n) : full.format(n))

export function formatBucket(iso: string, interval: Interval, long = false) {
  const date = new Date(iso)
  return interval === 'hour'
    ? date.toLocaleString(undefined, {
        timeZone: 'UTC',
        hour: 'numeric',
        ...(long ? { month: 'short', day: 'numeric' } : {}),
      })
    : date.toLocaleDateString(undefined, {
        timeZone: 'UTC',
        month: 'short',
        day: 'numeric',
        ...(long ? { weekday: 'short' } : {}),
      })
}
