---
title: Keep official runner decisions separate from registration status
author: Vreboton
date: 2026-11-20
branch: feature/milestone-6-phase-3-race-control
id: l-e486bba0
scope: repo
status: active
src: agent
touches:
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Timing.cs
  - backend/RaceTiming.Api/Controllers/TimingController.Observations.cs
  - backend/RaceTiming.Api/Controllers/TimingController.Results.cs
  - backend/RaceTiming.Infrastructure/Persistence/Migrations/RaceTimingDbContextModelSnapshot.cs
  - backend/RaceTiming.Infrastructure/Persistence/RaceTimingDbContext.cs
  - backend/RaceTiming.Infrastructure/Persistence/TimingModel.cs
  - frontend/race-timing-web/src/pages/EventDetailsPage.test.ts
  - frontend/race-timing-web/src/pages/EventDetailsPage.vue
---

## Decided
In backend/RaceTiming.Api/Controllers/TimingController.RaceControl.cs record official classifications as successive decisions keyed to the frozen timing registration and race.

## Tried and ruled out
Changing the operational registration status would mix unrelated workflows, because decisions need independent reasons, a stable history and evidence-relative review.

## Watch out
Recalculate timing from accepted evidence and compare the current fingerprint with the recorded decision. Evidence corrections can require review without themselves changing a classification.
