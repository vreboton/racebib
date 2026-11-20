---
title: Provision managed categories for retired-only races
author: Vreboton
date: 2026-11-20
branch: feature/simplified-race-configuration
id: l-e074dcba
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Registrations.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Races.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.Import.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.cs
  - docs/architecture.md
  - docs/decisions/001-race-category-wave-model.md
  - docs/runner-registration.md
  - frontend/race-timing-web/src/components/events/RegistrationPanel.test.ts
---

## Decided
ResolveCategory in backend/RaceTiming.Api/Controllers/RegistrationsController.cs treats a retired-only race as needing a managed category for a new race assignment. Reuse an unrestricted unreferenced retired Open with no custom description, or allocate a distinct name and preserve the retired row.

## Tried and ruled out
Counting all category records made a retired-only race look configured and let new assignments remain null. Reactivating a historically referenced Open would change its retirement state.

## Watch out
RegistrationsController.Import.cs fingerprints the provisioning plan. Preview stays read-only, and confirmation verifies the plan before applying it once per race under the event lock. Same-race edits with an omitted category retain their existing assignment.
