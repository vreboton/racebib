import { api } from './api'

export type OrganizerRole = 'EventAdministrator' | 'RaceDirector'
export interface Organizer { id: string; name: string; slug: string; isActive: boolean }
export interface Member { membershipId: string; userId: string; displayName: string | null; email: string | null; role: OrganizerRole; isActive: boolean; joinedAt: string }
export interface Invitation { id: string; email: string; role: OrganizerRole | 'CheckpointMarshal'; status: string; expiresAtUtc: string; createdAtUtc: string }
export interface Page<T> { items: T[]; page: number; pageSize: number; total: number }

export const organizerMembersApi = {
  organizers: () => api.get<Organizer[]>('/organizers'),
  members: (organizerId: string, params: { search?: string; page?: number; pageSize?: number }) => api.get<Page<Member>>(`/organizers/${organizerId}/members`, { params }),
  searchUsers: (organizerId: string, query: string) => api.get<Array<{ id: string; email: string | null; displayName: string | null }>>(`/organizers/${organizerId}/members/user-search`, { params: { query } }),
  addMember: (organizerId: string, email: string, role: OrganizerRole) => api.post<Member>(`/organizers/${organizerId}/members`, { email, role }),
  changeRole: (organizerId: string, membershipId: string, role: OrganizerRole) => api.put(`/organizers/${organizerId}/members/${membershipId}/role`, { role }),
  setStatus: (organizerId: string, membershipId: string, active: boolean) => api.put(`/organizers/${organizerId}/members/${membershipId}/status`, null, { params: { active } }),
  invitations: (organizerId: string) => api.get<Invitation[]>(`/organizers/${organizerId}/invitations`),
  invite: (organizerId: string, email: string, role: OrganizerRole | 'CheckpointMarshal') => api.post<{ invitation: Invitation; developmentAcceptanceUrl?: string | null }>(`/organizers/${organizerId}/invitations`, { email, role }),
  resend: (organizerId: string, id: string) => api.post<{ developmentAcceptanceUrl?: string | null }>(`/organizers/${organizerId}/invitations/${id}/resend`),
  revoke: (organizerId: string, id: string) => api.post(`/organizers/${organizerId}/invitations/${id}/revoke`),
}
