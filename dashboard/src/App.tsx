import { useEffect, useMemo, useState } from 'react'
import { useSites, useTopPages, useTopReferrers } from './api'
import { Card, QueryState } from './components/Card'
import { PageviewsChart } from './components/PageviewsChart'
import { TopList } from './components/TopList'
import { PRESETS, rangeFor, type PresetKey } from './format'

// The selected site and range live in the URL (?site=5&range=30d), so a refresh
// or a copied link shows the same view.
function readUrl(): { site?: number; preset: PresetKey } {
  const params = new URLSearchParams(location.search)
  const site = Number(params.get('site')) || undefined
  const range = params.get('range')
  const preset = PRESETS.some((p) => p.key === range) ? (range as PresetKey) : '7d'
  return { site, preset }
}

export default function App() {
  const initial = readUrl()
  const [siteId, setSiteId] = useState<number | undefined>(initial.site)
  const [preset, setPreset] = useState<PresetKey>(initial.preset)

  const sites = useSites()

  // Default to the first site once the list arrives.
  const activeSiteId = siteId ?? sites.data?.[0]?.id
  const activeSite = sites.data?.find((s) => s.id === activeSiteId)

  // useMemo: only recompute the range when the preset changes, not on every
  // render -- otherwise the query key would change constantly.
  const range = useMemo(() => rangeFor(preset), [preset])

  useEffect(() => {
    const params = new URLSearchParams({ range: preset })
    if (activeSiteId !== undefined) params.set('site', String(activeSiteId))
    history.replaceState(null, '', `?${params}`)
  }, [activeSiteId, preset])

  const pages = useTopPages(activeSiteId, range)
  const referrers = useTopReferrers(activeSiteId, range)
  const waitingForSite = activeSiteId === undefined

  return (
    <main className="app">
      <header className="app-header">
        <h1>Analytics</h1>
        <p>{activeSite ? activeSite.domain : 'Self-hosted web analytics'} · all times UTC</p>
      </header>

      {/* One filter row above everything; it scopes every view below. */}
      <div className="filters">
        <label>
          Site
          <select
            value={activeSiteId ?? ''}
            onChange={(e) => setSiteId(Number(e.target.value))}
            disabled={!sites.data?.length}
          >
            {sites.data?.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </label>
        <div className="segmented" role="group" aria-label="Date range">
          {PRESETS.map((p) => (
            <button key={p.key} aria-pressed={preset === p.key} onClick={() => setPreset(p.key)}>
              {p.label}
            </button>
          ))}
        </div>
      </div>

      {sites.error || sites.data?.length === 0 ? (
        <Card title="Sites">
          <QueryState error={sites.error} loading={false} />
          {sites.data?.length === 0 && (
            <p className="message">No sites yet. Run scripts/dev_seed.sql to create some.</p>
          )}
        </Card>
      ) : (
        <div className="grid">
          <PageviewsChart siteId={activeSiteId} range={range} />

          <TopList
            title="Top pages"
            items={pages.data?.items}
            error={pages.error}
            loading={pages.isPending && !waitingForSite}
            refreshing={pages.isPlaceholderData}
            labelHeader="Page"
            primaryHeader="Pageviews"
            secondaryHeader="Visitors"
            label={(p) => p.path}
            primary={(p) => p.pageviews}
            secondary={(p) => p.visitors}
            rowKey={(p) => p.path}
          />

          <TopList
            title="Top referrers"
            items={referrers.data?.items}
            error={referrers.error}
            loading={referrers.isPending && !waitingForSite}
            refreshing={referrers.isPlaceholderData}
            labelHeader="Source"
            primaryHeader="Sessions"
            secondaryHeader="Visitors"
            label={(r) => r.domain ?? <span className="muted">Direct / none</span>}
            primary={(r) => r.sessions}
            secondary={(r) => r.visitors}
            rowKey={(r) => r.domain ?? '(direct)'}
          />

          <Card title="Locations & devices" className="span-2">
            <p className="message">
              Country, browser, OS and device breakdowns arrive with ingestion enrichment (Phase 6).
              The data isn't collected yet.
            </p>
          </Card>
        </div>
      )}
    </main>
  )
}
