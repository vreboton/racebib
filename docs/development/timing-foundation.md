# Checkpoint timing foundation

Phase 6.1 is a backend contract for online timing. Operators use authenticated RaceBib accounts. Event Administrators and Race Directors configure timing and resolve evidence in their own organizer. Timing entry requires a location grant for every actor, including managers and platform administrators.

## Setup and capture

1. Configure each course in Race Configuration. Create a physical location for each station and map the existing course occurrences to it. Several occurrences, including start and finish, can share a location.
2. Grant active accounts access to a location. An account can operate without organizer membership. Each activation changes the grant generation. Repeat calls that leave the grant active keep its current generation.
3. Arm each ready or live race. Arming snapshots the course ID, ordered visits, start and finish, cutoff values, timezone, event window, race and wave schedules, and timing bases. It snapshots confirmed registrations with assigned bibs. Race Configuration already locks course/race edits from Ready onward. The timing mapping is also frozen for courses used by an armed race.
4. During Live, resolve a bib in the frozen timing roster and record the crossing. A runner's individual start is an accepted crossing of the start occurrence. Check-in is optional. Managers declare race/wave gun times independently of checkpoint entry.
5. To add an eligible registration after arming, a manager explicitly admits it to the timing roster with a reason. The existing registration workflow prepares the underlying registration. Existing frozen registrations and bib associations are reserved for the event. Ordinary registration writes reject changes to a frozen race, wave, or bib. Manual, CSV, and bulk allocation exclude frozen bibs even when the registration desk has a different value. Check-in and status transitions retain the reservation.

Fixed laps and out-and-back visits use distinct ordered course occurrence IDs. Open-ended laps require a separate design for defining subsequent occurrences, final-lap semantics, and slot identity. Phase 6.1 supports fixed routes only.

## HTTP contract

Event endpoints use `/api/v1/events/{eventId}/timing`. GUIDs are UUIDs. Enum input values follow the existing ASP.NET numeric convention. Timing basis is Gun (0, default) or Net (1). Observation source is Manual (0) or Scanner (1).

| Method and path | Request and response |
|---|---|
| POST `/checkpoints` | `{ name, code }` → 201 physical checkpoint. Codes are unique within an event. |
| PUT `/courses/{courseId}/occurrences/{occurrenceId}/checkpoint` | `{ checkpointId }` → 204. Identities must belong to the same event. |
| PUT `/checkpoints/{checkpointId}/assignments/{userId}` | `{ active }` → 200 with `active` and `generation`, or 204 when revoking a nonexistent grant. |
| POST `/races/{raceId}/arm` | `{ resultsBasis?, cutoffBasis? }` → 201 with race, course and roster count. Arming is one-time. |
| POST `/races/{raceId}/roster/{registrationId}` | `{ reason }` → 204 audited admission of a new confirmed registration with an available bib and frozen wave, if assigned. |
| POST `/races/{raceId}/gun-starts` | `{ waveId?, gunAtUtc, expectedDeclarationId?, reason }` → 201. Omit the expected ID for the first declaration. Use the returned ID for a replacement. |
| GET `/api/v1/me/timing-assignments` | Assigned locations, generation, event timezone, and whether the event is Live. This route is independent of the event prefix. |
| GET `/checkpoints/{checkpointId}/registrations?bib=0042` | Frozen registration, race, canonical bib, display name and the location's ordered occurrences for that course. |
| POST `/checkpoints/{checkpointId}/observations` | UUID `Idempotency-Key` header and payload below. Returns 201 for committed evidence and 200 for an authorized replay of the original receipt. |
| POST `/checkpoints/{checkpointId}/observations/{id}/confirm` | `{ occurrenceId, assignmentGeneration, expectedRevision }` → 200 decision receipt. Requires an active grant and an ambiguous latest decision. |
| POST `/observations/{id}/resolve` | Manager-only `{ occurrenceId?, expectedRevision, reason }` → 200. Null occurrence invalidates. A valid unoccupied visit can be accepted explicitly. Invalidate an existing accepted decision before attempting to replace it. |
| GET `/observations/{id}/history` | Manager-only raw observation and its ordered decisions. |

```json
{
  "observationId": "ddcbcc8f-4624-48db-900b-b833082d3254",
  "registrationId": "e0832eca-d719-4948-aea0-98c1da6485f2",
  "raceId": "b126c509-b5aa-4141-9f97-ed5a13ed9f2e",
  "bibNumber": "0042",
  "occurrenceId": null,
  "observedAtUtc": "2026-11-20T23:59:58.250Z",
  "deviceId": "59baf5c5-70d4-4524-9107-d475f8d205ad",
  "assignmentGeneration": "e4f2a4b5-ce46-4432-a7a6-87b210121958",
  "source": 0
}
```

A receipt contains the observation ID, registration ID, race ID, observed and receipt instants, decision revision, outcome and optional occurrence ID. Outcomes include Accepted, AmbiguousOccurrence, SlotConflict, SuspiciousClock, SequenceConflict and Invalidated. Use the same observation ID, operation key, device, grant generation and canonical payload for retries. A changed replay or stale decision returns 409. Unauthorized actors receive 401 or 403. An authorized request with unavailable scoped data can receive 404.

## Evidence, ordering and recovery

- Bib lookup and new capture return HTTP 409 with code `timing_bib_identity_conflict` if a different current registration in the same event owns a frozen bib. Both operations check under the event lock. If the live registrations have no owner for that bib, the frozen registration remains authoritative. Existing observations and authorized replays retain their original identity and receipt.

- UTC instants are stored separately at MySQL microsecond precision. Sub-microsecond crossing input is rejected. Values outside the armed window or more than two minutes ahead of server receipt are retained for manager review. Receipt time never replaces the observed time.
- A location with one occurrence can auto-accept after chronology and occupancy checks. A repeated location requires the occurrence in the submission or explicit operator confirmation. Operators cannot change accepted slots. Earlier evidence can be accepted after a later visit if its timestamp fits the frozen sequence. Receipt order does not drive traversal.
- Ordinary entry and revocation share the existing MySQL parent-event lock in a serializable transaction. Event lifecycle and role, account and organizer checks run inside that transaction. A replay rechecks the grant, including its generation. The database additionally guards observation ID, scoped operation key, and the single accepted registration/occurrence slot. A concurrent database conflict returns 409 and is retryable using the original operation identifiers.
- Observations, gun declarations, frozen setup, and decisions are append-only through the application. The database uses foreign keys and unique constraints. Privileged SQL administration still requires operational controls. TimingProjection.Reconstruct rebuilds current slots from each observation's latest decision and rejects conflicting acceptance or missing initial evidence. It has no dependency on arrival order or current projection data.
- The additive migration creates timing tables and reference keys on existing registration and checkpoint rows. No data backfill occurs. Staged locations and mappings are opt-in. After capture begins, preserve timing tables in any application rollback.

See [Timing and split calculation](timing-calculation.md) for Phase 6.2 start selection, calculation rules, and manager read endpoints.
