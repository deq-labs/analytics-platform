import { useState } from 'react'
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { TooltipContentProps } from 'recharts'
import { usePageviews, type Interval, type Range } from '../api'
import { formatAxis, formatBucket, formatNumber, niceTicks } from '../format'
import { Card, QueryState } from './Card'

// The two series. Colors come from CSS variables so dark mode swaps them.
const SERIES = [
  { key: 'pageviews', label: 'Pageviews', color: 'var(--series-1)' },
  { key: 'visitors', label: 'Visitors', color: 'var(--series-2)' },
] as const

export function PageviewsChart({ siteId, range }: { siteId: number | undefined; range: Range }) {
  const { data, error, isPending, isPlaceholderData } = usePageviews(siteId, range)
  const [showTable, setShowTable] = useState(false)

  const points = data?.points ?? []
  const interval = data?.interval ?? range.interval
  const total = points.reduce((sum, p) => sum + p.pageviews, 0)
  const hasData = points.some((p) => p.pageviews > 0)
  const yTicks = niceTicks(Math.max(...points.map((p) => p.pageviews), 0))

  return (
    <Card
      title="Pageviews over time"
      className="span-2"
      refreshing={isPlaceholderData}
      figure={data && <p className="figure">{formatNumber(total)}</p>}
      action={
        hasData && (
          <button className="link-button" onClick={() => setShowTable((v) => !v)} aria-pressed={showTable}>
            {showTable ? 'Chart' : 'Table'}
          </button>
        )
      }
    >
      <QueryState error={error} loading={isPending && siteId !== undefined} empty={data && !hasData} />

      {hasData && !showTable && (
        <>
          {/* Two series, so a legend is always shown; identity is never color alone. */}
          <ul className="legend">
            {SERIES.map((s) => (
              <li key={s.key}>
                <span className="line-key" style={{ background: s.color }} />
                {s.label}
              </li>
            ))}
          </ul>

          {/* Fixed height includes the x-axis labels, so the card never scrolls. */}
          <ResponsiveContainer width="100%" height={280}>
            <LineChart data={points} margin={{ top: 8, right: 12, bottom: 0, left: 0 }}>
              <CartesianGrid vertical={false} stroke="var(--grid)" />
              <XAxis
                dataKey="time"
                tickFormatter={(t: string) => formatBucket(t, interval)}
                tick={{ fill: 'var(--text-muted)', fontSize: 12 }}
                axisLine={{ stroke: 'var(--axis)' }}
                tickLine={false}
                minTickGap={24}
              />
              <YAxis
                ticks={yTicks}
                domain={[0, yTicks[yTicks.length - 1]]}
                tickFormatter={formatAxis}
                tick={{ fill: 'var(--text-muted)', fontSize: 12 }}
                axisLine={false}
                tickLine={false}
                allowDecimals={false}
                width={48}
              />
              {/* Crosshair: a vertical hairline that snaps to the nearest point. */}
              <Tooltip
                content={(props) => <ChartTooltip {...props} interval={interval} />}
                cursor={{ stroke: 'var(--axis)', strokeWidth: 1 }}
                isAnimationActive={false}
              />
              {SERIES.map((s) => (
                <Line
                  key={s.key}
                  dataKey={s.key}
                  name={s.label}
                  type="linear"
                  stroke={s.color}
                  strokeWidth={2}
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  dot={false}
                  activeDot={{ r: 4, fill: s.color, stroke: 'var(--surface)', strokeWidth: 2 }}
                  isAnimationActive={false}
                />
              ))}
            </LineChart>
          </ResponsiveContainer>
        </>
      )}

      {hasData && showTable && (
        <table>
          <thead>
            <tr>
              <th>{interval === 'hour' ? 'Hour (UTC)' : 'Day (UTC)'}</th>
              <th className="num">Pageviews</th>
              <th className="num">Visitors</th>
            </tr>
          </thead>
          <tbody>
            {points.map((p) => (
              <tr key={p.time}>
                <td>{formatBucket(p.time, interval, true)}</td>
                <td className="num primary">{formatNumber(p.pageviews)}</td>
                <td className="num">{formatNumber(p.visitors)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  )
}

// One tooltip listing every series at the hovered time. Values lead (strong),
// series names follow (secondary), each keyed by a short line in its color.
function ChartTooltip({ active, payload, label, interval }: TooltipContentProps & { interval: Interval }) {
  if (!active || !payload?.length) return null
  return (
    <div className="tooltip">
      <div className="tooltip-time">{formatBucket(String(label), interval, true)}</div>
      {SERIES.map((s) => {
        const entry = payload.find((p) => p.dataKey === s.key)
        return (
          <div className="tooltip-row" key={s.key}>
            <span className="line-key" style={{ background: s.color }} />
            <strong>{formatNumber(Number(entry?.value ?? 0))}</strong>
            <span>{s.label}</span>
          </div>
        )
      })}
    </div>
  )
}
