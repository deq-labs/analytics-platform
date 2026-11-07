import { useState } from 'react'
import { deleteWarning, useDeleteUser, useUsers } from '../admin'
import type { User } from '../auth'
import { Card, QueryState } from './Card'

// Admin only: everyone with an account. Deleting someone ends their session
// immediately and deletes their sites and data.
export function UsersCard({ me }: { me: User }) {
  const users = useUsers()
  const remove = useDeleteUser()
  const [confirmId, setConfirmId] = useState<number | null>(null)
  const confirming = users.data?.find((u) => u.id === confirmId)

  return (
    <Card title="Users">
      <QueryState error={users.error} loading={users.isPending} />
      {users.data && (
        <table>
          <thead>
            <tr>
              <th>Email</th>
              <th>Joined</th>
              <th className="num">Sites</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {users.data.map((user) => (
              <tr key={user.id}>
                <td>
                  {user.email}
                  {user.isAdmin && <span className="muted"> · admin</span>}
                  {user.id === me.id && <span className="muted"> · you</span>}
                </td>
                <td className="muted">{new Date(user.createdAt).toLocaleDateString()}</td>
                <td className="num">{user.sites}</td>
                <td className="num">
                  {user.id !== me.id && (
                    <button className="link-button" onClick={() => setConfirmId(user.id)}>Delete…</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {confirming && (
        <div className="confirm" role="alertdialog" aria-label="Confirm delete">
          <p className="hint"><strong>{deleteWarning(confirming)}</strong> This can't be undone.</p>
          <div className="row tight">
            <button className="link-button" onClick={() => setConfirmId(null)}>Cancel</button>
            <button
              className="danger"
              disabled={remove.isPending}
              onClick={() => remove.mutate(confirming.id, { onSuccess: () => setConfirmId(null) })}
            >
              Delete user
            </button>
          </div>
        </div>
      )}
      {remove.error && <p className="message error" role="alert">{remove.error.message}</p>}
    </Card>
  )
}
