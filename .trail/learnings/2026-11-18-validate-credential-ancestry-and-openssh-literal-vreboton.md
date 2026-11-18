---
title: Validate credential ancestry and OpenSSH literal paths before local deployment
author: Vreboton
date: 2026-11-18
branch: feature/milestone-2.4.3-local-controller
id: l-ff6d2559
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
Final controller audit rejects null DACLs, untrusted ownership/replacement rights throughout credential ancestry, UNC/network paths and OpenSSH percent/environment filename expansion. Revalidate configuration and ACLs after local authorization, reject changed values, and parse evidence with unique exact string fields. Require maintained PowerShell 7.4+ for the APIs actually used. Sanitize the root wrapper child environment without changing the production engine.
## Tried and ruled out
A secure file plus only an immediate-parent check cannot prevent replacement through an unsafe ancestor. Quoting a known_hosts argument does not suppress OpenSSH's percent/environment token expansion. ConvertFrom-Json alone silently accepts duplicate property names. The deleted runner prototype client and actionlint label file were never tracked on main; no production dependency was deleted.
## Watch out
Synthetic ACL tests run under the operator identity and never touch actual credentials. Windows test fixtures use the user's profile rather than a potentially shared temp ancestry. Local confirmation is not independent GitHub authorization. Real sudoers, source restrictions, host key and receipt installation remain manually verified rollout prerequisites; no production access is allowed during this audit.

Additional audit finding: the existing engine registers TERM/INT but not HUP. The SSH wrapper now launches it in a separate session with inherited HUP ignored, so a routine disconnect need not interrupt the stateful operation. A synthetic local child proves HUP survival; hard kills/reboots can still interrupt cleanup and require manual state inspection. Never translate that resilience into permission to retry.
