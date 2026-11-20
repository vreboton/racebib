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

export function localTime(instant: Date, zone: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
  }).formatToParts(instant)
  const fields = Object.fromEntries(parts.map(({ type, value }) => [type, value]))
  return `${fields.year}-${fields.month}-${fields.day}T${fields.hour}:${fields.minute}:${fields.second}`
}

export function scheduleOptions(value: string, zone: string) {
  const local = value.length === 16 ? value + ':00' : value
  const wall = Date.parse(local + 'Z')
  if (!Number.isFinite(wall)) return []
  const offsets = new Set<number>()
  for (const hours of [-36, 0, 36]) {
    const sample = wall + hours * 3600000
    offsets.add(Date.parse(localTime(new Date(sample), zone) + 'Z') - sample)
  }
  return Array.from(offsets).map(offset => new Date(wall - offset))
    .filter(candidate => localTime(candidate, zone) === local)
    .sort((a, b) => a.getTime() - b.getTime())
    .map(candidate => ({
      value: candidate.toISOString(),
      title: new Intl.DateTimeFormat('en', {
        timeZone: zone, hour: '2-digit', minute: '2-digit', timeZoneName: 'longOffset',
      }).format(candidate),
    }))
}
