---
title: Check bulk bib previews against the database collation
author: Vreboton
date: 2026-11-20
branch: feature/milestone-5-phase-3-bulk-bib-assignment
id: l-5e830f8e
scope: repo
status: active
src: agent
touches:
  - backend/RaceTiming.Api/Controllers/RegistrationsController.BulkBibs.cs
  - backend/RaceTiming.Application/BibAllocator.cs
  - backend/RaceTiming.Api.Tests/MySqlApiIntegrationTests.BulkBibs.cs
---

## Decided
Check planned bibs against the MySQL column comparison during read-only preview and locked confirmation because legacy values can be equal under the event-bib index collation even when BibAllocator considers them distinct.

## Tried and ruled out
Using only the allocator's ordinal comparison produced an approved ASCII bib that MySQL rejected as a duplicate of a legacy full-width number.

## Watch out
A legacy equivalent can appear outside a race-filtered scope after approval. Reject it at confirmation before writes. Keep the unique index and event transaction because any database conflict during a write must roll back every bib, version increment, and audit in the batch.
