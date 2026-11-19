---
title: Lock the event before reading configuration for a write
author: Vreboton
date: 2026-11-19
branch: feature/milestone-3-race-configuration
id: l-546291d6
scope: repo
status: active
src: agent
touches:
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.RaceConfiguration.cs
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.cs
  - backend/RaceTiming.Api/Controllers/EventsController.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Courses.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Races.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.cs
  - docs/milestone-3-plan.md
  - frontend/race-timing-web/src/components/events/RaceConfigurationPanel.vue
  - frontend/race-timing-web/src/components/events/ScheduleInput.vue
  - frontend/race-timing-web/src/pages/EventDetailsPage.vue
---

## Decided
Race configuration mutations and event window or lifecycle changes lock the same MySQL event row before reading configuration that they plan to update.

## Tried and ruled out
Starting a serializable transaction and reading the event normally lets concurrent revision clones share locks and then deadlock when both try to insert. The MySQL provider can wrap the deadlock inside another exception.

## Watch out
Keep the parent event lock and both phases of checkpoint reordering in the same transaction. Preserve the existing course version and checkpoint IDs on failures. Map MySQL duplicate-key and lock conflicts to an HTTP conflict response.
