---
title: Match offline checkpoint assets despite static Vary headers
author: Vreboton
date: 2026-11-27
branch: feature/milestone-8-3-offline-checkpoint
id: l-c1a45c83
scope: repo
status: active
src: agent
touches:
  - race-day-pwa/service-worker.js
---

## Decided
In race-day-pwa/service-worker.js, match cached checkpoint assets by exact allowlisted URL with ignoreVary on Cache API lookup.

## Tried and ruled out
Chromium reached the cached HTML but reported net::ERR_FAILED for both production assets offline. The Vite preview host emitted Vary: Origin, and module and preload requests could differ from the installation fetch headers.

## Watch out
Ignore Vary only for the versioned, same-origin static asset allowlist. Keep authentication and timing API requests out of the fetch handler.
