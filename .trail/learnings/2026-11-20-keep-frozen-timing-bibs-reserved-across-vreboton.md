---
title: Keep frozen timing bibs reserved across registration writes
author: Vreboton
date: 2026-11-20
branch: feature/milestone-6-phase-1-timing-foundation
id: l-a6de009c
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Timing.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.BulkBibs.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.Import.cs
  - backend/RaceTiming.Api/Controllers/RegistrationsController.cs
  - docs/development/timing-foundation.md
---

## Decided
In backend/RaceTiming.Api/Controllers/RegistrationsController.cs, reserve bibs from both Registrations and TimingRosterEntries under the same event lock as arming and roster amendments.

## Tried and ruled out
Reading only current registration bibs fails because a frozen roster can still resolve a bib after a registration-side change.

## Watch out
CSV and bulk confirmation must recompute reservations, because a frozen reservation can appear after a preview is issued. Withdrawal and check-in state do not release the frozen bib.
