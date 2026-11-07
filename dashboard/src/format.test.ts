import { describe, expect, it } from 'vitest'
import { formatAxis, niceTicks, rangeFor } from './format'

// Run with `npm test`. Vitest's describe/it/expect read like xUnit's
// class/[Fact]/Assert.

describe('niceTicks', () => {
  it.each([
    // max, expected ticks
    [1572, [0, 500, 1000, 1500, 2000]], // the bug the screenshots caught: was 0/450/900...
    [18, [0, 5, 10, 15, 20]],
    [100, [0, 25, 50, 75, 100]],
    [7, [0, 2, 4, 6, 8]],
    // Counts are whole numbers, so the step is never below 1.
    [3, [0, 1, 2, 3]],
    [1, [0, 1]],
  ])('max %d -> %j', (max, expected) => {
    expect(niceTicks(max)).toEqual(expected)
  })

  it('never repeats a tick', () => {
    for (let max = 1; max <= 200; max++) {
      const ticks = niceTicks(max)
      expect(new Set(ticks).size).toBe(ticks.length)
    }
  })

  it('always starts at zero and covers the max', () => {
    for (const max of [3, 42, 999, 12_345, 7_654_321]) {
      const ticks = niceTicks(max)
      expect(ticks[0]).toBe(0)
      expect(ticks.at(-1)).toBeGreaterThanOrEqual(max)
    }
  })

  it('handles an empty chart', () => {
    expect(niceTicks(0)).toEqual([0, 1])
  })
})

describe('rangeFor', () => {
  // Fri 2026-11-07 14:42:10 UTC
  const now = new Date(Date.UTC(2026, 10, 7, 14, 42, 10))

  it('24h is hourly, starting 23 whole hours before the current hour', () => {
    expect(rangeFor('24h', now)).toEqual({
      from: '2026-11-06T15:00:00.000Z',
      to: '2026-11-07T14:43:00.000Z', // "now" rounded up to the minute
      interval: 'hour',
    })
  })

  it.each([
    ['7d', '2026-11-01T00:00:00.000Z'],
    ['30d', '2026-10-09T00:00:00.000Z'],
    ['90d', '2026-08-10T00:00:00.000Z'],
  ] as const)('%s is daily and starts at UTC midnight, including today', (preset, from) => {
    const range = rangeFor(preset, now)
    expect(range.from).toBe(from)
    expect(range.interval).toBe('day')
  })

  it('stays the same within a minute (stable query key)', () => {
    const a = rangeFor('7d', new Date(Date.UTC(2026, 10, 7, 14, 42, 1)))
    const b = rangeFor('7d', new Date(Date.UTC(2026, 10, 7, 14, 42, 59)))
    expect(a).toEqual(b)
  })
})

describe('formatAxis', () => {
  it('writes numbers in full under 10,000 and compact above', () => {
    expect(formatAxis(9_500)).toBe((9_500).toLocaleString())
    expect(formatAxis(20_000)).toMatch(/^20\s?K$/i)
  })
})
