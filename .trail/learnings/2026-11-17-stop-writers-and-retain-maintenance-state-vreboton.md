---
title: Stop writers and retain maintenance state on administrator provisioning failure
author: Vreboton
date: 2026-11-17
branch: fix/admin-provisioning-failure-cleanup
id: l-877514df
scope: repo
status: active
src: agent
touches:
  - deploy/scripts/operations.py
  - deploy/tests/test_safety.py
  - deploy/scripts/release_state.py
---

## Decided
In deploy/scripts/operations.py, Operations.provision_admin() must stop API/web on any failure in its state-changing sequence, matching Operations.deploy(). Preserve the pre-commit maintenance state and reinstate it when ReleaseState.commit() in deploy/scripts/release_state.py fails, including after file replacement. Cleanup must not mask the original error. Recovery remains explicit; no database reset is performed.

## Tried and ruled out
The former inline administrator provisioning path could restart API/web and then fail readiness or persistence without stopping writers. Keeping only the mutated in-memory state after a failed commit is insufficient: commit clears inflight before saving, and a late durability error may already have replaced the file.

## Watch out
The maintenance barrier blocks operations, not API writes. deploy/tests/test_safety.py now covers readiness after restart, stage/commit persistence failures, post-replacement failure, cleanup errors, successful provisioning, and explicit recovery. Docker/storage outages still require operator incident recovery; do not kill detached maintenance containers or modify database data during cleanup.
