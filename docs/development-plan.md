# Milestone 1 development plan

## Agreed domain model

1. Organizer owns Events.
2. Event contains multiple Races.
3. Race represents a distance/competitive offering and owns its primary Course version, start time and cutoff rules.
4. Race has multiple RaceCategories (competitive divisions such as Open, Male/Female or age groups).
5. Race has multiple StartWaves (scheduled start groupings).
6. Registration belongs to Event and Race, references Runner, and may be assigned a RaceCategory and StartWave. Bib numbers are unique within an Event.
7. A Runner may register for multiple races/events.
8. Course versions are immutable once published or used operationally; a Race selects one primary version.

## Sequence

1. Solution and frontend scaffolding (present).
2. Refactor domain model to the agreed Event > Race > Category/Wave structure; clarify course-version ownership and registration relationships.
3. Configure EF Core relationships, constraints and initial migration. Do not hand-edit generated migration snapshots.
4. Correct Identity sign-in/lockout behavior and enforce organizer-scoped authorization.
5. Implement event, race, category, wave, course, checkpoint, runner and registration APIs.
6. Build Vuetify administration screens and connect them to the APIs.
7. Harden Docker Compose, health checks, environment documentation and deployment configuration.
8. Add automated domain, API, authorization and end-to-end tests.

## Acceptance workflow

- Sign in as a seeded development administrator.
- Create an organizer and invite/assign organizer members.
- Create an event and move it through valid lifecycle transitions.
- Add multiple races to an event (for example 7K, 21K and 42K).
- Assign one primary course version to each race.
- Add competitive categories and start waves independently.
- Configure ordered start/intermediate/finish checkpoints for each course version.
- Create runners and register them in a race; assign category and wave where applicable.
- Assign event-unique bib numbers.
- Verify duplicate bibs, cross-event references, invalid course versions and unauthorized organizer access are rejected.
- Verify published/used course versions cannot be modified in place.

## Out of scope

Checkpoint timing PWA, offline queue/synchronization, QR timing capture, RFID reader, raw timing ingestion, split calculations, ETA predictions, public live tracking, automated cutoffs and missing-runner alerts.
