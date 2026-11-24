---
title: Timestamp checkpoint entries before online lookup
author: Vreboton
date: 2026-11-24
branch: feature/milestone-8-online-checkpoint
id: l-011cf38a
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.Timing.cs
  - backend/RaceTiming.Api.Tests/OrganizerMemberManagementTests.cs
  - backend/RaceTiming.Api/Controllers/OrganizerInvitationsController.cs
  - frontend/race-timing-web/src/components/events/RaceControlPanel.vue
  - frontend/race-timing-web/src/pages/OrganizerMembersPage.vue
  - frontend/race-timing-web/src/services/organizerMembers.ts
---

## Decided
The checkpoint app sends backend/RaceTiming.Api/Controllers/TimingController.Observations.cs the timestamp, assignment and two identifiers it captured before lookup.

## Tried and ruled out
Waiting to timestamp until runner or occurrence confirmation would shift the observation because a network request or operator choice can be delayed.

## Watch out
Reuse the timestamp and operation identifier when retrying an uncertain receipt. Tab storage survives reload; operators must keep the tab open until pending captures are reconciled.
