---
title: Enforce event runner uniqueness while retaining duplicate history
author: Vreboton
date: 2026-11-19
branch: feature/milestone-4-runner-registration
id: l-726a504e
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Registrations.cs
  - backend/RaceTiming.Api/Controllers/PublicParticipantsController.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.Import.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.cs
  - backend/RaceTiming.Api/Controllers/RunnersController.cs
  - backend/RaceTiming.Domain/Entities/Registration.cs
  - backend/RaceTiming.Infrastructure/Persistence/Migrations/RaceTimingDbContextModelSnapshot.cs
  - backend/RaceTiming.Infrastructure/Persistence/RaceTimingDbContext.cs
  - docs/runner-registration.md
---

## Decided
RaceTimingDbContext.cs uses a unique event/runner index with a database-owned slot for new registrations. EnforceEventRunnerRegistration preserves one indexed slot per historical pair and grandfathers additional existing rows. RegistrationsController.cs keeps the parent event lock and reads all rows before creating any registration. Profile writes lock the organizer, and registration writers take that lock after the event lock.

## Tried and ruled out
A plain unique event/runner index rejects legacy duplicates. An application-only uniqueness check permits inserts outside the event lock. A name match with an absent or unmatched email can conflate different runners.

## Watch out
The nullable slot is for the migration. Preserve it during data maintenance. RegistrationsController.Import.cs only resolves an existing runner by explicitly verified Runner ID, or by a unique email with agreeing names. Existing birthdates can only be changed through a deliberate profile edit.
