---
title: Preserve accepted visits when conflicting evidence arrives
author: Vreboton
date: 2026-11-20
branch: feature/milestone-6-phase-1-timing-foundation
id: l-ee3b0518
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Courses.cs
  - backend/RaceTiming.Api/Services/IAuditLogger.cs
  - backend/RaceTiming.Infrastructure/Persistence/Migrations/RaceTimingDbContextModelSnapshot.cs
  - backend/RaceTiming.Infrastructure/Persistence/RaceTimingDbContext.cs
---

## Decided
In backend/RaceTiming.Api/Controllers/TimingController.Observations.cs, keep one accepted timing slot per registration and ordered occurrence. Allow an assigned operator to confirm an ambiguous visit.

## Tried and ruled out
Do not use the next open visit to resolve an ambiguous report, because a duplicate of the previous crossing would consume a legitimate later slot.

## Watch out
TimingProjection.Reconstruct uses the latest decision per observation. Replay rechecks the assignment generation because revocation must also block delayed submissions.
