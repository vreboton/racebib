import axios from 'axios'

export const api = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080/api/v1',
  headers: { 'Content-Type': 'application/json' },
  timeout: 15000,
})

api.interceptors.request.use((config) => {
  const token = sessionStorage.getItem('racebib.accessToken')
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})
