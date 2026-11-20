import { api } from './api'

export interface Page<T> { total: number; items: T[] }
export interface Overview {
  races: { raceId: string; name: string; timeZoneId: string; registered: number; acceptedVisits: number; recordedFinishes: number }[]
  actionable: number
  bibConflicts: number
}
export interface EvidenceRow {
  bibNumber: string
  registrationId?: string
  observationId?: string
  outcome?: string
  receivedAtUtc?: string
  decidedAtUtc?: string
  revision?: number
  classification?: string
  previousClassification?: string
  actorUserId?: string
  reason?: string
}
interface Cutoff { status: string }
interface Visit {
  occurrenceId: string; sequence: number; name: string; crossingAtUtc: string | null
  observationId: string | null; gunElapsedMicroseconds: number | null; netElapsedMicroseconds: number | null
  segmentMicroseconds: number | null; flags: string[]; cutoff: Cutoff | null
}
export interface RunnerRow {
  classification: string; revision: number; needsReview: boolean; bibConflict: boolean; warnings: string[]
  timing: {
    registrationId: string; bibNumber: string; visits: Visit[]; raceCutoff: Cutoff | null
    provisionalFinish: { gunElapsedMicroseconds: number | null; netElapsedMicroseconds: number | null } | null
    readiness: { missingOfficialGun: boolean; missingIndividualStart: boolean; missingMandatoryOccurrences: string[] }
  }
}
export interface Resolution {
  id: string; bibNumber: string; observedAtUtc: string; receivedAtUtc: string; acceptedOccurrenceId: string | null
  choices: { occurrenceId: string; sequence: number; name: string; effect: string; currentObservationId: string | null }[]
  decisions: { revision: number; outcome: string; reason: string; actorUserId: string; decidedAtUtc: string }[]
}

const base = (id: string) => `/events/${id}/timing`
const control = (id: string) => base(id) + '/control'
const runnerPath = (id: string, race: string) => control(id) + `/races/${race}/runners`

export const raceControlApi = {
  overview: (id: string) => api.get<Overview>(control(id)),
  exceptions: (id: string, kind: string, offset: number, limit: number) =>
    api.get<Page<EvidenceRow>>(control(id) + '/exceptions', { params: { kind, offset, limit } }),
  runners: (id: string, race: string, offset: number, limit: number) =>
    api.get<Page<RunnerRow>>(runnerPath(id, race), { params: { offset, limit } }),
  audit: (id: string, kind: string, offset: number, limit: number) =>
    api.get<Page<EvidenceRow>>(control(id) + '/audit', { params: { kind, offset, limit } }),
  resolution: (id: string, observation: string) =>
    api.get<Resolution>(control(id) + `/observations/${observation}/resolution`),
  resolve: (id: string, observation: string, occurrenceId: string | null, expectedRevision: number, reason: string) =>
    api.post(base(id) + `/observations/${observation}/resolve`, { occurrenceId, expectedRevision, reason }),
  classify: (id: string, race: string, registration: string, classification: number, expectedRevision: number, reason: string) =>
    api.post(runnerPath(id, race) + `/${registration}/classification`, { classification, expectedRevision, reason }),
}
