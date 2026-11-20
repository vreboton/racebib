---
title: Provision registration categories under the event lock
author: Vreboton
date: 2026-11-20
branch: feature/simplified-race-configuration
id: l-747a4077
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Registrations.cs
  - backend/RaceTiming.Api/Controllers/RaceConfigurationController.Races.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.Import.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.cs
  - frontend/race-timing-web/src/components/events/RaceConfigurationPanel.test.ts
  - frontend/race-timing-web/src/components/events/RaceConfigurationPanel.vue
  - frontend/race-timing-web/src/components/events/RegistrationPanel.test.ts
  - frontend/race-timing-web/src/components/events/RegistrationPanel.vue
  - frontend/race-timing-web/src/pages/EventDetailsPage.vue
---

## Decided
Use ResolveCategory in RegistrationsController.cs to resolve the sole active category. Provision a default category for an empty race on a unified configuration save or when a successful registration first needs it, under the existing event-row lock.

## Tried and ruled out
A schema-wide uniqueness rule or rewriting past registration assignments would alter legacy data. The read-only category audit counts inactive categories too.

## Watch out
RegistrationsController.Import.cs must keep preview read-only. Confirmation creates missing categories only after verifying the preview fingerprint under the event lock. An edit on the original race retains the saved assignment, including null.
