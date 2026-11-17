# Milestone 2.4.2 verification — November 17, 2026

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
- Required-reviewer availability under this private repository's actual GitHub subscription. Do not enable the deployment role until the documented approval configuration works.
- Target Ubuntu 26.04.1 agent compatibility, real SSM delivery/execution interruption/reboot, first controlled production deployment, or actual production release/registry availability.
- Production backup restoration or administrator login; the user reported these were previously verified, and local tests do not reconfirm them.
- The external `LiveDocker` backend test requiring a configured live application environment. It was deliberately excluded; the local disposable MySQL tests were executed independently.

The original immutable publishing system was audited and retained; no image build/publication or remote release was created. Private GHCR access and retained release/job evidence must be confirmed during setup. See [the complete setup and first-deployment procedure](github-actions-ssm.md).

## Files

- Modified `.github/workflows/build.yml`: explicit read-only token permissions.
- Modified `.github/workflows/deploy-production.yml`: OIDC/SSM, no migration input or SSH key, strict HTTPS status check and outcome summary.
- Added `deploy/scripts/ssm_deploy.py`, `ssm_entry.py`, `ssm_bootstrap.py`: bounded runner observation, fixed installed-engine invocation, isolated host imports and private per-operation logs.
- Added `deploy/aws/deploy-document.json`, `github-trust.json`, `github-permissions.json`, `instance-trust.json`, `instance-permissions.json`: fixed command document and IAM templates.
- Added `deploy/tests/test_ssm.py`: SSM/workflow/IAM safety tests.
- Added this verification report and `docs/deployment/github-actions-ssm.md`; updated `docs/deployment/aws-ec2.md` to identify the current transport.
- Added `.trail/learnings/2026-11-17-use-approved-oidc-ssm-deployment-without-vreboton.md`: approval, migration and uncertain-submission decisions.
