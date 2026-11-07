import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { sendJson } from './auth'

// Mirrors the records in src/AnalyticsPlatform.Api/Auth/ApiKeys.cs.
export type ApiKey = { id: number; name: string; prefix: string; createdAt: string; lastUsedAt: string | null }
export type CreatedApiKey = ApiKey & { key: string }

// A copy-paste example of reading stats from a server with a key.
export function curlExample(origin: string, siteId: number, key = 'apk_YOUR_KEY'): string {
  const base = origin.replace(/\/+$/, '')
  return `curl -H "Authorization: Bearer ${key}" "${base}/api/sites/${siteId}/stats/top-pages"`
}

export function useApiKeys() {
  return useQuery({
    queryKey: ['api-keys'],
    queryFn: async () => (await (await sendJson('GET', '/api/keys')).json()) as ApiKey[],
  })
}

export function useCreateApiKey() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (name: string) => (await (await sendJson('POST', '/api/keys', { name })).json()) as CreatedApiKey,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['api-keys'] }),
  })
}

export function useDeleteApiKey() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => sendJson('DELETE', `/api/keys/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['api-keys'] }),
  })
}
