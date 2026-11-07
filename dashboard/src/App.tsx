import { useEffect, useMemo, useState } from 'react'
import { useSites, useTopPages, useTopReferrers } from './api'
import { useLogout, useMe, type User } from './auth'
import { Card, QueryState } from './components/Card'
import { BreakdownCard } from './components/BreakdownCard'
import { PageviewsChart } from './components/PageviewsChart'
import { AuthScreen } from './components/AuthScreen'
import { TopList } from './components/TopList'
import { SitesPage } from './components/SitesPage'
import { PRESETS, rangeFor, type PresetKey } from './format'

// The selected site and range live in the URL (?site=5&range=30d), so a refresh
// or a copied link shows the same view.
type View = 'stats' | 'settings'

function readUrl(): { site?: number; preset: PresetKey; view: View } {
  const params = new URLSearchParams(location.search)
  const site = Number(params.get('site')) || undefined
  const range = params.get('range')
  const preset = PRESETS.some((p) => p.key === range) ? (range as PresetKey) : '7d'
  const view: View = params.get('view') === 'settings' ? 'settings' : 'stats'
  return { site, preset, view }
}

// Signed in -> dashboard. Not signed in -> setup / login / invite sign-up.
export default function App() {
  const me = useMe()
  if (me.isPending) return <main className="auth"><p className="message">Loading…</p></main>
  if (!me.data) return <AuthScreen />
  return <Dashboard user={me.data} />
}

function Dashboard({ user }: { user: User }) {
  const logout = useLogout()
  const initial = readUrl()
  const [siteId, setSiteId] = useState<number | undefined>(initial.site)
  const [preset, setPreset] = useState<PresetKey>(initial.preset)
  const [view, setView] = useState<View>(initial.view)

  const sites = useSites()

  // Default to the first site once the list arrives.
  // (An id that isn't in the list -- e.g. a just-deleted site -- falls back too.)
  const activeSiteId = sites.data?.some((s) => s.id === siteId) ? siteId : sites.data?.[0]?.id
  const activeSite = sites.data?.find((s) => s.id === activeSiteId)

  // useMemo: only recompute the range when the preset changes, not on every
  // render -- otherwise the query key would change constantly.
  const range = useMemo(() => rangeFor(preset), [preset])

  useEffect(() => {
    const params = new URLSearchParams({ range: preset })
    if (view === 'settings') params.set('view', view)
    if (activeSiteId !== undefined) params.set('site', String(activeSiteId))
    history.replaceState(null, '', `?${params}`)
  }, [activeSiteId, preset, view])

  const pages = useTopPages(activeSiteId, range)
  const referrers = useTopReferrers(activeSiteId, range)
  const waitingForSite = activeSiteId === undefined

  return (
    <main className="app">
      <header className="app-header">
        <div>
          <h1>Analytics</h1>
          <p>{activeSite ? activeSite.domain : 'Self-hosted web analytics'} · all times UTC</p>
        </div>
        <nav className="segmented" aria-label="View">
          <button aria-pressed={view === 'stats'} onClick={() => setView('stats')}>Dashboard</button>
          <button aria-pressed={view === 'settings'} onClick={() => setView('settings')}>Settings</button>
        </nav>
        <div className="account">
          <span>{user.email}{user.isAdmin && <span className="muted"> · admin</span>}</span>
          <button className="link-button" onClick={() => logout.mutate()} disabled={logout.isPending}>
            Sign out
          </button>
        </div>
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
        {view === 'stats' && (
          <div className="segmented" role="group" aria-label="Date range">
          {PRESETS.map((p) => (
            <button key={p.key} aria-pressed={preset === p.key} onClick={() => setPreset(p.key)}>
              {p.label}
            </button>
          ))}
          </div>
        )}
      </div>

      {sites.error ? (
        <Card title="Sites">
          <QueryState error={sites.error} loading={false} />
        </Card>
      ) : view === 'settings' || sites.data?.length === 0 ? (
        <SitesPage user={user} sites={sites.data} siteId={activeSiteId} onSelectSite={setSiteId} />
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

          <BreakdownCard title="Locations" tabs={[{ dimension: 'country', label: 'Country' }]} siteId={activeSiteId} range={range} />

          <BreakdownCard
            title="Devices"
            tabs={[
              { dimension: 'browser', label: 'Browser' },
              { dimension: 'os', label: 'OS' },
              { dimension: 'device', label: 'Device' },
            ]}
            siteId={activeSiteId}
            range={range}
          />
        </div>
      )}
    </main>
  )
}
