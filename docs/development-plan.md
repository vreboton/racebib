# Milestone 1 development plan

## Sequence

1. Solution and frontend scaffolding.
2. Domain model and organizer tenancy.
3. EF Core/MySQL configuration and initial migration.
4. Identity, JWT sessions, role-scoped authorization.
5. Event/category/course/checkpoint/runner/registration APIs.
6. Vuetify administration screens and API integration.
7. Docker Compose, health checks and environment documentation.
8. Automated tests and end-to-end verification.

## Acceptance workflow

- Sign in as a seeded development administrator.
- Create an organizer and invite/assign organizer members.
- Create an event and move it through valid lifecycle transitions.
- Add categories and courses, then configure ordered start/intermediate/finish checkpoints.
- Create runners, register them in a category/course, and assign event-unique bib numbers.
- Verify duplicate bibs, cross-event references and unauthorized organizer access are rejected.

## Out of scope

Checkpoint timing PWA, offline queue/synchronization, QR timing capture, RFID reader, split calculations, ETA predictions, public live tracking, automated cutoffs and missing-runner alerts.
