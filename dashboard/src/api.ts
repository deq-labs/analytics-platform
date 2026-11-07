import { keepPreviousData, useQuery } from '@tanstack/react-query'

// ---- Types mirroring the C# records in src/AnalyticsPlatform.Api/Stats ----
// (ASP.NET Core serializes PascalCase properties as camelCase JSON.)

export type Site = { id: number; name: string; domain: string }

export type Interval = 'hour' | 'day'

export type TimeSeriesPoint = { time: string; pageviews: number; visitors: number }
export type TimeSeriesResponse = { from: string; to: string; interval: Interval; points: TimeSeriesPoint[] }

export type TopPage = { path: string; pageviews: number; visitors: number }
export type TopReferrer = { domain: string | null; sessions: number; visitors: number }
export type TopListResponse<T> = { from: string; to: string; items: T[] }

export type Range = { from: string; to: string; interval: Interval }

// ---- Fetching ----

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function getJson<T>(path: string): Promise<T> {
  let response: Response
  try {
    response = await fetch(path)
  } catch {
    throw new ApiError(0, 'Could not reach the API.')
  }
  // Vite's proxy answers 502/504 when the C# API isn't running.
  if (response.status === 502 || response.status === 504) {
    throw new ApiError(response.status, 'The API is not running.')
  }
  if (!response.ok) {
    throw new ApiError(response.status, `The API returned ${response.status}.`)
  }
  return (await response.json()) as T
}

function statsUrl(siteId: number, endpoint: string, range: Range, extra: Record<string, string> = {}) {
  const query = new URLSearchParams({ from: range.from, to: range.to, ...extra })
  return `/api/sites/${siteId}/stats/${endpoint}?${query}`
}

// ---- Hooks ----
// Each hook is a TanStack Query: it fetches, caches by `queryKey`, and
// re-fetches when the key changes (a different site or range).
// keepPreviousData keeps showing the old result while the new one loads, so
// charts dim instead of disappearing (see .card.refreshing).

export function useSites() {
  return useQuery({ queryKey: ['sites'], queryFn: () => getJson<Site[]>('/api/sites') })
}

export function usePageviews(siteId: number | undefined, range: Range) {
  return useQuery({
    queryKey: ['pageviews', siteId, range],
    queryFn: () =>
      getJson<TimeSeriesResponse>(statsUrl(siteId!, 'pageviews', range, { interval: range.interval })),
    enabled: siteId !== undefined,
    placeholderData: keepPreviousData,
  })
}

export function useTopPages(siteId: number | undefined, range: Range) {
  return useQuery({
    queryKey: ['top-pages', siteId, range],
    queryFn: () => getJson<TopListResponse<TopPage>>(statsUrl(siteId!, 'top-pages', range, { limit: '10' })),
    enabled: siteId !== undefined,
    placeholderData: keepPreviousData,
  })
}

export function useTopReferrers(siteId: number | undefined, range: Range) {
  return useQuery({
    queryKey: ['top-referrers', siteId, range],
    queryFn: () =>
      getJson<TopListResponse<TopReferrer>>(statsUrl(siteId!, 'top-referrers', range, { limit: '10' })),
    enabled: siteId !== undefined,
    placeholderData: keepPreviousData,
  })
}
