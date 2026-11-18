# Milestone 2.4.3 — Windows local deployment controller

This controller runs interactively from the trusted Windows 11 PC checkout at `D:\xtrail\racebib`. It is not a GitHub Actions runner. Do not register or install a runner/service, create runner tokens or allow PR code to run on this PC. GitHub-hosted build/MySQL/frontend CI, immutable image publishing and `test-production-approval.yml` remain unchanged. The old SSM workflow is retained with its original steps and permissions but an impossible event condition: it accepts only `workflow_dispatch`, while its job requires `disabled_ssm_transport`, which GitHub never supplies for that trigger. It is explicitly named `INACTIVE-SSM`; no AWS call/job is executed. SSM scripts/templates are preserved for a separately reviewed future account upgrade.

## Actual interfaces inspected and setup blockers

Current main was fetched/inspected at `94a2651`. Its SSH protocol is `deploy FULL_SHA [--migrate] [--release-tag RECOVERY_TAG]`; it does not return an independently checkable release-state receipt. The existing verifier API `verify_release()` checks immutable release/assets, main-source/tag provenance and exact successful build/test/safety/publication attempts. Its canonical path does not check live registry objects; the new local preflight **always also calls `verify_registry_images()`**, comparing both immutable OCI digests, original publication-attempt tags, Linux/amd64 configuration and source labels with GET requests only. No image is executed or rebuilt locally.

The controller reuses that verifier and the existing root deployment engine. Before first use, a separately authorized administrator must install this branch's restricted dispatcher and receipt entry point. An older installed dispatcher may still permit migrations, and an older wrapper cannot supply the required receipt. An old dispatcher can execute the deployment before the client rejects its missing receipt: never submit a release merely to probe compatibility. Inspect the installed root entry independently first. Do not treat that incompatibility as success or fall back to unchecked SSH output. No production installation is performed by this implementation or its tests.

The revised forced command still accepts the same ordinary `deploy SHA [--release-tag TAG]` syntax. It rejects migration/recovery/rollback/arbitrary commands and invokes only `/usr/bin/sudo -n /usr/local/sbin/racebib-ssh-deploy`. That root-owned wrapper uses isolated Python and calls the installed `deploy.sh`. The engine repeats provenance checks and owns locking, digest pulls, database baseline, backups, durable maintenance state, exact schema/readiness checks, HTTPS routing and atomic successful-state/history recording. The engine, release verifier and Compose files are unchanged.

## Authorization: local operator only

**GitHub production environment approval does not authorize this controller.** No signed/external approval artifact, independent approver or verifiable GitHub-to-local authorization bridge is implemented. The production approval-test workflow proves only its own environment behavior.

The controller first validates configuration/key ACLs and exact host pin, independently verifies the release and registry, displays the verified SHA/tag, manifest digest and target, then requires the operator to type exactly `DEPLOY FULL_SHA sha256:MANIFEST_HASH`. There is no `-Yes`, migration, force, retry or noninteractive-approval option. After confirmation, configuration, ACLs and the host pin are revalidated; any changed configuration stops before SSH. After confirmation, the controller revalidates configuration, ACLs and the host pin and refuses any changed configuration before submission. Mismatch/cancellation stops before submission. This prevents accidental local submission; it is not two-person control or proof against a compromised operator account. Possession of the restricted private key and host authorization is the actual SSH authority, and a user with that key can invoke SSH independently of the controller. If organizational policy requires independent approval, keep local deployment disabled until a separately designed and tested authorization mechanism exists. Record human approvals outside GitHub's unrelated approval test as required by local operating policy; do not misrepresent them as cryptographically enforced.

## Windows prerequisites and credential storage

Use a maintained PowerShell 7.4 or newer release (`pwsh`), official Python >=3.11 installed at an absolute trusted path (not a Windows Store execution alias), Windows OpenSSH client (`C:\Windows\System32\OpenSSH\ssh.exe`), current Windows trusted roots and Git. Python uses only the standard library; no AWS CLI, Docker, .NET SDK, npm, GitHub runner or OpenSSH **server on the PC** is required for deployment. GitHub CLI/credential-manager tooling is optional for obtaining approved read-only API/package tokens.

Use a trusted local Windows operator account, preferably standard-user, with no unrelated cloud/administrator deployment key available to routine tooling. Do not run deployment as SYSTEM or install a service. Protect/review the controller checkout before execution; local scripts are trusted code, not signed artifacts. Never run it from an unreviewed PR checkout. Keep private keys/configuration/known_hosts outside `D:\xtrail\racebib` and outside synced/shared folders, for example `C:\Users\OPERATOR\.racebib-deploy`. Protect the directory and all ancestors from other users' writes/deletion; protect all three files with explicit ACLs. Local administrators and SYSTEM remain trusted Windows security principals; ACLs do not defend against their compromise.

The controller requires file owner = current operator, inheritance disabled, and allow entries only for that operator, SYSTEM and local Administrators. It also rejects repository paths, reparse points/junctions/symlinks and another identity's write access to the immediate parent, replacement/delete/control access along the entire directory ancestry, and untrusted ancestor owners. Null DACLs are rejected. Files and Python must use fixed local drives; UNC/mapped-network paths are rejected before access. Key/known_hosts paths may not contain OpenSSH `%` or `${...}` expansion tokens. Do not loosen ACLs to resolve a rejection. Verify filesystem ownership and paths, especially when running as a different account or when the checkout/credential folder has been moved.

Manual setup pattern (instructions only; substitute the protected directory and operate under its owner):

```powershell
$credentialDirectory = Join-Path $env:USERPROFILE '.racebib-deploy'
New-Item -ItemType Directory -Path $credentialDirectory -ErrorAction Stop
$operatorSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$directoryAcl = [Security.AccessControl.DirectorySecurity]::new()
$directoryAcl.SetOwner($operatorSid)
$directoryAcl.SetAccessRuleProtection($true, $false)
$directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
    $operatorSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $credentialDirectory -AclObject $directoryAcl
```

Generate a **new dedicated Ed25519 deployment key**, never copy/reference the EC2 administrator PEM:

```powershell
& "$env:WINDIR\System32\OpenSSH\ssh-keygen.exe" -t ed25519 `
    -f (Join-Path $credentialDirectory 'deployment_ed25519') -C 'racebib-local-deployment'
```

The controller disables agent use and interactive SSH authentication. This design uses an unencrypted dedicated key with restrictive ACLs; an encrypted key requiring a prompt will fail closed, not silently use an administrator agent key. Accept the at-rest-key theft tradeoff deliberately, restrict its server command/source and rotate it. Do not print the private key or place it into configuration JSON, environment variables, repository files, clipboard history, CI secrets, transcripts, artifacts or persistent workspaces. The controller references the existing protected key directly and makes no key copy.

Copy `deploy/windows/config.example.json` to `controller.json` inside the protected directory and fill in the actual trusted Python path, SSH host/port, fixed user `racebib-deploy`, private-key path, pinned known_hosts path and independently confirmed `HostKeyFingerprint`. The example contains placeholders and cannot be used as-is. Configuration contains references and a public fingerprint, not credentials, but must still have protected integrity.

Obtain the **server's Ed25519 host key and SHA256 fingerprint independently** through trusted administrator access or previously established trust. An unverified `ssh-keyscan` output is not an authorization to trust a key. Store exactly one known_hosts entry: `HOST ssh-ed25519 BASE64_KEY` for port 22, or `[HOST]:PORT ssh-ed25519 BASE64_KEY` otherwise. Match the configured hostname exactly; no wildcard, hashed-host alternative, extra host, certificate authority or automatic host-key update. Local `ssh-keygen -lf known_hosts -E sha256` can display the collected key's fingerprint for comparison; it does not contact EC2 or independently establish trust.

After creating/editing each credential/configuration file, assign a protected owner-only ACL explicitly (do not rely on inherited mode flags or `chmod`):

```powershell
foreach ($name in @('deployment_ed25519','known_hosts','controller.json')) {
    $path = Join-Path $credentialDirectory $name
    $fileAcl = [Security.AccessControl.FileSecurity]::new()
    $fileAcl.SetOwner($operatorSid)
    $fileAcl.SetAccessRuleProtection($true, $false)
    $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($operatorSid, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $path -AclObject $fileAcl
}
```

Some Windows configurations require privileged help to repair ownership. Do not run routine deployment as administrator merely to bypass an ACL problem. The Windows tests use synthetic temporary files and `icacls` to exercise rejection; they do not edit a real credential.

Public repository access is independent of GHCR package visibility and GitHub API/job-log access/rate limits. Supply approved expiring read-only `GH_TOKEN` and, where needed, `GHCR_TOKEN` plus `GHCR_USERNAME` in the **current process environment** from an approved credential manager/authenticated CLI flow. Do not paste literal tokens into commands or configuration. Do not persist them as machine/user environment variables or in profiles/transcripts. The Python verifier uses them only for read-only HTTPS requests and strips credentials on redirects as before. The SSH child environment excludes these tokens. The root wrapper removes caller tokens and shell/Python startup overrides and fixes its executable search path, relying on protected host credentials. The root wrapper also strips caller tokens and shell/Python startup overrides and fixes its executable search path; it relies on protected host credentials. Remove them from the session after use and close the shell; rotate revoked/expired tokens separately from SSH keys. Tokens cannot approve deployment.

## One-time server setup by an independent administrator

These commands are documented for a later approved maintenance/setup session and were not executed. Preserve existing administrator access, `.env`, engine/Compose, backups, volumes and state. Review the installed version first; do not replace unrelated production files using a blanket installer.

```bash
sudo install -o root -g root -m 0644 deploy/scripts/transport_common.py deploy/scripts/ssh_entry.py /opt/racebib/scripts/
sudo install -o root -g root -m 0755 deploy/scripts/ssh-deploy.sh /usr/local/sbin/racebib-ssh-deploy
sudo install -o root -g root -m 0755 deploy/scripts/ssh-dispatch.sh /usr/local/sbin/racebib-ssh
```

All parent directories/entry points must remain root-owned/not group/world writable; state is root-only 0700. Provision dedicated account `racebib-deploy` with no password authentication, Docker/privileged groups, writable shell startup files or alternate unrestricted keys. Its home/startup files and authorized-key file must be root-controlled. Use sshd user restrictions to force `/usr/local/sbin/racebib-ssh`, require public-key authentication, forbid TTY, agent/TCP/X11/tunnel forwarding, and prevent user-controlled environment configuration. Validate effective configuration and `sshd -t` before any administrator reload; retain a separate recovery session.

The dedicated public key must have `from="EXISTING_TRUSTED_PC_EGRESS_IP/32",restrict,command="/usr/local/sbin/racebib-ssh"` restrictions. Consider a supported reviewed expiry-time limit. Grant in sudoers **only** `racebib-deploy ALL=(root) NOPASSWD: /usr/local/sbin/racebib-ssh-deploy`. Remove the older grant to the unrestricted `/usr/local/sbin/racebib-deploy`; check `sudo -l -U racebib-deploy` and default environment reset/no SETENV. Direct sudo invocation of the constrained entry must also reject migration/recovery/arbitrary options. Server-side GitHub/GHCR read credentials and root Docker authentication stay protected on EC2 as previously documented; none is sent through the client.

Test restrictions on a disposable staging host before production setup: wrong source/key, arbitrary shell, TTY, SFTP/SCP, forwarding, multiline/separator injection, migration flags, bad tag, mutable release, failing readiness and interrupted state. The root wrapper returns a receipt only after engine success and validated current/history/schema/idle state; the controller compares exact metadata to its independently verified release. A fake old-style success string or SSH exit zero is insufficient.

## Network and first controlled local use

Windows needs outbound HTTPS to GitHub/API/assets/GHCR and the existing production HTTPS health endpoint, plus outbound SSH to the fixed EC2 address/port. It needs no inbound OpenSSH server, GitHub webhook or runner firewall rule. Keep the existing EC2 SSH and HTTPS source restrictions. Confirm the PC's actual public/VPN egress is **already authorized**. If a home ISP/VPN changes the address, stop; do not modify security groups, open `0.0.0.0/0`, dynamically whitelist the PC, or use hosted-runner ranges. Obtain a separately reviewed network arrangement outside this task.

After the protected configuration, dedicated key and reviewed server receipt entry are installed and staged successfully:

1. Inspect host idle state, current/previous releases, backup/off-instance copies, space and existing schema/HTTPS readiness through independent administrator access. Confirm provenance evidence and both image digests are retained.
2. Open trusted `pwsh -NoProfile` in `D:\xtrail\racebib` and use the reviewed controller:

```powershell
.\deploy\windows\Invoke-RaceBibDeployment.ps1 -SourceSha VERIFIED_FULL_LOWERCASE_SHA `
    -ConfigPath (Join-Path $env:USERPROFILE '.racebib-deploy\controller.json')
# Only for an existing verified recovery release, additionally pass:
# -RecoveryTag racebib-SAME_SHA-recovery-EXISTING_RUN-ATTEMPT
```

3. Verification is mandatory before the local confirmation prompt. Check the displayed SHA, exact selector, manifest and target against the intended change, then type the required phrase. No GitHub approval status is queried or trusted as local authorization. There is no automatic/noninteractive bypass.
4. Require the structured result `HostState=verified` **and** `Https=verified`. The receipt must match SHA/tag, immutable manifest digest, API and web image digests, and a valid schema hash. HTTPS uses the fixed public domain, Windows CA/hostname verification, exact HTTP 200, zero redirects/retries and no certificate bypass. HTTP 200 alone never proves release/schema readiness. The existing engine's internal database-backed readiness/TLS checks remain mandatory.
5. Check real login/business behavior from the trusted PC as appropriate. Keep non-secret result, release identifier and independent operational approval records in a protected audit location; do not save raw remote logs or tokens. Clear process token variables and close the shell.

## Shutdowns, failures and recovery

Before confirmation/SSH submission, failure or PC shutdown submits nothing. After submission starts, a timeout, Ctrl+C, PC shutdown/sleep, network loss or lost/malformed receipt makes the remote outcome **unconfirmed**, even if Windows reports exit zero or later HTTPS returns 200. The remote process may still be running, may have committed, or may have retained a maintenance barrier. The root wrapper starts the engine in a separate session with inherited SIGHUP ignored, so a routine SSH hangup can let the single operation finish safely rather than abruptly terminating it. This does not survive every hard kill, reboot or service-manager termination and never permits an automatic retry. The controller makes one SSH attempt and never automatically retries, migrates, rolls back, restores, clears state or restarts writers.

After ambiguous outcomes, use independent administrator access to inspect `/opt/racebib/state/release-state.json`, current manifest/SHA/digests, latest history/schema, host process/operation lock, root-only `ssh-deployment-*.log` and named maintenance containers. Do not rerun the controller or treat an old receipt as new confirmation. If the requested release was committed and is healthy, record the completed operation without resubmitting. If the engine is still running, wait/inspect under the normal operations procedure. If maintenance is retained, investigate and use separately approved `recover.sh` with meaningful reason; resume migrations only under separate explicit approval. Do not clear the barrier manually.

When a valid host receipt was received but external HTTPS fails, the controller reports **host deployment/state verified, external HTTPS unconfirmed**. Preserve that distinction. Investigate DNS, certificate/routing and trusted-IP firewall access; do not redeploy or roll back automatically. Rollback still requires idle state, the previous distinct immutable manifest and exact compatible schema. Incompatible schema needs a forward fix or approved clean-target restore on a replacement target. See [existing recovery safeguards](aws-ec2.md).

## Rotation, cleanup and verification boundary

Rotate the dedicated client key by generating a new distinct protected key, installing its equally restricted public key alongside the old one through administrator access, updating the protected client key path, verifying staging/controlled local use, then removing the old authorized key and securely retiring old copies. On suspected theft revoke first, investigate the PC/host, then restore service deliberately. For legitimate EC2 host-key rotation, independently verify the new server key/fingerprint and update both protected pin files/configuration; never accept a mismatch automatically.

No runner registration, service, workspace credential file or automatic key copy exists to clean up. Tests remove only their own absolute, verified temporary fixture directories. The real key remains intentionally outside the repository under ACL protection until rotated/revoked; deleting a local key alone does not revoke server authorization. Use BitLocker, screen locking, patched software and a controlled local account; administrators/malware running as the operator can still steal the key or bypass confirmation. Avoid sleep/shutdown during a deployment for operational clarity, but never assume the PC staying awake proves success.

Run the safe local rehearsal with `pwsh -NoProfile -File .\deploy\tests\Test-LocalDeployment.ps1`. It exercises real synthetic Windows ACLs and `ssh -G` configuration parsing only; all verification-submission/HTTPS calls are mocked. The Python deployment suite mocks live GitHub/registry checks and does not contact production. The separate Windows GitHub-hosted safety workflow executes only this mock rehearsal, with contents-read permissions and no environment, credentials or runner registration. See [actual verification](milestone-2.4.3-verification.md) for results and rollout prerequisites.

Final audit hardening: executable paths are checked for trusted ownership and unauthorized write permissions as well as top-down directory/reparse validation. Use an ordinary installed Python whose ACLs can be inspected; do not bypass checks for a protected app alias. Credential path checks walk from the drive root down before following each next component, preventing a static junction from being followed before rejection. Receipt and preflight JSON must contain exactly the expected unique string fields; duplicate/extra fields are rejected.
