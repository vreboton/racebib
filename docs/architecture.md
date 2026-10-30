# RaceBib architecture

## Applications

1. Event Management System (this repository's Milestone 1 deliverable).
2. Checkpoint Timing PWA (future, independently deployable).
3. RFID Reader Application (future, independently deployable and only after the PWA is proven reliable).

All timing clients will eventually submit observations through a common backend timing API. No timing engine, offline sync, live tracking, ETA engine, or RFID integration belongs in Milestone 1.

## Backend boundaries

- Domain: entities, enums, invariants; no framework dependencies.
- Application: use cases, contracts, DTOs, validation and authorization abstractions.
- Infrastructure: EF Core/MySQL, identity persistence, migrations and implementations.
- API: HTTP endpoints, authentication middleware, OpenAPI and composition root.

Avoid generic repositories. Use DbContext directly behind application service boundaries unless a concrete persistence abstraction provides value.

## Tenant model

Organizer is the tenant boundary. Users join organizers through OrganizerMembership, with a role scoped to that membership. SystemAdministrator is a platform-level role. Event, category, course and registration access must be checked against the caller's organizer membership. Never trust an organizer ID supplied by a client without authorization.

## Data rules

- Persist timestamps as UTC; display event-local times using the event's IANA/Windows time-zone identifier resolved by .NET TimeZoneInfo.
- Use decimal for distances (kilometres) and integer seconds for durations.
- Enforce unique (EventId, BibNumber), unique (CourseId, Sequence), unique event slug, and same-event category/course registration consistency.
- Keep inactive records for auditability; use status fields rather than destructive deletion for operational records.
- A registration's EventId, RaceCategory.EventId and Course.EventId must match. Enforce in application validation and with composite foreign keys where practical.

## Security

JWT access tokens are short-lived. Persist refresh/session token hashes (never raw tokens) so logout can revoke the current session. Passwords use ASP.NET Core Identity's password hasher. Secrets are environment supplied.

## Future timing boundary

Future timing clients will authenticate as scoped service/user identities and POST observations to a versioned timing API. Timing observations must be idempotent and preserve source/device identity. That contract is intentionally not implemented in Milestone 1.
