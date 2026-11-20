# ADR 001: Separate races, competitive categories and start waves

- Status: Accepted
- Date: 2026-10-30

## Context

The initial RaceCategory entity represented distance, course assignment, start time and cutoff duration. Those properties describe a race offering, not a competitive category. Keeping these concepts together would make it difficult to support multiple competitive divisions and staggered starts within one distance, and would blur the distinction between a race and a category in registration and results.

## Decision

Adopt this hierarchy:

`Organizer -> Event -> Race -> (RaceCategory, StartWave)`

A Race represents a distinct competitive offering (for example 7K, 21K, 42K or 100K). It owns the operational distance, primary course version, start time and cutoff rules.

RaceCategory represents a competitive division within a Race (for example Open, Male, Female, or an age group). It does not own distance, course, start time or race cutoff.

StartWave represents a scheduled start grouping within a Race. It is independent of category.

Registration belongs to an Event and Race, references a Runner, and may reference a RaceCategory and StartWave. Bib number uniqueness remains scoped to the Event. The Race's primary course version is authoritative for the registration in the initial release.

A Runner may have registrations in multiple races and events. Organizer access controls govern access to runner data.

Course versions may be shared intentionally, but a published or operationally referenced version is immutable. Route changes create a new version.

## Consequences

- The existing RaceCategory model must be refactored; its distance, course, start and cutoff fields move to Race.
- Add Race and StartWave entities and their EF Core relationships.
- Registration changes from RaceCategoryId/CourseId to RaceId plus optional RaceCategoryId and StartWaveId.
- Event API/UI and development acceptance criteria must use races as the primary distance offering.
- The schema and API are not considered stable until the refactor, migrations and tests are complete.
- Historical timing observations and computed results will be separate future concepts; this decision does not implement timing functionality.

## Amendment: Managed registration category

Race is the operational selection for registration. The managed workflow uses one active registration category per race and resolves it automatically. If no category is active, a successful configuration save or new assignment provisions an unrestricted category under the event lock. An unreferenced, unrestricted retired Open with no custom description may be reactivated. If the Open name belongs to a historical or customized category, provisioning uses a separate managed Open name and retains the original row and status.

The original category and wave schema remains available for compatibility. Existing same-race assignments, including historical category links or null, are preserved when no replacement is requested. Multiple active legacy categories still require explicit selection for new assignments. Start waves remain independent, and course revisions keep their existing identity and protections.

The managed workflow permits one registration per runner per event. Reassignment changes the existing registration's race, and pre-existing duplicate registrations remain intact.
