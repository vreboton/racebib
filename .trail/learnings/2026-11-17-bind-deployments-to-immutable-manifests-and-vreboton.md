---
title: Bind deployments to immutable manifests and explicit schema recovery
author: Vreboton
date: 2026-11-17
branch: feature/milestone-2.4-production-readiness
id: l-cdcf7cff
scope: repo
status: active
src: agent
touches:
  - backend/RaceTiming.Api/Infrastructure/DevelopmentIdentitySeeder.cs
  - backend/RaceTiming.Api/Program.cs
---

## Decided
Use immutable GitHub release assets to bind a source SHA to exact API/web image digests and successful build, safety and publication attempts. Keep current, previous distinct release, history and the maintenance barrier in one fsynced atomic JSON document. An interrupted operation requires explicit recovery; do not delete the barrier. Database-only Compose operations must not depend on application release markers or JWT configuration.

## Tried and ruled out
SHA-tag-only rollback and liveness/401 probes cannot establish release or schema readiness. Comparing only EF table indexes rejected valid MySQL schemas: alternate keys are unique constraints in EF and unique indexes in MySQL. The verifier now includes both; the disposable MySQL rehearsal confirmed this correction.

## Watch out
MySQL DDL can partially commit. Verify exact migration history plus supported relational structure and application reads before recording success. A timed-out Docker CLI can leave a migration running: stop regular writers, never automatically kill the maintenance container. Keep HTTP restricted to disposable tests; invitations require HTTPS. Actual EC2/SSH/GitHub release execution still needs authorized staging rehearsal.
