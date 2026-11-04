import { api } from './api'

export type EventStatus =
  | 'Draft' | 'Published' | 'RegistrationOpen' | 'RegistrationClosed'
  | 'Ready' | 'Live' | 'Completed' | 'Cancelled'

export interface Organizer {
  id: string
  name: string
  slug: string
  description?: string | null
  isActive?: boolean
}

export interface EventListItem {
  id: string
  organizerId: string
  name: string
  slug: string
  location?: string | null
  venue?: string | null
  startDate: string
  endDate: string
  timeZoneId: string
  status: EventStatus
}

export interface EventDetails extends EventListItem {
  description?: string | null
  organizerName: string
  createdAt: string
  updatedAt: string
}

export interface EventPage {
  items: EventListItem[]
  page: number
  pageSize: number
  total: number
  totalPages: number
}

export interface EventPayload {
  organizerId?: string
  name: string
  slug: string
  description: string | null
  organizerName: string
  venue: string | null
  location: string | null
  startDateUtc: string
  endDateUtc: string
  timeZoneId: string
}

export interface EventQuery {
  page?: number
  pageSize?: number
  search?: string
  status?: EventStatus | null
}

export const eventStatuses: EventStatus[] = [
  'Draft', 'Published', 'RegistrationOpen', 'RegistrationClosed',
  'Ready', 'Live', 'Completed', 'Cancelled',
]

export const eventStatusTransitions: Record<EventStatus, EventStatus[]> = {
  Draft: ['Published', 'Cancelled'],
  Published: ['Draft', 'RegistrationOpen', 'Cancelled'],
  RegistrationOpen: ['RegistrationClosed', 'Cancelled'],
  RegistrationClosed: ['RegistrationOpen', 'Ready', 'Cancelled'],
  Ready: ['Live', 'Cancelled'],
  Live: ['Completed', 'Cancelled'],
  Completed: [],
  Cancelled: [],
}

export const eventsApi = {
  async list(query: EventQuery) {
    const { data } = await api.get<EventPage>('/events', { params: query })
    return data
  },
  async get(id: string) {
    const { data } = await api.get<EventDetails>(`/events/${id}`)
    return data
  },
  async create(payload: EventPayload & { organizerId: string }) {
    const { data } = await api.post<EventDetails>('/events', payload)
    return data
  },
  async update(id: string, payload: EventPayload) {
    const { data } = await api.put<EventDetails>(`/events/${id}`, payload)
    return data
  },
  async changeStatus(id: string, status: EventStatus) {
    const { data } = await api.patch<{ id: string; status: EventStatus; updatedAt: string }>(
      `/events/${id}/status`, { status },
    )
    return data
  },
}

export async function loadOrganizers() {
  const { data } = await api.get<Organizer[]>('/organizers')
  return data
}

export function getApiError(error: unknown, fallback: string) {
  if (typeof error === 'object' && error !== null && 'response' in error) {
    const response = (error as { response?: { data?: { title?: string; detail?: string; errors?: Record<string, string[]> } } }).response
    const body = response?.data
    if (body?.title) return body.title
    if (body?.detail) return body.detail
    const validation = body?.errors
    if (validation) return Object.values(validation).flat().join(' ')
  }
  return error instanceof Error && error.message ? error.message : fallback
}
