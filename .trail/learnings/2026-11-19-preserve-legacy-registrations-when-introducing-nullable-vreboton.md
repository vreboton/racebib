---
title: Preserve legacy registrations when introducing nullable bibs
author: Vreboton
date: 2026-11-19
branch: feature/milestone-4-runner-registration
id: l-be5f3d46
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api/Controllers/RunnersController.cs
  - backend/RaceTiming.Api/RaceTiming.Api.csproj
  - backend/RaceTiming.Domain/Entities/Registration.cs
  - backend/RaceTiming.Domain/Entities/Runner.cs
  - backend/RaceTiming.Domain/Enums/RecordStatus.cs
  - backend/RaceTiming.Infrastructure/Persistence/Migrations/RaceTimingDbContextModelSnapshot.cs
  - backend/RaceTiming.Infrastructure/Persistence/RaceTimingDbContext.cs
  - docs/architecture.md
  - frontend/race-timing-web/src/pages/EventDetailsPage.vue
---

## Decided
Keep the existing unique event/bib index when making BibNumber nullable. Serialize manual registration and CSV confirmation on the parent event row. Keep the event/runner lookup index non-unique so migration preserves legacy duplicate history.

## Tried and ruled out
The scaffolded non-null payment column defaulted to an empty string. Use Unspecified so EF can read registrations created before the migration. Converting all unassigned bibs to an empty string on downgrade collides with the unique index.

## Watch out
The MySQL CI workflow verifies the exact number of executed tests in its TRX output. Update that count when adding relational tests. Race fixtures created through the race API need a course with valid start and finish checkpoints.
