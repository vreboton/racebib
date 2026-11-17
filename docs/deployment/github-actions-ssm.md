# Milestone 2.4.2 — approved GitHub Actions deployment over SSM

Implementation only: no AWS/GitHub settings or production files have been changed and no production deployment has been executed. Work is on `feature/milestone-2.4.2-aws-cicd`. The reported production baseline is `899f0d6a22cdafad3a691c52d3956ffbeb2bbcee`; do not infer that its current release/registry provenance has been verified in this session.

## Architecture and audit

The existing `build.yml` builds/tests .NET, checks migration consistency, executes all six required disposable-MySQL HTTP tests, tests Vue and builds the production frontend. `deployment-safety.yml` independently runs deployment failure-injection tests and disposable MySQL/HTTPS rehearsals. `publish-images.yml` accepts only a successful main-push build, independently verifies all required jobs/attempts and the matching safety run, then publishes API/web images and one immutable release mapping. Failed or skipped required tests prevent publication. Image jobs alone get packages write; release finalization alone gets contents write. This milestone retains that publishing contract and adds explicit contents-read permissions to build.

`deploy-production.yml` is dispatch-only, main-only, serialized without cancellation, and uses environment `production`. An approval unlocks runner-side immutable release verification, followed by a short-lived OIDC role session. SSM can invoke only document `RaceBib-DeployVerifiedRelease` against one instance. The version-1 document runs a fixed root-owned Python entry point; it accepts only a full SHA and optional matching recovery tag. No arbitrary command, checkout, script download, migration flag or credential can be supplied.

The installed `deploy.sh` and `operations.py` independently verify the release again using host credentials, acquire the existing host lock, enforce the idle state and database baseline, pull digest images, stop writers, back up, verify schema, start/verify API and web, check routing/TLS, and atomically commit successful state. The wrapper checks current SHA and absence of a barrier afterward. The runner also checks public HTTPS health with certificate verification enabled. This adds transport around the existing engine rather than another deployment engine.

Ordinary deployment never migrates, resets a database/admin, deletes a volume, changes `.env`, clears state or automatically rolls back. Existing manual recovery/migration entry points remain privileged operator operations. No production DB credentials belong in GitHub.

## Before enabling the role: GitHub approval is mandatory

In repository Settings → Environments create `production`; select deployment branches **only main** (no tags), required independent reviewers, prevent self-review and disable administrator bypass. Protect main and require `backend`, `backend-mysql-integration`, `frontend`, and `deployment-safety`, with reviewed workflow changes and restricted bypass/force pushes.

**Private repository limitation:** verify the account plan actually supports required environment reviewers. GitHub Free/Pro/Team required reviewers are available only for public repositories; private repositories require a supporting Enterprise plan. Do not change repository visibility as part of setup. If required reviewers are unavailable, leave the AWS role disabled/untrusted until a supported approval configuration is available. Dispatch is not an approval substitute. YAML cannot create or enforce these external protection settings. Confirm a dispatch waits for approval and that a rejected deployment never obtains AWS credentials before enabling production use. See [GitHub deployment review availability](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/review-deployments).

Add these **environment variables**, not secrets:

| Variable | Value |
| --- | --- |
| `AWS_REGION` | Actual instance region (existing inventory says `ap-southeast-2`; verify) |
| `AWS_ACCOUNT_ID` | Actual twelve-digit account ID |
| `AWS_INSTANCE_ID` | Exact production `i-` plus 17 hexadecimal digits |
| `AWS_DEPLOY_ROLE_ARN` | `arn:aws:iam::ACCOUNT_ID:role/RaceBibGitHubDeploy` |

The deploy job has contents/actions/packages read and id-token write. There are no AWS keys, SSH keys or database secrets. Grant this repository Actions read access to **both** private GHCR packages. Keep release immutability enabled. Retain release assets, workflow/job records and digest objects throughout the rollback window. The existing private-repository and root Docker authentication requirements in [aws-ec2.md](aws-ec2.md) still apply; the OIDC AWS role does not grant GitHub or GHCR access.

## AWS setup (administrator, never the deployment role)

Replace `ACCOUNT_ID`, `REGION`, `INSTANCE_ID` in copies of the JSON templates in `deploy/aws/`. These placeholders are intentional; never upload unresolved templates. Use an authenticated administrator AWS CLI session and the actual region. Commands below are setup instructions, not commands run during implementation.

1. In IAM add OIDC provider `https://token.actions.githubusercontent.com`, audience `sts.amazonaws.com` (reuse an existing identical provider). The [trust template](../../deploy/aws/github-trust.json) requires exact audience and `repo:vreboton/racebib:environment:production`. An environment subject does not also constrain the branch: the GitHub environment main-only rule is essential. Follow [GitHub OIDC in AWS](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-aws).
2. Create `RaceBibGitHubDeploy`, maximum session duration 7200, from rendered trust/permissions files:

```bash
aws iam create-role --role-name RaceBibGitHubDeploy --max-session-duration 7200 --assume-role-policy-document file://github-trust.rendered.json
aws iam put-role-policy --role-name RaceBibGitHubDeploy --policy-name RaceBibRestrictedDeployment --policy-document file://github-permissions.rendered.json
```

The permissions template allows `ssm:SendCommand` only on the exact custom document ARN **and** EC2 instance ARN. It cannot use `AWS-RunShellScript`, Session Manager, create/update documents, pass roles, cancel commands, read secrets or change IAM. `ssm:GetCommandInvocation` requires wildcard resource support and is read-only; isolate this role/account accordingly. Do not attach other policies that broaden the role. Restrict administrator document updates and deletion: the workflow explicitly requests version `1`, never `$LATEST`. A replacement document name/version requires a reviewed policy/workflow change. AWS policies cannot protect against a privileged account administrator replacing infrastructure.

3. Attach an EC2 instance profile. Reuse its existing role if appropriate; do not replace unrelated production permissions without review. For a new role use `instance-trust.json` (EC2 service trust), `instance-permissions.json`, then:

```bash
aws iam create-role --role-name RaceBibSsmInstance --assume-role-policy-document file://deploy/aws/instance-trust.json
aws iam put-role-policy --role-name RaceBibSsmInstance --policy-name RaceBibSsmChannels --policy-document file://deploy/aws/instance-permissions.json
aws iam create-instance-profile --instance-profile-name RaceBibSsmInstance
aws iam add-role-to-instance-profile --instance-profile-name RaceBibSsmInstance --role-name RaceBibSsmInstance
aws ec2 associate-iam-instance-profile --region REGION --instance-id INSTANCE_ID --iam-instance-profile Name=RaceBibSsmInstance
```

The minimal instance policy grants registration/heartbeat and modern `ssmmessages` control/data channels; these operations require wildcard resources. It grants no AWS secrets, S3, registry or database access. Inventory, patching, legacy `ec2messages`, CloudWatch logging and other optional SSM features are deliberately not granted. If existing management needs `AmazonSSMManagedInstanceCore`, review its broader managed policy separately. Require IMDSv2. Root inside a container/host is still a powerful boundary; do not trust container compromise merely because this role is narrow.

4. Verify/install the official SSM Agent for the actual Ubuntu release/architecture; do not blindly replace a working installation. Require agent **3.3.2746.0 or newer**, running at boot, supporting `ENV_VAR`. The document has no unsafe interpolation fallback; an unsupported agent must fail. See [AWS parameter handling](https://docs.aws.amazon.com/systems-manager/latest/userguide/parameter-troubleshooting.html). Permit outbound DNS/HTTPS to regional `ssm` and `ssmmessages` endpoints (or correctly configured VPC endpoints), GitHub API/assets and GHCR, plus existing application dependencies. SSM needs no inbound port. Verify official agent support and operation on Ubuntu 26.04.1 in staging; do not infer support from installation alone. Do not open SSH to hosted runners.
5. Through an existing protected administrator session, copy only these three reviewed additions into the installed root-owned script directory; the current engine/Compose files need no replacement:

```bash
sudo install -o root -g root -m 0644 deploy/scripts/ssm_deploy.py deploy/scripts/ssm_entry.py deploy/scripts/ssm_bootstrap.py /opt/racebib/scripts/
```

The full existing installer also copies these Python files, but selectively installing these additions avoids replacing production engine/configuration during transport setup. Verify `/opt/racebib`, scripts and state are root-owned, not group/world writable; state is mode 0700. The isolated Python bootstrap ignores inherited Python paths. Keep existing root-owned mode-0600 `github-auth.json` and root Docker credential helper/login for private images; verify repository contents/actions read and packages read. Expiring GitHub/GHCR credentials still require independent rotation; AWS OIDC does not remove that requirement. Never transmit credentials through SSM parameters or Actions logs.
6. Register the document in the instance region:

```bash
aws ssm create-document --region REGION --name RaceBib-DeployVerifiedRelease --document-type Command --document-format JSON --content file://deploy/aws/deploy-document.json
aws ssm get-document --region REGION --name RaceBib-DeployVerifiedRelease --document-version 1 --document-format JSON
aws ssm describe-instance-information --region REGION --filters Key=InstanceIds,Values=INSTANCE_ID
```

Compare returned content to the reviewed JSON and confirm Online agent/version. If the name already exists, inspect it; do not overwrite or silently use another version. Validate the same document/entry point against a disposable staging instance first. The production role must not have document update rights. CloudTrail records role/session and command submission; restrict SSM result visibility and log retention. Root-only detailed deployment logs can contain sensitive operational information.

## First controlled deployment

1. Complete review, local/CI checks and separately authorized merge to main. This implementation does not merge or push. Publishing after a successful main build creates releases but never deploys them.
2. Complete AWS/GitHub setup above and staging rehearsal. Confirm independent approval is enforced, rejected approvals execute nothing, wrong branch/instance/document are denied, and an unsupported/missing agent fails before any mutation. Keep old forced-command SSH configured until SSM is proven; then rotate/revoke the CI SSH key and remove obsolete `DEPLOY_*` secrets through a separately approved access cleanup. Preserve administrator recovery access.
3. Via protected administrator access inspect `/opt/racebib/state/release-state.json`: idle barrier, expected current SHA, previous release, recent backup and sufficient disk space. Validate `https://racebib.vreboton.com/health`, normal administrator login and the configured HTTPS base URL. Confirm off-instance backup availability. Never print `.env` or full Compose configuration.
4. Select an **already published, independently verified immutable** full source SHA. Verify with `python3 -B deploy/scripts/release.py verify SHA` in an authenticated trusted checkout; use `--release-tag racebib-SHA-recovery-RUN-ATTEMPT` only where the existing recovery protocol requires it. For the reported baseline `899f0d6a22cdafad3a691c52d3956ffbeb2bbcee`, inspect its actual release first; do not assume the canonical tag is immutable or invent a recovery tag. If schema differs, stop and arrange separately reviewed/approved migrations through the existing operator procedure. There is no migration option in this workflow/document.
5. Dispatch from main (UI Actions → deploy-production-manual → Run workflow), or:

```bash
gh workflow run deploy-production.yml --repo vreboton/racebib --ref main -f source_sha=VERIFIED_FULL_SHA
# For an existing recovery release, also pass: -f release_tag=EXACT_VERIFIED_RECOVERY_TAG
```

6. Independent reviewer checks SHA/tag/provenance, schema compatibility and backup/recovery plan, then approves `production`. Watch the Actions run and recorded SSM command ID. Submission happens once; API errors never resubmit automatically. SSM delivery timeout is 120 seconds, remote execution 3600, observation 3900, job 80 minutes and AWS session two hours.
7. Require successful remote engine/state verification and runner HTTPS health. Independently inspect the host's recorded current SHA/manifest digest, absent `inflight`, internal readiness and login. For first use, retain command ID/run URL and before/after state. A liveness response alone is insufficient. Review and retain backups before marking the rollout accepted.

## Failure, state inspection, manual recovery and rollback

Use administrator SSM Session Manager if separately authorized, or existing restricted administrator SSH. The GitHub deploy role intentionally cannot open sessions or execute arbitrary diagnostic shell commands. The document emits only current SHA, maintenance presence/stage; detailed output is root-only `/opt/racebib/state/ssm-deployment-*.log`. Do not copy raw logs to GitHub. Administrators can inspect:

```bash
aws ssm get-command-invocation --region REGION --command-id COMMAND_ID --instance-id INSTANCE_ID
sudo cat /opt/racebib/state/release-state.json
sudo docker ps --all --filter label=com.docker.compose.project=racebib-production
```

Before the maintenance barrier, verification/pull failures leave the current service running. After it, existing engine failure handling stops writers and retains `inflight`; ordinary deployment and rollback refuse to proceed. Cancellation, delivery/execution timeout, runner/network loss and a lost SendCommand response leave the remote outcome uncertain. **Do not immediately rerun, cancel remotely, restart writers, delete state, restore over data or auto-rollback.** Inspect command status, host lock/processes, named maintenance container and durable state first. A hard agent/host kill can interrupt normal cleanup; keep access restricted and verify writers are stopped before recovery. An external HTTPS failure after a successful engine commit may be a network/routing failure; it does not imply the recorded deployment should be rolled back.

After investigation and separate operator approval, use the existing procedure:

```bash
sudo bash /opt/racebib/scripts/recover.sh VERIFIED_SHA --reason 'Reviewed interruption and verified the selected target and database'
# Add --release-tag EXACT_RECOVERY_TAG when required by that release.
# Only under a separate migration approval: recover.sh ... --resume-migrations --reason '...'
```

For an idle deployment with a compatible previous successful schema, separately approve and run `sudo bash /opt/racebib/scripts/rollback.sh`. This re-verifies the exact previous manifest and schema and takes a backup. It refuses a barrier or schema mismatch. Resolve any barrier through reviewed recovery first. For incompatible schema, use a forward fix or the documented checksum-verified clean-target restore on a replacement host; never downgrade schema/history or erase volumes. See [existing recovery and rollback runbook](aws-ec2.md). Direct manual ordinary deployment remains `sudo bash /opt/racebib/scripts/deploy.sh VERIFIED_SHA` with optional verified recovery tag; migration approval remains separate from routine deployment.

## Validation boundary and remaining risks

See [milestone verification](milestone-2.4.2-verification.md) for actual results. Live OIDC, IAM authorization, protected-environment approvals, agent compatibility and SSM interruption behavior require staging/AWS checks; local unit tests do not prove them. Approval availability on the private repository is a first-deployment blocker until confirmed. Private package/token expiry or deletion of retained provenance/images deliberately prevents deployment/rollback. AWS administrators, main workflow writers and root can change the trusted boundary; restrict and audit them. Public health checks require the runner to reach HTTPS; restricted production ingress may require a trusted runner/network without exposing SSH. The existing framework/dependency risks and single-host availability limits remain outside this transport milestone.
