import { api } from './api'

export interface Checkpoint {
  id?: string
  name: string
  code: string
  sequence: number
  distanceFromStartKm: number
  elevationMeters: number | null
  isMandatory: boolean
  isStart: boolean
  isFinish: boolean
  hasCutoff: boolean
  cutoffDurationSeconds: number | null
  description: string | null
}

interface Entry {
  id: string
  name: string
  status: 'Active' | 'Inactive'
}

export interface Course extends Entry {
  version: number
  description: string | null
  distanceKm: number
  elevationGainMeters: number | null
  immutable: boolean
  editBlock: 'historical' | 'registrations' | 'inactive' | null
  routeEditBlock?: 'historical' | 'lifecycle' | 'inactive' | null
  usable: boolean
  hasActiveRaces: boolean
  canDelete: boolean
  affectedRegistrationCount: number
  registrationConfirmation: string | null
  checkpoints: Checkpoint[]
}

export interface Category extends Entry {
  hasRegistrations: boolean
  description: string | null
  minimumAge: number | null
  maximumAge: number | null
}

export interface Wave extends Entry {
  hasRegistrations: boolean
  startAt: string
  sequence: number
  capacity: number | null
}

export interface Race extends Entry {
  revisionIds: string[]
  courseId: string
  distanceKm: number
  description: string | null
  startAt: string
  cutoffDurationSeconds: number | null
  categories: Category[]
  waves: Wave[]
}

export interface Configuration {
  timeZoneId: string
  canEdit: boolean
  courses: Course[]
  races: Race[]
}

export async function loadConfiguration(eventId: string) {
  const { data } = await api.get<Configuration>(`/events/${eventId}/configuration`)
  return data
}

export async function saveConfiguration<T = void>(eventId: string, path: string,
  method: 'post' | 'put' | 'patch' | 'delete', data?: unknown) {
  const response = await api.request<T>({ method, url: `/events/${eventId}/${path}`, data })
  return response.data
}
