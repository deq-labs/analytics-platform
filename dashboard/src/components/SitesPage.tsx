import { useState, type FormEvent } from 'react'
import type { Site } from '../api'
import type { User } from '../auth'
import {
  STANDARD_EVENTS,
  changedSettings,
  inviteLink,
  snippetFor,
  useCreateInvite,
  useCreateSite,
  useDeleteSite,
  useRegenerateTrackingId,
  useSiteDetails,
  useUpdateSite,
  type SiteDetails,
} from '../sites'
import { Card, QueryState } from './Card'
import { ApiKeysCard } from './ApiKeysCard'
import { CopyButton } from './CopyButton'
import { UsersCard } from './UsersCard'

// The "Settings" view: add sites, set each one up, and (admins) invite people.
export function SitesPage({
  user,
  sites,
  siteId,
  onSelectSite,
}: {
  user: User
  sites: Site[] | undefined
  siteId: number | undefined
  onSelectSite: (id: number | undefined) => void
}) {
  return (
    <div className="settings">
      {siteId !== undefined && <SiteSettings key={siteId} siteId={siteId} onDeleted={() => onSelectSite(undefined)} />}
      <AddSite onCreated={onSelectSite} first={sites?.length === 0} />
      <ApiKeysCard siteId={siteId} />
      {user.isAdmin && <Invites />}
      {user.isAdmin && <UsersCard me={user} />}
    </div>
  )
}

// ---- Add a site ----

function AddSite({ onCreated, first }: { onCreated: (id: number) => void; first: boolean }) {
  const create = useCreateSite()
  const [name, setName] = useState('')
  const [domain, setDomain] = useState('')

  function submit(e: FormEvent) {
    e.preventDefault()
    create.mutate(
      { name, domain },
      {
        onSuccess: (site) => {
          setName('')
          setDomain('')
          if (site) onCreated(site.id)
        },
      },
    )
  }

  return (
    <Card title={first ? 'Add your first site' : 'Add a site'}>
      <form className="form inline" onSubmit={submit}>
        <label>
          Name
          <input required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} placeholder="My blog" />
        </label>
        <label>
          Domain
          <input required value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="example.com" />
        </label>
        <button className="primary" type="submit" disabled={create.isPending}>
          {create.isPending ? 'Adding…' : 'Add site'}
        </button>
      </form>
      <p className="hint">Subdomains and www. are included automatically.</p>
      {create.error && <p className="message error" role="alert">{create.error.message}</p>}
    </Card>
  )
}

// ---- One site's settings ----

function SiteSettings({ siteId, onDeleted }: { siteId: number; onDeleted: () => void }) {
  const details = useSiteDetails(siteId)
  if (!details.data) {
    return (
      <Card title="Site settings">
        <QueryState error={details.error} loading={details.isPending} />
      </Card>
    )
  }
  return (
    <>
      <Snippet site={details.data} />
      <SettingsForm site={details.data} />
      <DangerZone site={details.data} onDeleted={onDeleted} />
    </>
  )
}

function Snippet({ site }: { site: SiteDetails }) {
  const regenerate = useRegenerateTrackingId(site.id)
  const [confirming, setConfirming] = useState(false)
  const snippet = snippetFor(location.origin, site.trackingId)

  return (
    <Card title="Tracking code" action={<CopyButton text={snippet} label="Copy" />}>
      <p className="hint">
        Paste this into the <code>&lt;head&gt;</code> of every page on <strong>{site.domain}</strong>.
      </p>
      <pre className="code">{snippet}</pre>
      <div className="row">
        <span className="hint">
          Tracking ID <code>{site.trackingId}</code> — public, it identifies the site but grants no access.
        </span>
        {confirming ? (
          <span className="row tight">
            <span className="hint">The old code stops working at once.</span>
            <button className="danger" onClick={() => regenerate.mutate(undefined, { onSettled: () => setConfirming(false) })}>
              Replace ID
            </button>
            <button className="link-button" onClick={() => setConfirming(false)}>Cancel</button>
          </span>
        ) : (
          <button className="link-button" onClick={() => setConfirming(true)}>New tracking ID…</button>
        )}
      </div>
      {regenerate.error && <p className="message error" role="alert">{regenerate.error.message}</p>}
    </Card>
  )
}

function SettingsForm({ site }: { site: SiteDetails }) {
  const update = useUpdateSite(site.id)
  const [edited, setEdited] = useState(site)
  const [saved, setSaved] = useState(false)

  const changes = changedSettings(site, edited)
  const dirty = Object.keys(changes).length > 0

  function submit(e: FormEvent) {
    e.preventDefault()
    update.mutate(changes, {
      onSuccess: (updated) => {
        if (updated) setEdited(updated) // start from what the server now has
        setSaved(true)
      },
    })
  }

  return (
    <Card title="Settings">
      <form className="form" onSubmit={submit} onChange={() => setSaved(false)}>
        <label>
          Name
          <input required maxLength={100} value={edited.name} onChange={(e) => setEdited({ ...edited, name: e.target.value })} />
        </label>

        <fieldset>
          <legend>Visitor identification</legend>
          <label className="choice">
            <input
              type="radio"
              name="mode"
              checked={edited.visitorIdMode === 'hash'}
              onChange={() => setEdited({ ...edited, visitorIdMode: 'hash' })}
            />
            <span>
              <strong>Cookie-less</strong> (recommended)
              <span className="hint block">Nothing is stored on the visitor's device. A returning visitor counts as new each day.</span>
            </span>
          </label>
          <label className="choice">
            <input
              type="radio"
              name="mode"
              checked={edited.visitorIdMode === 'persistent'}
              onChange={() => setEdited({ ...edited, visitorIdMode: 'persistent' })}
            />
            <span>
              <strong>Persistent ID</strong>
              <span className="hint block">
                A random ID in the browser's storage: accurate returning visitors. You may need visitors' consent.
              </span>
            </span>
          </label>
        </fieldset>

        <fieldset>
          <legend>Automatically tracked events</legend>
          <div className="checks">
            {STANDARD_EVENTS.map((event) => (
              <label className="choice" key={event.key}>
                <input
                  type="checkbox"
                  checked={edited.standardEvents[event.key] ?? false}
                  onChange={(e) =>
                    setEdited({ ...edited, standardEvents: { ...edited.standardEvents, [event.key]: e.target.checked } })
                  }
                />
                <span>
                  {event.label}
                  {event.hint && <span className="hint block">{event.hint}</span>}
                </span>
              </label>
            ))}
          </div>
        </fieldset>

        <div className="row start">
          <button className="primary" type="submit" disabled={!dirty || update.isPending}>
            {update.isPending ? 'Saving…' : 'Save changes'}
          </button>
          {saved && !dirty && <span className="hint" role="status">Saved.</span>}
        </div>
        {update.error && <p className="message error" role="alert">{update.error.message}</p>}
      </form>
    </Card>
  )
}

// Deleting asks you to type the domain -- a deliberate speed bump for
// something that can't be undone (it also deletes every event).
function DangerZone({ site, onDeleted }: { site: SiteDetails; onDeleted: () => void }) {
  const remove = useDeleteSite(site.id)
  const [typed, setTyped] = useState('')

  return (
    <Card title="Delete site" className="danger-card">
      <p className="hint">
        Permanently deletes <strong>{site.name}</strong> and <strong>all of its analytics data</strong>. This can't be undone.
      </p>
      <form
        className="form inline two"
        onSubmit={(e) => {
          e.preventDefault()
          remove.mutate(undefined, { onSuccess: onDeleted })
        }}
      >
        <label>
          <span>
            Type <code>{site.domain}</code> to confirm
          </span>
          <input value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" />
        </label>
        <button className="danger" type="submit" disabled={typed.trim() !== site.domain || remove.isPending}>
          {remove.isPending ? 'Deleting…' : 'Delete site'}
        </button>
      </form>
      {remove.error && <p className="message error" role="alert">{remove.error.message}</p>}
    </Card>
  )
}

// ---- Invites (admin only) ----

function Invites() {
  const create = useCreateInvite()
  const link = create.data ? inviteLink(location.origin, create.data.token) : null

  return (
    <Card title="Invite someone">
      <p className="hint">
        Creates a one-time sign-up link that expires in 7 days. Send it to the person yourself; it's shown only once.
      </p>
      {link ? (
        <>
          <pre className="code">{link}</pre>
          <div className="row">
            <CopyButton text={link} label="Copy link" />
            <span className="hint">Expires {new Date(create.data!.expiresAt).toLocaleString()}</span>
            <button className="link-button" onClick={() => create.mutate()}>Create another</button>
          </div>
        </>
      ) : (
        <button className="primary" onClick={() => create.mutate()} disabled={create.isPending}>
          Create invite link
        </button>
      )}
      {create.error && <p className="message error" role="alert">{create.error.message}</p>}
    </Card>
  )
}

