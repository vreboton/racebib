import { api } from './api'

export interface Page<T> { items: T[]; pageNumber: number; pageSize: number; totalCount: number }
export interface PlatformUser { id: string; displayName: string | null; email: string | null; isActive: boolean; isSystemAdministrator: boolean; lockoutEnd: string | null }
export interface PlatformOrganizer { id: string; name: string; slug: string; description: string | null; status: 'Active' | 'Suspended' | 'Inactive'; createdAt: string; memberCount: number; eventCount: number }
export interface AuditRecord { id: string; actorUserId: string | null; actorIdentity: string | null; action: string; targetEntityType: string; targetEntityId: string; organizerId: string | null; timestampUtc: string; metadataJson: string | null }

export const platformAdminApi = {
  dashboard: () => api.get('/platform/dashboard'),
  users: (params: { search?: string; page?: number; pageSize?: number }) => api.get<Page<PlatformUser>>('/platform/users', { params }),
  userDetails: (id: string) => api.get(`/platform/users/${id}`),
  setPlatformAdmin: (id: string, isAdministrator: boolean) => api.put(`/platform/users/${id}/platform-admin`, { isAdministrator }),
  setUserStatus: (id: string, isActive: boolean) => api.put(`/platform/users/${id}/status`, { isActive }),
  organizers: (params: { search?: string; page?: number; pageSize?: number }) => api.get<Page<PlatformOrganizer>>('/platform/organizers', { params }),
  organizerDetails: (id: string) => api.get(`/platform/organizers/${id}`),
  organizerMembers: (id: string) => api.get(`/platform/organizers/${id}/members`),
  organizerEvents: (id: string) => api.get(`/platform/organizers/${id}/events`),
  setOrganizerStatus: (id: string, status: 'Active' | 'Suspended' | 'Inactive') => api.put(`/platform/organizers/${id}/status`, { status }),
  audit: (params: Record<string, string | number | undefined>) => api.get<Page<AuditRecord>>('/platform/audit', { params }),
  organizerAudit: (id: string, params: Record<string, string | number | undefined>) => api.get<Page<AuditRecord>>(`/platform/organizers/${id}/audit`, { params }),
}
