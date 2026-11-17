---
title: Normalize Compose byte limits and explicitly resume interrupted database adoption
author: Vreboton
date: 2026-11-17
branch: codex/fix-mysql-resource-limit-validation
id: l-5c56056d
scope: repo
status: active
src: agent
touches:
  - deploy/scripts/operations.py
  - docs/deployment/aws-ec2.md
---

## Decided
Normalize MySQL runtime Memory/MemorySwap and rendered Compose mem_limit/memswap_limit to validated positive signed-64-bit integer bytes for equality. Accept decimal ASCII strings and integers only; reject booleans, floats, units, negatives, zero, missing or oversized values. Keep the exact rendered configuration baseline fingerprint unchanged.
## Tried and ruled out
Raw JSON equality fails with Compose 5.7.1 decimal strings versus Docker inspect integers. Unrestricted int() coercion would accept booleans and truncate floats. Clearing state or rerunning ordinary initialization cannot safely resume an interrupted database initialization.
## Watch out
A failed first initialization leaves a prepared database-initialization barrier before database.json is written. Resume explicitly with database-init.sh --adopt-existing --resume-initialization and a reason, only with no successful application history. Validate and start the existing container without pull/up/recreation; preserve prior barrier until separate schema/application recovery succeeds. Never delete or reset a volume or hand-write database.json.
