import { useState, type ReactNode } from 'react'
import { useBreakdown, type Dimension, type Range } from '../api'
import { countryName, deviceLabel } from '../format'
import { TopList } from './TopList'

type Tab = { dimension: Dimension; label: string }

type BreakdownCardProps = {
  title: string
  tabs: Tab[]
  siteId: number | undefined
  range: Range
}

// How each dimension's raw value is shown. null = unknown.
function renderValue(dimension: Dimension, value: string | null): ReactNode {
  if (value === null) return <span className="muted">Unknown</span>
  switch (dimension) {
    case 'country':
      return (
        <>
          {countryName(value)} <span className="muted">{value}</span>
        </>
      )
    case 'device':
      return deviceLabel(value)
    default:
      return value
  }
}

// A ranked list of visitors by browser, OS, device or country. With more than
// one tab, switching tabs changes what the list groups by. (The site/date
// filters above still scope everything; tabs only pick the view.)
export function BreakdownCard({ title, tabs, siteId, range }: BreakdownCardProps) {
  const [dimension, setDimension] = useState(tabs[0].dimension)
  const query = useBreakdown(siteId, range, dimension)

  const tabControl =
    tabs.length > 1 ? (
      <div className="segmented small" role="group" aria-label={`${title} view`}>
        {tabs.map((t) => (
          <button key={t.dimension} aria-pressed={dimension === t.dimension} onClick={() => setDimension(t.dimension)}>
            {t.label}
          </button>
        ))}
      </div>
    ) : undefined

  return (
    <TopList
      title={title}
      action={tabControl}
      items={query.data?.items}
      error={query.error}
      loading={query.isPending && siteId !== undefined}
      refreshing={query.isPlaceholderData}
      labelHeader={tabs.find((t) => t.dimension === dimension)!.label}
      primaryHeader="Visitors"
      secondaryHeader="Pageviews"
      label={(item) => renderValue(dimension, item.value)}
      primary={(item) => item.visitors}
      secondary={(item) => item.pageviews}
      rowKey={(item) => item.value ?? '(unknown)'}
    />
  )
}
