---
title: Rehearse deployment on a disposable isolated AWS stack with independent trust
author: Vreboton
date: 2026-11-18
branch: feature/milestone-2.4.5-staging-rehearsal
id: l-5ce18eea
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/local-controller-safety.yml"
---

## Decided
Use a separate Windows staging entry point and new nonce-named CloudFormation stack. Reuse the production engine unchanged only on an isolated temporary VM, with fresh data/keys/secrets and a per-run private CA. Trust the SSH key through authenticated AWS console output after stack/account/instance checks. Plan offline, require explicit cost/run approval, and tear down only that guarded stack.
## Tried and ruled out
A shared production VPC, production PEM, weakened TLS, persistent runner, or automatic application recovery after ambiguous SSH outcomes are not acceptable. Windows OpenSSH public-key files can lack WRITE_OWNER despite correct ownership: protect their DACL without an unnecessary ownership mutation, then recheck the existing ancestry/owner policy.
## Watch out
The inspected main includes Milestone 2.4.3 but no identifiable 2.4.4 baseline. Local mocks cannot certify account IAM compatibility or production readiness. The shutdown fuse and local cost ceiling are not billing caps. Preserve run state after interruption and verify scoped stack/resource cleanup; never repeat an uncertain CreateStack or deployment.

The security review found that a generic SafetyError with a maintenance barrier is not evidence of the intended injection. Require the exact backup-hook exception plus a single-use run/nonce/release/state-bound root proof, and reject even cleanup stop-writers errors swallowed by the engine. Nonzero SSH status alone is inconclusive. Before deletion, verify exact inventory, unique creation events, persisted physical identities, live ownership/relationships and an operator-supplied protected production ID list. Partial or abnormal states without proof require independent manual investigation; do not weaken guards for convenient cleanup. CloudFormation tagged creation needs a separate creation-only TagResource allow.

The actual SSH entry prints a root-log announcement even on maintenance rejection; empty-stdout mocks hid a real protocol mismatch. Keep production scripts unchanged. Treat that exact output as an envelope only, then attest the fresh root-owned log through staging control: exact guard traceback, state-bound dual nonce receipt, no raw log disclosure, one verification attempt, and a verified probe required before consuming the synthetic recovery proof. The isolated real-entry integration test mocks only engine execution and supplies the Windows protocol fixtures; generic diagnostics or matching exception text from the wrong origin must never authorize recovery.

Recovery proof checks must share one uninterrupted engine lock with engine execution. The old check/unlink/subprocess sequence had a confirmed TOCTOU/ABA race. Staging now takes the existing non-blocking deployment lock once and calls unchanged Operations.deploy(recovery=True, migrate=False) directly, avoiding nested acquisition by operations.main. Proof files are immutable retained evidence; exclusive durable per-nonce claim files spend authorization without deleting any pathname. Recheck descriptor identity/content and current state before dispatch; never remove claims to retry. Real-lock process tests cover contention, delayed stale requests, replacement/state mutation, interruptions and late commit failures, without production or network access.
