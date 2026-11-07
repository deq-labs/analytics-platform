import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

// Mirrors the C# records in src/AnalyticsPlatform.Api/Auth/AuthEndpoints.cs.
export type User = { id: number; email: string; isAdmin: boolean }
export type AuthStatus = { setupRequired: boolean; openRegistration: boolean }
export type Screen = 'setup' | 'login' | 'register'

// Which sign-in screen to show. First run always means "create the admin";
// an invite link (?invite=...) opens sign-up; otherwise log in.
export function authScreen(status: AuthStatus, inviteToken: string | null): Screen {
  if (status.setupRequired) return 'setup'
  if (inviteToken) return 'register'
  return 'login'
}

export const inviteTokenFromUrl = () => new URLSearchParams(location.search).get('invite')

// The API answers errors as ProblemDetails / ValidationProblem JSON. Turn
// either into one readable message.
async function errorMessage(response: Response): Promise<string> {
  try {
    const body = await response.json()
    if (body.errors) return Object.values(body.errors as Record<string, string[]>).flat().join(' ')
    if (body.detail) return body.detail
    if (body.title) return body.title
  } catch {
    /* not JSON */
  }
  if (response.status === 429) return 'Too many attempts. Wait a minute and try again.'
  return `Something went wrong (${response.status}).`
}

async function postJson(path: string, body?: unknown): Promise<Response> {
  const response = await fetch(path, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!response.ok) throw new Error(await errorMessage(response))
  return response
}

// ---- Queries ----

export function useAuthStatus() {
  return useQuery({
    queryKey: ['auth', 'status'],
    queryFn: async () => (await (await fetch('/api/auth/status')).json()) as AuthStatus,
  })
}

// The signed-in user, or null when not signed in (401 is an answer, not an error).
export function useMe() {
  return useQuery({
    queryKey: ['auth', 'me'],
    queryFn: async (): Promise<User | null> => {
      const response = await fetch('/api/auth/me')
      if (response.status === 401) return null
      if (!response.ok) throw new Error(`The API returned ${response.status}.`)
      return (await response.json()) as User
    },
    retry: false,
  })
}

// ---- Mutations: actions that change server state ----
// After each one, forget every cached query so all data refetches as the new
// user (and nothing from the previous session lingers on screen).

export function useLogin() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: { email: string; password: string }) => postJson('/api/auth/login', input),
    onSuccess: () => queryClient.resetQueries(),
  })
}

export function useRegister() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: { email: string; password: string; inviteToken: string | null }) =>
      postJson('/api/auth/register', input),
    onSuccess: () => {
      history.replaceState(null, '', location.pathname) // drop the used ?invite= from the URL
      return queryClient.resetQueries()
    },
  })
}

export function useLogout() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => postJson('/api/auth/logout'),
    onSuccess: () => queryClient.resetQueries(),
  })
}
