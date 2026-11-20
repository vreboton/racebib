---
title: Keep route confirmation scoped and preserve fixed CSV headers
author: Vreboton
date: 2026-11-20
branch: feature/race-configuration-enhancements
id: l-c1dd0c93
scope: repo
status: active
src: agent
touches:
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.RaceConfiguration.cs
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Registrations.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Courses.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Races.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.Import.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.cs
  - frontend/race-timing-web/src/components/events/RaceConfigurationPanel.test.ts
  - frontend/race-timing-web/src/components/events/RaceConfigurationPanel.vue
  - frontend/race-timing-web/src/components/events/RegistrationPanel.vue
---

## Decided
Keep fixed-template and email/default-Race work in the CSV PRs. RegistrationPanel.vue can explain the Race and Category CSV headers without editing their parser or the preview payload. Use git merge-tree against both PR heads because a clean merge to main alone misses overlapping changes in the stacked CSV PRs.

## Tried and ruled out
Adding aliases and render fields in RegistrationsController.Import.cs conflicts with the fixed-template parser replacing those paths. The fixed header contract is shared with CSV files and should keep each literal unchanged.

## Watch out
RegistrationConfirmation must include the source Course and assignment values because a move between Categories on the same Route preserves both registration IDs and count. Read that stamp for writes under the shared event row lock.
