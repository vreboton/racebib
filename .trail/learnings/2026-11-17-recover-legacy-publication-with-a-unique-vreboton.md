---
title: Recover legacy publication with a unique immutable receipt and unchanged manifest
author: Vreboton
date: 2026-11-17
branch: fix/immutable-publication-recovery
id: l-00f562ef
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/deploy-production.yml"
  - deploy/scripts/operations.py
  - deploy/scripts/release.py
  - deploy/scripts/restore_rehearsal.py
  - deploy/scripts/ssh-dispatch.sh
  - deploy/tests/test_safety.py
  - docs/deployment/aws-ec2.md
---

## Decided
Retain legacy mutable RaceBib releases and original GHCR attempt tags. Recover only through a fresh immutable racebib-SHA-recovery-RUN-ATTEMPT release, preserving release.json bytes and its hash; record recovery provenance in a separate recovery.json receipt. Require successful source checks and both image jobs, logged digest/provenance agreement, live registry objects, and a successful recovery workflow. Pass the explicit tag through deployment and rollback state.
## Tried and ruled out
Rebuilding a previously released source, replacing tags/assets, or rewriting publication identity inside release.json would break the fixed SHA-to-manifest mapping. None is permitted. Default canonical verification must continue rejecting mutable releases.
## Watch out
The repository is currently public; the earlier private-repository assumption was incorrect and is superseded by the November 17 remediation verification. Package visibility and reads need independent checks. The workstation Git credential had repository access but no package-read scope; GHCR denied scope / returned manifest unknown, so live image availability remained unverified locally. Do not infer deletion or bypass verification. Preserve original Actions job logs and attempt tags for the recovery/rollback window.
