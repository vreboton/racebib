---
title: Use restricted SSH with verified runner isolation or a manual trusted-workstation fallback
author: Vreboton
date: 2026-11-18
branch: feature/milestone-2.4.3-ssh-deployment
id: l-0d881f97
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
Historical proposal only, superseded by the local Windows controller decision later on November 18. Do not register a runner; the current implementation has no runner dependency. The older rationale below is retained for audit history.

Milestone 2.4.3 replaces active OIDC/SSM with pinned-host restricted SSH. The CI dispatcher no longer permits migrations and sudoes only a separately validated root wrapper. Existing release verifier, digest/state engine, backups and recovery barriers remain unchanged. SSM code/templates are retained as inactive history.
## Tried and ruled out
An always-on public-repository label-only runner, a runner on production, administrator SSH keys, dynamic ingress changes, and automatic retry after disconnect. Require server-enforced workflow-restricted runner group access and prefer a clean on-demand single-job VM; temporary online status alone does not isolate queued jobs.
## Watch out
The repository is public and personally owned (GitHub owner.type=User). Organization/workflow-restricted runner-group availability is an activation blocker until an owner-approved arrangement establishes it. Do not remove the group requirement; if no safe runner can be provided, use separately approved manual verified deployment from an already trusted workstation/network. Preserve the trusted-IP-only HTTPS policy. Restricted rotatable SSH keys still carry theft/runner-compromise risk and need source/command constraints and rotation.
