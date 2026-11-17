---
title: Use approved OIDC SSM deployment without routine migration or submission retries
author: Vreboton
date: 2026-11-17
branch: feature/milestone-2.4.2-aws-cicd
id: l-a609fa62
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/build.yml"
  - ".github/workflows/deploy-production.yml"
  - docs/deployment/aws-ec2.md
---

## Decided
Use environment-approved GitHub OIDC and a version-1 fixed SSM document around the installed deployment engine. Send only a verified SHA and optional matching recovery tag. Routine deployment cannot migrate; recovery and migrations remain separately approved operator procedures.
## Tried and ruled out
Permanent access keys and private CI SSH keys were rejected in favor of temporary OIDC role sessions. Generic shell-document execution, remote script downloads, and automatic resubmission were also rejected. A lost SendCommand response leaves an unknown outcome and must be investigated before retrying.
## Watch out
The repository is public; the earlier private-repository assumption was incorrect. Public repositories support required environment reviewers on current GitHub plans, but enforcement, main-only environment rules, independent approval and disabled bypass must be verified before enabling the OIDC role. SSM ENV_VAR requires agent 3.3.2746.0 or later. GHCR package visibility is independent of repository visibility; rotate host credentials where needed. Port 443 is restricted to a trusted IP, so the hosted-runner external probe defaults off. Pin the approved AWS document hash as well as version 1; IAM limits document/instance ARNs, not the client-selected version.
