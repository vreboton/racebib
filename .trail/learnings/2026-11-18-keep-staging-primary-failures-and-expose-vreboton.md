---
title: Keep staging primary failures and expose only sanitized infrastructure diagnostics
author: Vreboton
date: 2026-11-18
branch: feature/milestone-2.4.5-staging-rehearsal
id: l-96192d05
scope: repo
status: active
src: agent
touches:
  - deploy/staging/RaceBib.Staging.psm1
  - deploy/staging/infrastructure.py
  - deploy/tests/Test-StagingRehearsal.ps1
  - deploy/tests/test_staging_rehearsal.py
  - docs/deployment/staging-rehearsal.md
---

## Decided
In deploy/staging/infrastructure.py, emit exact string-only racebib-stage-error-v1 diagnostics on stdout because the shared production native runner discards stderr. Allowlist action, AWS operation/code and exception metadata; never print raw arguments, environment, AWS/SSH output or exception messages. In RaceBib.Staging.psm1, retain primary execution failure before independent deletion and verification failures.
Execute now holds a protected, existing per-run Windows file lock from before preflight through teardown. Fresh approval creates a durable execution.json claim; Python atomically consumes creation-attempt.json and binds submission to that invocation. Teardown cannot use historical submission intent or another invocation. Explicit-ID cleanup validates exact response collections and identities; diagnostics validate cross-field emission consistency.
## Tried and ruled out
The historical run record validated locally, and exit-zero empty AWS JSON plus DELETE_COMPLETE replay succeeded offline. The original cleanup exception and preceding creation failure remain unconfirmed; generic failure is not evidence of IAM denial. The finally exception masked the earlier failure, so changing permissions would not diagnose the cause.
Moving a submitted flag alone did not fix replay/concurrency: rejected Execute could still tear down an older run. Schema checks only on tag scans did not protect explicit-ID absence proofs. Independent enum validation admitted contradictory diagnostic combinations.
## Watch out
cleanup() supports absent/null targets, saved partial attachments, tagged ENIs/VPCs and terminal deleted stacks. destroy() persists a positively reconciled lost-response ARN without repeating deletion. VerifyCleanup leaves historical files/ACLs unchanged and never synthesizes acceptance. Exact missing-stack errors differ from authorization failures; no ARN plus absence remains ambiguous. Keep ownership, protected production identity and no-resubmission guards. Seek separate authorization before any AWS verification or new rehearsal.
Older unused plans without execution.lock need a fresh Plan; never retrofit the historical directory. Once approval is claimed, interruption consumes the invocation even before creation begins. Preflight failure or declined approval does not consume it. Local Windows lock/one-shot creation race tests and evidence snapshots exercise these distinctions without AWS access.
