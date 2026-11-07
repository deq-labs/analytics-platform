import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { sendJson } from './auth'

// Mirrors UserSummary in src/AnalyticsPlatform.Api/Auth/UserStore.cs.
export type UserRow = { id: number; email: string; isAdmin: boolean; sites: number; createdAt: string }

export function deleteWarning(user: UserRow): string {
  if (user.sites === 0) return `Deletes ${user.email}.`
  const sites = user.sites === 1 ? '1 site' : `${user.sites} sites`
  return `Deletes ${user.email}, their ${sites} and all of those sites’ analytics data.`
}

export function useUsers() {
  return useQuery({
    queryKey: ['admin-users'],
    queryFn: async () => (await (await sendJson('GET', '/api/admin/users')).json()) as UserRow[],
  })
}

export function useDeleteUser() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => sendJson('DELETE', `/api/admin/users/${id}`),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: ['admin-users'] }),
      queryClient.invalidateQueries({ queryKey: ['sites'] }),
    ]),
  })
}
