import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../services/api'
import { pinia } from '../stores'
import { useAuthStore } from '../stores/auth'
import { mockSessionStorage } from '../test/storage'
import router from './index'

vi.mock('../services/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }))

describe('protected route access', () => {
  const auth = useAuthStore(pinia)

  beforeEach(async () => {
    mockSessionStorage()
    auth.clearSession()
    auth.initialized = false
    await router.replace('/login')
    vi.mocked(api.get).mockReset()
  })

  it('redirects an unauthenticated user to sign-in and preserves the target route', async () => {
    await router.push('/events')

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/events')
  })

  it('denies an organizer administrator access to platform administration', async () => {
    auth.token = 'organizer-token'
    auth.initialized = true
    auth.user = {
      id: 'organizer-admin',
      email: 'organizer@example.test',
      displayName: 'Organizer Admin',
      roles: [],
      organizerMemberships: [{ organizerId: 'org-1', organizerName: 'Trail Club', role: 'EventAdministrator' }],
    }

    await router.push('/platform')

    expect(router.currentRoute.value.name).toBe('dashboard')
    expect(router.currentRoute.value.query.accessDenied).toBe('platform')
  })
})
