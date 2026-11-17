# Milestone 2.4.2 verification — November 17, 2026

## Review remediation (current)

Fetched `origin/main` and inspected its only new commit, `71e687fc35930e695a694c53bcba6fa4a2da5e74`, which merges the Certbot/HTTPS changes already in the feature branch. Rebased `feature/milestone-2.4.2-aws-cicd` onto it without conflicts; the original feature commit is now `0652c3d`. Main is an ancestor and its deployment engine, release verifier, Nginx templates and safeguards are preserved. No merge or push was performed. The existing remote feature history was not rewritten remotely; any later publication must account for the local rebase and review remote changes first.

GitHub's repository API confirmed `visibility=public`, `private=false`. Corrected the private-repository claims and stale SSH/migration workflow instructions. Public-repository environment reviewers are available on current plans, but main-only deployment rules, independent approval, prevented self-review and disabled bypass must be demonstrated before granting AWS access. GHCR package visibility remains independent.

External HTTPS probing is opt-in (`verify_external_https=false` by default) because production 443 is restricted to a trusted IP. Skipping explicitly means external availability is unverified. Existing host engine readiness/TLS checks remain mandatory. The summary separates SSM completion, target host-state receipt verification, and external probe outcome; an external failure preserves recorded remote success and never triggers retries, recovery, rollback or ingress changes.

Reviewed the exact-document/exact-instance SendCommand policy and read-only wildcard GetCommandInvocation scope. No IAM permissions were broadened. Version `1` is now accompanied by a required, independently approved AWS-reported document SHA256; missing/malformed hashes fail before submission. Version/hash pinning is enforced by the client/API, not by an invented IAM version condition. Administrators must restrict every document version, deletion/recreation, installed root-owned host scripts and workflow/environment changes. The deploy role still cannot invoke arbitrary shell documents, modify documents, open sessions, pass roles, change IAM, cancel commands or recover/migrate. Unknown remote outcomes still require inspection, and submission retries remain disabled.

| Re-run check | Result |
| --- | --- |
| Linux deployment safety suite | 70 passed, zero skipped, including new receipt/hash/ingress tests |
| Backend Release regression suite excluding MySqlIntegration/LiveDocker | 42 passed, zero skipped |
| Isolated MySQL safety rehearsal | Passed, including all six HTTP integration tests and synthetic clean-target restore |
| Frontend tests and production build | Eight passed; build passed with existing bundle-size warning |
| All workflow actionlint / shellcheck | Passed after correcting summary redirection style |
| Operational Bash syntax and diff whitespace | Passed |
| Isolated Nginx HTTPS/ACME rehearsal | Passed |
| Main ancestry / Trail refresh and learning check | Main ancestry verified; graph refreshed. Trail reported one lexical conflict on existing verified source/tag selection wording; reviewed as a false positive because no tag/asset/manifest is rewritten. Commit message records the rationale |

Before a controlled rollout: install the updated receipt-emitting wrapper, independently compare AWS document version-1 content and set `AWS_SSM_DOCUMENT_SHA256`, verify environment approval rejection/enforcement, and rehearse OIDC/SSM/hash mismatch/interruption behavior in staging. Verify HTTPS/login from the existing trusted IP; leave the hosted-runner probe off. If the host itself cannot pass its existing HTTPS check, resolve an authorized host path before rollout without weakening that check. These live checks remain untested here. No production deployment, AWS/GitHub infrastructure/settings change or security-group change occurred. The external LiveDocker application test remains excluded because no dedicated live environment was configured.

## Initial implementation record

Implemented on `feature/milestone-2.4.2-aws-cicd`. No production operation, AWS configuration, GitHub settings change, release publication, merge or push was performed. Existing unrelated untracked workspace files were preserved. No secrets were added.

## Actual checks

| Check | Result |
| --- | --- |
| Backend Release build and tests excluding MySqlIntegration/LiveDocker | 42 passed, zero failed/skipped |
| MySQL HTTP integration suite against a new UUID-labelled local MySQL 8.4 container | Six passed, zero skipped; committed migrations applied only to its fresh test database |
| Existing disposable MySQL safety rehearsal | Passed: empty schema refusal, migrations, schema/history drift refusal, readiness, atomic/idempotent bootstrap, synthetic backup/clean-target restore; its embedded six HTTP tests also passed |
| Deployment Python tests on Linux/Python 3.11 in read-only local container | 66 passed, including nine new SSM tests; zero skipped |
| Frontend Vitest | Four files, eight passed |
| Frontend production build | Passed; existing bundle-size warning remains |
| All GitHub Actions workflows via local actionlint container | Passed |
| Operational shell scripts and installer via bash syntax check | Passed |
| Existing isolated Nginx/HTTPS rehearsal using Git OpenSSL | Passed: ACME 200/404, HTTP 308, canonical redirects, trusted TLS, proxy headers/paths and security headers |
| Git diff whitespace check | Passed |
| Trail graph refresh and learning check | Refreshed; no conflicts. Reviewed heuristic warnings: temporary OIDC sessions and retained administrator recovery access are intentional; permanent CI credentials remain rejected |

The Windows `python` command was an unavailable app alias; deployment tests ran using the existing local `python:3.11-slim` Docker image. Backend/rehearsals required execution outside sandbox restrictions to access the local Docker daemon/.NET environment. These tests used fresh local test resources only, with identity checks before cleanup; production resources were not selected. Default Windows OpenSSL failed during HTTPS rehearsal certificate creation; the available Git OpenSSL was selected explicitly for the follow-up rehearsal.

## New safety coverage

SSM tests cover SHA/tag injection and mismatches, fixed document/environment parameters and command, main/manual/production workflow gates and permissions, exact IAM resources/trust subject, existing-engine delegation without migrations, failed-operation state inspection, target/barrier checks with redacted state projection, eventual consistency, terminal failures, unknown-outcome timeout, and disabled implicit CLI retries/error redaction. Existing release verifier and deployment failure-injection tests continue to cover rejected mutable releases, skipped/failed provenance jobs, preserved recovery metadata and durable barriers/rollback safeguards.

## Not claimed or tested locally

- Live OIDC federation, account IAM enforcement, environment approval rejection, or SSM document creation/execution on AWS.
- Required-reviewer enforcement on this public repository. Do not enable the deployment role until the documented approval configuration works.
- Target Ubuntu 26.04.1 agent compatibility, real SSM delivery/execution interruption/reboot, first controlled production deployment, or actual production release/registry availability.
- Production backup restoration or administrator login; the user reported these were previously verified, and local tests do not reconfirm them.
- The external `LiveDocker` backend test requiring a configured live application environment. It was deliberately excluded; the local disposable MySQL tests were executed independently.

The original immutable publishing system was audited and retained; no image build/publication or remote release was created. Actual GHCR package access and retained release/job evidence must be confirmed during setup. See [the complete setup and first-deployment procedure](github-actions-ssm.md).

## Files

- Modified `.github/workflows/build.yml`: explicit read-only token permissions.
- Modified `.github/workflows/deploy-production.yml`: OIDC/SSM, no migration input or SSH key, strict HTTPS status check and outcome summary.
- Added `deploy/scripts/ssm_deploy.py`, `ssm_entry.py`, `ssm_bootstrap.py`: bounded runner observation, fixed installed-engine invocation, isolated host imports and private per-operation logs.
- Added `deploy/aws/deploy-document.json`, `github-trust.json`, `github-permissions.json`, `instance-trust.json`, `instance-permissions.json`: fixed command document and IAM templates.
- Added `deploy/tests/test_ssm.py`: SSM/workflow/IAM safety tests.
- Added this verification report and `docs/deployment/github-actions-ssm.md`; updated `docs/deployment/aws-ec2.md` to identify the current transport.
- Added `.trail/learnings/2026-11-17-use-approved-oidc-ssm-deployment-without-vreboton.md`: approval, migration and uncertain-submission decisions.
