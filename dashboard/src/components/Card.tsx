import type { ReactNode } from 'react'
import { ApiError } from '../api'

type CardProps = {
  title: string
  className?: string
  refreshing?: boolean
  // Rendered on the right of the header (e.g. a "Table" toggle).
  action?: ReactNode
  // Optional headline figure under the title.
  figure?: ReactNode
  children: ReactNode
}

// The frame every dashboard view sits in.
export function Card({ title, className = '', refreshing = false, action, figure, children }: CardProps) {
  return (
    <section className={`card ${refreshing ? 'refreshing' : ''} ${className}`} aria-busy={refreshing}>
      <div className="card-head">
        <div>
          <h2>{title}</h2>
          {figure}
        </div>
        {action}
      </div>
      {children}
    </section>
  )
}

// Loading / error / empty messages, shared by all cards.
export function QueryState({ error, loading, empty }: { error: unknown; loading: boolean; empty?: boolean }) {
  if (error) {
    const notRunning = error instanceof ApiError && (error.status === 0 || error.status >= 502)
    return (
      <p className="message error" role="alert">
        <strong>{error instanceof Error ? error.message : 'Something went wrong.'}</strong>
        {notRunning && <> Start it in Visual Studio (F5), then refresh.</>}
      </p>
    )
  }
  if (loading) return <p className="message">Loading…</p>
  if (empty) return <p className="message">No data for this period.</p>
  return null
}
