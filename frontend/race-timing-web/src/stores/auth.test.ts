import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../services/api'
import { mockSessionStorage } from '../test/storage'
import { useAuthStore } from './auth'

vi.mock('../services/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }))

const tokenKey = 'racebib.accessToken'
const profile = {
  id: 'user-1',
  email: 'admin@example.test',
  displayName: 'Organizer Admin',
  roles: [],
  organizerMemberships: [{ organizerId: 'org-1', organizerName: 'Trail Club', role: 'EventAdministrator' }],
}

describe('authentication initialization', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('loads the current user for a token restored from session storage', async () => {
    const { storage } = mockSessionStorage({ [tokenKey]: 'saved-access-token' })
    vi.mocked(api.get).mockResolvedValueOnce({ data: profile } as never)
    const auth = useAuthStore()

    await auth.initialize()

    expect(api.get).toHaveBeenCalledWith('/auth/me')
    expect(auth.token).toBe('saved-access-token')
    expect(auth.user).toEqual(profile)
    expect(auth.initialized).toBe(true)
    expect(storage.getItem).toHaveBeenCalledWith(tokenKey)
  })

  it('marks an empty session initialized without requesting a user profile', async () => {
    mockSessionStorage()
    const auth = useAuthStore()

    await auth.initialize()

    expect(api.get).not.toHaveBeenCalled()
    expect(auth.isAuthenticated).toBe(false)
    expect(auth.initialized).toBe(true)
  })
})
