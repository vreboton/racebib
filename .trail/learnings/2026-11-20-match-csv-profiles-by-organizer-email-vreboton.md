---
title: Match CSV profiles by organizer email under the existing organizer lock
author: Vreboton
date: 2026-11-20
branch: feature/csv-registration-email-default-race
id: l-64a9300c
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Registrations.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.Import.cs
  - docs/runner-registration-sample.csv
  - docs/runner-registration.md
  - frontend/race-timing-web/src/components/events/RegistrationPanel.test.ts
  - frontend/race-timing-web/src/components/events/RegistrationPanel.vue
---

## Decided
RegistrationsController.Import.cs requires a valid email as the organizer-scoped CSV identity key. A unique normalized email reuses the existing profile. An unmatched email makes a new profile at confirmation. Multiple saved profiles for the same email block import. Preserve historical uniqueness slots and the event then organizer lock order.

## Tried and ruled out
The new CSV workflow drops Runner ID and the CSV status columns. Matching profiles by name would conflate homonyms. A database-wide unique email migration could reject historical profiles and would change manual profile behavior.

## Watch out
RegistrationsController.Import.cs fingerprints the chosen default race and resolved runner identity. If two event imports preview the same new organizer email, the second confirmation can require a fresh preview after the first creates the profile. Registration CSV now sets Confirmed and Paid by design.
