import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { sendJson } from './auth'

// Mirrors SiteDetails in src/AnalyticsPlatform.Api/Sites/SiteStore.cs.
export type VisitorIdMode = 'hash' | 'persistent'
export type SiteDetails = {
  id: number
  name: string
  domain: string
  trackingId: string
  visitorIdMode: VisitorIdMode
  standardEvents: Record<string, boolean>
}
export type SiteUpdate = Partial<Pick<SiteDetails, 'name' | 'visitorIdMode' | 'standardEvents'>>

// The toggleable auto-tracked events (CLAUDE.md "Standard (auto-tracked) events").
export const STANDARD_EVENTS = [
  { key: 'scroll', label: 'Scroll depth', hint: '25 / 50 / 75 / 90% of the page' },
  { key: 'outbound_click', label: 'Outbound links', hint: 'Clicks to other websites' },
  { key: 'file_download', label: 'File downloads', hint: 'PDF, ZIP, DOCX and similar' },
  { key: 'form_start', label: 'Form starts', hint: 'First interaction with a form (values are never read)' },
  { key: 'form_submit', label: 'Form submits', hint: '' },
  { key: 'video_start', label: 'Video starts', hint: 'Native <video> players' },
  { key: 'video_progress', label: 'Video progress', hint: '25 / 50 / 75%' },
  { key: 'video_complete', label: 'Video completes', hint: '' },
] as const

// ---- Pure helpers (unit-tested in sites.test.ts) ----

// The <script> tag a site owner pastes into their pages. tracker.js is served
// by the API, which shares an origin with the dashboard (Vite proxies it in dev).
export function snippetFor(origin: string, trackingId: string): string {
  const base = origin.replace(/\/+$/, '')
  return `<script defer src="${base}/tracker.js" data-tracking-id="${trackingId}"></script>`
}

export function inviteLink(origin: string, token: string): string {
  return `${origin.replace(/\/+$/, '')}/?invite=${encodeURIComponent(token)}`
}

// The PATCH body: only fields that differ from what the server has.
export function changedSettings(saved: SiteDetails, edited: SiteDetails): SiteUpdate {
  const update: SiteUpdate = {}
  const name = edited.name.trim()
  if (name !== saved.name) update.name = name
  if (edited.visitorIdMode !== saved.visitorIdMode) update.visitorIdMode = edited.visitorIdMode
  const events = Object.fromEntries(
    Object.entries(edited.standardEvents).filter(([key, on]) => saved.standardEvents[key] !== on),
  )
  if (Object.keys(events).length > 0) update.standardEvents = events
  return update
}

// ---- Server calls ----

export function useSiteDetails(siteId: number | undefined) {
  return useQuery({
    queryKey: ['site', siteId],
    queryFn: async () => (await (await sendJson('GET', `/api/sites/${siteId}`)).json()) as SiteDetails,
    enabled: siteId !== undefined,
  })
}

// After any change: refresh the site list (picker) and this site's details.
function useSiteMutation<TInput>(request: (input: TInput) => Promise<Response>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (input: TInput) => {
      const response = await request(input)
      return response.status === 204 ? null : ((await response.json()) as SiteDetails)
    },
    onSuccess: (site) => {
      if (site) queryClient.setQueryData(['site', site.id], site)
      return queryClient.invalidateQueries({ queryKey: ['sites'] })
    },
  })
}

export const useCreateSite = () =>
  useSiteMutation((input: { name: string; domain: string }) => sendJson('POST', '/api/sites', input))

export const useUpdateSite = (siteId: number) =>
  useSiteMutation((update: SiteUpdate) => sendJson('PATCH', `/api/sites/${siteId}`, update))

export const useRegenerateTrackingId = (siteId: number) =>
  useSiteMutation(() => sendJson('POST', `/api/sites/${siteId}/tracking-id`))

export const useDeleteSite = (siteId: number) =>
  useSiteMutation(() => sendJson('DELETE', `/api/sites/${siteId}`))

export function useCreateInvite() {
  return useMutation({
    mutationFn: async () =>
      (await (await sendJson('POST', '/api/admin/invites')).json()) as { token: string; expiresAt: string },
  })
}
