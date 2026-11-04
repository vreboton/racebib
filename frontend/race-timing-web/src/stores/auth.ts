import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { isAxiosError } from 'axios'
import { api } from '../services/api'

export interface AuthUser {
  id: string
  email: string
  displayName: string
  roles: string[]
}

interface LoginResponse {
  accessToken: string
  expiresAtUtc: string
  user: AuthUser
}

const TOKEN_KEY = 'racebib.accessToken'

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string | null>(sessionStorage.getItem(TOKEN_KEY))
  const user = ref<AuthUser | null>(null)
  const initialized = ref(false)
  const isAuthenticated = computed(() => Boolean(token.value))

  function clearSession() {
    token.value = null
    user.value = null
    sessionStorage.removeItem(TOKEN_KEY)
  }

  async function login(email: string, password: string) {
    const response = await api.post<LoginResponse>('/auth/login', { email, password })
    token.value = response.data.accessToken
    user.value = response.data.user
    sessionStorage.setItem(TOKEN_KEY, response.data.accessToken)
    initialized.value = true
  }

  async function loadUser() {
    if (!token.value) {
      clearSession()
      return
    }

    try {
      const response = await api.get<AuthUser>('/auth/me')
      user.value = response.data
    } catch (error) {
      clearSession()
      throw error
    } finally {
      initialized.value = true
    }
  }

  async function initialize() {
    if (initialized.value) return
    if (!token.value) {
      initialized.value = true
      return
    }

    try {
      await loadUser()
    } catch {
      // An invalid or expired session is cleared by loadUser.
    }
  }

  async function logout() {
    try {
      if (token.value) await api.post('/auth/logout')
    } catch (error) {
      if (!isAxiosError(error)) throw error
    } finally {
      clearSession()
      initialized.value = true
    }
  }

  return {
    token,
    user,
    initialized,
    isAuthenticated,
    login,
    loadUser,
    initialize,
    logout,
    clearSession,
  }
})
