import { useState, type FormEvent } from 'react'
import { curlExample, useApiKeys, useCreateApiKey, useDeleteApiKey } from '../apiKeys'
import { Card, QueryState } from './Card'
import { CopyButton } from './CopyButton'

// Secret keys for reading stats from a server. A new key is shown exactly
// once -- only its hash is stored, so it can't be shown again later.
export function ApiKeysCard({ siteId }: { siteId: number | undefined }) {
  const keys = useApiKeys()
  const create = useCreateApiKey()
  const remove = useDeleteApiKey()
  const [name, setName] = useState('')
  const [confirmId, setConfirmId] = useState<number | null>(null)

  function submit(e: FormEvent) {
    e.preventDefault()
    create.mutate(name, { onSuccess: () => setName('') })
  }

  return (
    <Card title="API keys">
      <p className="hint">
        Read your sites' stats from a script or another server. Keys are read-only and secret — treat them like passwords.
      </p>

      {create.data && (
        <div className="new-key" role="status">
          <p className="hint">
            <strong>Copy your new key now.</strong> It won't be shown again.
          </p>
          <pre className="code">{create.data.key}</pre>
          <div className="row">
            <CopyButton text={create.data.key} label="Copy key" />
            {siteId !== undefined && (
              <CopyButton text={curlExample(location.origin, siteId, create.data.key)} label="Copy curl example" />
            )}
          </div>
        </div>
      )}

      <QueryState error={keys.error} loading={keys.isPending} />
      {keys.data && keys.data.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Key</th>
              <th>Last used</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {keys.data.map((key) => (
              <tr key={key.id}>
                <td>{key.name}</td>
                <td><code>{key.prefix}…</code></td>
                <td className="muted">{key.lastUsedAt ? new Date(key.lastUsedAt).toLocaleString() : 'Never'}</td>
                <td className="num">
                  {confirmId === key.id ? (
                    <span className="row tight">
                      <button className="danger" onClick={() => remove.mutate(key.id, { onSettled: () => setConfirmId(null) })}>
                        Revoke
                      </button>
                      <button className="link-button" onClick={() => setConfirmId(null)}>Cancel</button>
                    </span>
                  ) : (
                    <button className="link-button" onClick={() => setConfirmId(key.id)}>Revoke…</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <form className="form inline two" onSubmit={submit}>
        <label>
          New key name
          <input required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} placeholder="Weekly report" />
        </label>
        <button className="primary" type="submit" disabled={create.isPending}>
          {create.isPending ? 'Creating…' : 'Create key'}
        </button>
      </form>
      {(create.error ?? remove.error) && (
        <p className="message error" role="alert">{(create.error ?? remove.error)!.message}</p>
      )}
    </Card>
  )
}
