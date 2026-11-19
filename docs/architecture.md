# RaceBib architecture

## Applications

1. Event Management System (Milestone 1).
2. Checkpoint Timing PWA (future, independently deployable, offline-first).
3. RFID Reader Application (future, independently deployable; implement only after the Checkpoint Timing PWA is proven reliable).

All timing clients will eventually submit observations through a common, versioned backend timing API. The timing PWA and RFID reader remain separate clients. No timing engine, offline sync implementation, live tracking, ETA engine, or RFID integration belongs in Milestone 1.

## Backend boundaries

- Domain: entities, enums and invariants; no framework dependencies.
- Application: use cases, contracts, DTOs, validation and authorization abstractions.
- Infrastructure: EF Core/MySQL, identity persistence, migrations and implementations.
- API: HTTP endpoints, authentication middleware, OpenAPI and composition root.

Avoid generic repositories. Use DbContext directly behind application service boundaries unless a concrete persistence abstraction provides value.

## Tenant model

Organizer is the tenant boundary. Users join organizers through OrganizerMembership, with a role scoped to that membership. SystemAdministrator is a platform-level role. Event, race, category, course, runner and registration access must be checked against the caller's organizer membership. Never trust an organizer ID supplied by a client without authorization.

## Event and race domain

The domain hierarchy is:

- Organizer owns Events.
- Event contains one or more Races.
- Each Race represents a distinct competitive offering (for example 7K, 21K, 42K or 100K), with its own distance, primary course version, start/cutoff rules and lifecycle.
- A Race may have multiple RaceCategories. A category is a competitive division such as Open, Male, Female, or an age group; it is not a distance.
- A Race may have multiple StartWaves. A wave represents a scheduled start grouping and is not a category.
- A Registration belongs to one Event and one Race, and may be assigned a RaceCategory and StartWave. It references a Runner and receives an event-unique bib number.
- A Runner may register for multiple races and events, subject to organizer-scoped access and privacy rules.
- Course versions are immutable once published or referenced by operational registrations/timing. A course version may be reused by multiple races only when the version is intentionally shared and remains unchanged.

Race owns the operational distance, start time, cutoff and primary CourseId. Do not duplicate those fields on RaceCategory or Registration. Registration should not independently choose a different course from its Race; the race's selected course version is authoritative for the initial release.

## Timing domain boundary (future)

Keep raw timing observations separate from computed results. Future concepts include TimingEvent (immutable observation), TimingSource/TimingDevice, audited TimingAdjustment, Result and ResultSplit. A raw observation must never be silently overwritten. Ingestion should be idempotent, preserve source/device identity and support delayed offline synchronization. Result calculations are derived and reproducible from observations plus the applicable race/course/rules configuration.

## Data rules

- Persist instants as UTC; use DateTimeOffset at API boundaries and convert to UTC. Display event-local times using the event's IANA time-zone identifier. Store and validate the time-zone identifier consistently.
- Use decimal for distances (kilometres) and integer seconds for durations.
- Enforce unique (EventId, BibNumber), unique (CourseId, Sequence), unique event slug per organizer, and same-event consistency across race, category, wave, course and registration references.
- Keep inactive records for auditability; use status fields rather than destructive deletion for operational records.
- Validate course checkpoints: one start and one finish, unique sequence/code per course, finish after start, and nondecreasing distance.
- Treat a published course version as immutable. Create a new version for route/checkpoint changes that could affect registrations or timing history.

## Security

Use ASP.NET Core Identity's sign-in workflow so configured lockout and failed-login policies are enforced. Keep only platform-level roles (such as SystemAdministrator) in global Identity roles; organizer duties belong to OrganizerMembership.Role. JWT access tokens are short-lived. If refresh tokens are introduced, persist only their hashes and support rotation/revocation; do not claim a refresh-token workflow until it exists. Passwords use ASP.NET Core Identity's password hasher. Secrets are environment supplied.

## Milestone 1 scope

Milestone 1 establishes event setup and administration only: organizer tenancy, event/race/category/wave/course/checkpoint configuration, runner records, registrations, authentication and administrative UI. The checkpoint timing PWA, offline synchronization, RFID reader, raw timing ingestion, split calculations and public live tracking remain future milestones.


[Runner registration architecture and CSV workflow](runner-registration.md)
