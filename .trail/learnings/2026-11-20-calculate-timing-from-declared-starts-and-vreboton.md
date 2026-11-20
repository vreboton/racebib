---
title: Calculate timing from declared starts and adjacent visits
author: Vreboton
date: 2026-11-20
branch: feature/milestone-6-phase-2-timing-calculation
id: l-63a91825
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Timing.cs
  - backend/RaceTiming.Api/Controllers/TimingController.cs
  - docs/development/timing-foundation.md
---

## Decided
In backend/RaceTiming.Application/TimingCalculator.cs use an official declaration for the frozen wave or race and report a segment only when both adjacent configured visits have usable evidence.

## Tried and ruled out
A frozen schedule cannot substitute for a declaration because Phase 6.1 defines the schedule and observed gun separately. A duration across a missing visit does not establish either adjacent split.

## Watch out
Timing result reads use a single MySQL repeatable-read snapshot because loading declarations and accepted evidence in unrelated snapshots can mix revisions during capture.
