import type { ReactNode } from 'react'
import { formatNumber } from '../format'
import { Card, QueryState } from './Card'

// A generic ranked table: works for pages, referrers, or anything else with a
// label and two counts. `T` is a TypeScript generic, like C#'s List<T>.
type TopListProps<T> = {
  title: string
  items: T[] | undefined
  error: unknown
  loading: boolean
  refreshing: boolean
  labelHeader: string
  primaryHeader: string
  secondaryHeader: string
  label: (item: T) => ReactNode
  primary: (item: T) => number
  secondary: (item: T) => number
  rowKey: (item: T) => string
  // Optional control in the card header (e.g. tabs).
  action?: ReactNode
}

export function TopList<T>(props: TopListProps<T>) {
  const { title, items, error, loading, refreshing, label, primary, secondary, rowKey, action } = props
  // The thin bars are scaled to the largest row, so the top row is always full width.
  const max = Math.max(1, ...(items ?? []).map(primary))

  return (
    <Card title={title} refreshing={refreshing} action={action}>
      <QueryState error={error} loading={loading} empty={items?.length === 0} />
      {items && items.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>{props.labelHeader}</th>
              <th className="num">{props.secondaryHeader}</th>
              <th className="num">{props.primaryHeader}</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => (
              <tr key={rowKey(item)}>
                <td className="row-label">
                  <span className="name">{label(item)}</span>
                  <span className="thin-bar" aria-hidden="true">
                    <span style={{ width: `${(primary(item) / max) * 100}%` }} />
                  </span>
                </td>
                <td className="num">{formatNumber(secondary(item))}</td>
                <td className="num primary">{formatNumber(primary(item))}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  )
}
