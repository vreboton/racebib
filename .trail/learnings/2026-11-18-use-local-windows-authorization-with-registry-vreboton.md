---
title: "Use local Windows authorization with registry and host evidence, never runner approval claims"
author: Vreboton
date: 2026-11-18
branch: feature/milestone-2.4.3-local-controller
id: l-b499327c
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/deploy-production.yml"
  - deploy/install-server.sh
  - deploy/scripts/ssh-dispatch.sh
  - deploy/scripts/ssm_bootstrap.py
  - deploy/scripts/ssm_deploy.py
  - deploy/scripts/ssm_entry.py
  - deploy/tests/test_publication_recovery.py
  - deploy/tests/test_ssm.py
  - docs/deployment/aws-ec2.md
  - docs/deployment/github-actions-ssm.md
---

## Decided
Milestone 2.4.3 now uses an interactive Windows PowerShell 7 local controller, not a self-hosted runner. The operator explicitly confirms verified SHA and manifest after existing provenance and mandatory live registry checks. GitHub production approval authorizes only its own approval-test workflow; no GitHub-to-local independent authorization bridge is claimed. Match exact authenticated host receipt metadata and schema evidence before trusted-PC HTTPS.
## Tried and ruled out
Persistent runners on this public personally owned repo, administrator PEMs, POSIX chmod as Windows key protection, accepting SSH zero or HTTP 200 as complete proof, automatic retry after ambiguous outcomes, and activating the old SSM workflow. Keep SSM body/code/templates under an impossible dispatch event condition; require Windows ACLs and independently pinned Ed25519 host key.
## Watch out
Actual main still has an older SSH dispatcher permitting migration flags and no receipt. Independently verify/install the tightened root entry and remove broad sudo rights before first use; an old dispatcher can deploy before the client rejects a missing receipt. Do not probe capability by submitting a release. Local typed confirmation is not two-person authorization; if policy requires independent approval, stop until that mechanism exists. Windows mock rehearsals never contact production. This supersedes the earlier uncommitted self-hosted-runner proposal.
