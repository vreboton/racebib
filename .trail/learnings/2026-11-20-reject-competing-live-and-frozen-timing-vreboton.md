---
title: Reject competing live and frozen timing bib owners
author: Vreboton
date: 2026-11-20
branch: fix/timing-bib-identity-collision
id: l-9a11edc2
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Timing.cs
  - backend/RaceTiming.Api/Controllers/TimingController.Observations.cs
  - docs/development/timing-foundation.md
---

## Decided
In backend/RaceTiming.Api/Controllers/TimingController.Observations.cs, check live bib ownership under the event lock for lookup and new capture.

## Tried and ruled out
Protecting only registration allocation is insufficient because legacy databases can already assign the same bib to a different live registration.

## Watch out
A bib with no live owner can still identify its frozen registration. Reject only a competing owner and preserve committed receipts for authorized replay.
