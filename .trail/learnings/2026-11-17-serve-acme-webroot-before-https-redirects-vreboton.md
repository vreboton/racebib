---
title: Serve ACME webroot before HTTPS redirects without weakening template validation
author: Vreboton
date: 2026-11-17
branch: codex/https-certbot-webroot
id: l-80d8edb6
scope: repo
status: active
src: agent
touches:
  - ".github/workflows/deployment-safety.yml"
  - deploy/nginx/racebib-https.conf.example
  - deploy/scripts/operations.py
  - docs/deployment/aws-ec2.md
---

## Decided
Keep the RaceBib HTTPS validator's exact rendered-template comparison and trusted TLS handshake. Serve only the HTTP-01 ACME prefix from /var/www/letsencrypt with try_files returning 404 for missing challenges; put the canonical HTTPS redirect inside location /.
## Tried and ruled out
A server-scope return redirects before Nginx location processing, so adding an ACME location without moving that return does not enable webroot validation. Certbot Nginx installer rewrites would break the exact-template safety contract; use certonly webroot plus a successful-renewal nginx-test/reload hook instead.
## Watch out
A working TLS browser connection does not update application configuration. Transition only APPLICATION_BASE_URL and DEPLOYMENT_ACCESS_MODE, preserving secrets, database configuration, volume, immutable release identity and state. Redeploy the current verified release without migrations. Keep TCP 80 reachable for HTTP-01 renewal and rehearse externally; do not run the Docker Nginx test harness on the micro production host.
