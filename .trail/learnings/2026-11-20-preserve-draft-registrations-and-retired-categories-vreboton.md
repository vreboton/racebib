---
title: Preserve draft registrations and retired categories through route revisions
author: Vreboton
date: 2026-11-20
branch: feature/race-configuration-enhancements
id: l-b63bd903
scope: repo
status: active
src: agent
touches:
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.RaceConfiguration.cs
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Registrations.cs
  - backend/RaceTiming.Api/Controllers/EventsController.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Courses.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Races.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.Import.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.cs
  - frontend/race-timing-web/src/components/events/RaceConfigurationPanel.test.ts
  - frontend/race-timing-web/src/components/events/RaceConfigurationPanel.vue
---

## Decided
DeleteCourse in RaceConfigurationController.Courses.cs treats Courses with the same event and name as one revision family because Course.Version has no separate parent row. The latest revision must be retired before the family can be deleted. A Category on any revision protects the family, including retired Categories.

## Tried and ruled out
An in-place edit when a Route is shared with a retired Category changes historical distance and checkpoints. CreateNextRevision moves only active Draft Categories to a new revision because their registration assignments already point to the correct Category, Age Group, and Start Wave.

## Watch out
EventStatus provides Draft status in this schema. Run CheckConfirmation after WithEvent acquires the parent row because manual registration and CSV confirmation can change the registration set.
