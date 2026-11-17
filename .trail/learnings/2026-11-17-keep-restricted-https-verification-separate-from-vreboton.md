---
title: Keep restricted HTTPS verification separate from deployment and pin the approved SSM document
author: Vreboton
date: 2026-11-17
branch: feature/milestone-2.4.2-aws-cicd
id: l-e4d78135
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/deploy-production.yml"
  - deploy/scripts/ssm_deploy.py
  - deploy/scripts/ssm_entry.py
  - deploy/tests/test_ssm.py
  - docs/deployment/aws-ec2.md
  - docs/deployment/github-actions-ssm.md
  - docs/deployment/milestone-2.4.2-verification.md
---

## Decided
The repository is public (confirmed through GitHub API); required environment reviewer enforcement and main-only/no-bypass rules must be demonstrated before AWS access is granted. Production 443 remains restricted to the trusted IP. Default the hosted-runner external probe off, report it as unverified, and validate availability from the authorized network separately from remote execution and the host-state receipt.
## Tried and ruled out
Opening ingress for hosted runners, treating external probe failure as a failed host release, automatic deployment retry or rollback, and relying on numeric document version alone. Pin an independently approved AWS document hash and version 1 in the client. IAM still grants the exact document name across versions; administrators must control every version and host script installation.
## Watch out
A rebase onto main changes the feature branch commit identities; do not force-push without reviewing remote branch changes and explicit publishing authorization. SSM failure/cancellation/lost responses retain uncertain outcomes and require inspection. A verified deployment remains recorded if the optional external probe fails. Public repository visibility does not establish GHCR package visibility.
