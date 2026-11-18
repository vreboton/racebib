# Milestone 2.4.3 local-controller verification — November 18, 2026

Implemented locally on `feature/milestone-2.4.3-local-controller`, preserving prior local work and inspecting fetched `origin/main` at `94a2651`. No push, merge, deployment, AWS/GitHub setting change, production-file change, real credential change or runner registration occurred. Unrelated preexisting untracked files remain untouched.

## Actual checks

| Check | Result |
| --- | --- |
| Windows PowerShell rehearsal | 84 assertions passed: real synthetic ACL/owner/inheritance checks, unsafe key/PEM/endpoint rejection, exact Ed25519 host pin, local Windows OpenSSH `-G` parser and mocked authorization/preflight/SSH/receipt/HTTPS scenarios |
| Linux Python deployment suite | 77 passed, no skips, including six new local-preflight/host-state/receipt tests and existing release/state/rollback safety coverage |
| All workflow actionlint/shellcheck | Passed: inactive SSM workflow plus new GitHub-hosted Windows mock-only checks |
| PowerShell parsing/import | Passed on PowerShell 7.6.9; Windows OpenSSH 9.5p2 accepted strict options and paths with spaces without connecting |
| Operational Bash syntax / whitespace | Passed |
| Trail graph/check | Refreshed; check reported two lexical documentation conflicts against older runner/ingress wording, reviewed as false positives: current text prohibits runner registration/ingress changes and separates host state from HTTPS. Not reported as a clean Trail check |
| Existing application CI, image publishing, approval test, engine/verifier/state/Compose diff against main | Unchanged |
| Application tests | Re-run in final audit: 42 backend tests, all six disposable MySQL integration tests, eight frontend tests and production build passed; isolated MySQL and trusted-TLS/ACME rehearsals passed; existing bundle-size warning remains |

The Windows rehearsal uses only synthetic temporary files outside the checkout, tests actual ACL rejection using icacls, and removes only its verified absolute UUID fixture directory. It required the actual operator identity outside sandbox ACL virtualization. A test-fixture Set-Acl privilege issue was corrected with icacls; production ACL checks were not weakened. All orchestration native/preflight/SSH/HTTPS calls are mocked. The only actual SSH process is `ssh -G` against a synthetic .invalid host, which prints local configuration without connecting. Python API/registry tests use mocks. No test contacts production.

## Inspected interfaces and resulting behavior

Actual main's SSM workflow and old SSH dispatcher were inspected before implementation. The old dispatcher accepts migration flags and does not provide release-state proof. The new fixed root wrapper is a separate, independently installed prerequisite; it invokes the unchanged engine, refuses migration/recovery flags and emits exact release/manifest/API/web/schema evidence only after successful idle current/history validation. The Windows client compares that evidence with independently verified metadata before checking normal trusted-PC HTTPS. SSH zero or HTTP 200 alone cannot prove success.

The local preflight calls the existing immutable provenance verifier and always checks both live OCI objects, platform/config/source labels and original publication-attempt tag digests. No release is rebuilt or published. Credentials stay in approved process environment/outside-repository ACL-protected files; GitHub read tokens are excluded from SSH's environment. No raw native stderr is printed. Missing/wrong receipt, SSH failure or timeout remains an unconfirmed remote outcome, with no automatic retry, rollback, migration, restoration, barrier clearing or ingress changes.

Authorization is explicitly local operator confirmation of the verified SHA/manifest/target. There is no GitHub-to-local approval bridge, independent/two-person enforcement or claim that the production approval-test workflow authorizes local execution. A holder of the restricted key remains capable of direct SSH; Windows malware/administrators/operator compromise remains a trust boundary.

The SSM workflow body and permissions remain for audit, with an inactive name and a job requiring an event impossible under its dispatch-only trigger. Literal `if: false` was replaced because actionlint flags a constant condition; the impossible event retains inactivity without a credential/setup-dependent switch. No controller or runner job is wired into that workflow. Existing build/publish/approval workflows are intact. A separate Windows GitHub-hosted workflow runs only mock checks, contents read, no environment or credentials.

## Changed files

- `.github/workflows/deploy-production.yml`: archived inactive SSM body; `.github/workflows/local-controller-safety.yml`: hosted Windows mock tests.
- `deploy/windows/Invoke-RaceBibDeployment.ps1`, `RaceBib.Deployment.psm1`, `config.example.json`: local entry, Windows controller and placeholder-only configuration.
- `deploy/scripts/local_verify.py`, `transport_common.py`: mandatory provenance/registry checks and strict SHA/recovery validation.
- `deploy/scripts/ssh_entry.py`, `ssh-deploy.sh`, `ssh-dispatch.sh`, `deploy/install-server.sh`: constrained root entry, exact receipt and install support; existing engine unchanged.
- `deploy/tests/Test-LocalDeployment.ps1`, `test_local_controller.py`, `test_publication_recovery.py`, `test_ssm.py`: ACL/mock/preflight/receipt/forced-command safety checks and archived-workflow assertions.
- Historical SSM code docstrings, `deploy/aws/README.md`, `docs/deployment/github-actions-ssm.md`, `milestone-2.4.2-verification.md`: inactive/historical labels.
- `docs/deployment/aws-ec2.md`, `windows-local-deployment.md`, `github-actions-ssh.md`, this report and the new Trail learning: current local procedure and superseded-runner clarification.

The earlier uncommitted self-hosted runner proposal/client and custom runner-label configuration were removed from active implementation. No runner is registered or required.

## Rollout blockers and limits

Real SSH authentication, installed sshd/sudoers rules, actual server host key, live provenance/package access, production release state/HTTPS/login and interrupted production behavior were not tested. Before first use, independently verify/install the exact receipt-emitting root entry, remove the broad sudo grant, configure the dedicated source/command-restricted key and Windows ACL-protected pin/config, confirm existing trusted-PC ingress, and rehearse on staging. An old dispatcher can deploy before its missing receipt is rejected; never use deployment as a capability probe. If independent approval is mandatory policy, rollout remains blocked until a separately designed/tested mechanism exists. See [setup and safe recovery](windows-local-deployment.md).

Trail graph was refreshed. `trail check` reported two lexical conflicts on the updated runbook headers, matching historical warnings about hosted runners/external probes. These headers explicitly remove runner execution and retain host-state-plus-HTTPS verification; neither opens ingress, retries, rolls back or treats external health as release proof. The findings were reviewed as documentation keyword false positives, not reported as a clean Trail pass. Whitespace and PowerShell/Bash parsing checks passed.

## Final security audit against main

Fetched main remains `94a2651`; branch is `feature/milestone-2.4.3-local-controller`. Reviewed every staged Milestone file, including earlier untracked work. `deploy/scripts/ssh_deploy.py` and `.github/actionlint.yaml` were untracked files from the superseded runner prototype; neither is tracked in main or its inspected history. They are absent in the final design, so this is not a deletion of a main-branch client or lint configuration. The hosted Windows workflow has no custom runner labels and passes actionlint without that file.

Confirmed issues fixed during the audit:

1. File/immediate-parent checks alone missed replacement rights or untrusted ownership higher in credential ancestry. Check every ancestor, reject null DACLs, and reject UNC/network/removable paths before filesystem access. Skip inherit-only ACEs that do not grant rights on the current directory. Windows fixture tests exercise an unsafe grandparent and restore only their synthetic ACL.
2. Quoted OpenSSH filenames still expand percent/environment tokens. Reject those tokens in both identity and known_hosts paths; keep exact fingerprint, forced Ed25519 host algorithm, isolated SSH config, no agent/forwarding/TTY/control reuse and one connection attempt.
3. Configuration could change while awaiting confirmation. Snapshot the approved values, revalidate configuration/ACLs/pin after confirmation, and reject any change before SSH. Tests cover endpoint changes and pin/ACL rejection at that point.
4. Generic JSON conversion silently accepted duplicate property names. Preflight and receipt now require exact unique string fields; malformed, duplicate, extra/missing and mismatched evidence fail before success/HTTPS.
5. The original PowerShell 7.0 declaration was broader than the runtime APIs used. Require maintained PowerShell 7.4+ explicitly. Use the OS-known Windows directory for OpenSSH rather than an overridable environment path.

Defense in depth: the root wrapper clears shell/Python startup variables and caller GitHub tokens and fixes PATH before invoking the installed engine. The underlying engine is unchanged. Existing sudoers instructions grant only the constrained root wrapper and explicitly remove the old broad engine grant, require environment reset/no SETENV, root-owned paths, disabled account password/forwarding and source/forced-command key restrictions. Actual host rules cannot be certified without production inspection and remain a setup blocker.

All regression checks passed: 84 Windows mock/ACL/argument/evidence assertions, 77 Linux deployment tests, 42 backend tests, six disposable MySQL HTTP tests, eight frontend tests plus production build, isolated MySQL/HTTPS rehearsals, workflow actionlint/shellcheck, operational Bash/PowerShell syntax and whitespace checks. Existing application CI, immutable publishing, production approval test, release verifier, state engine, deployment engine, Compose and Nginx files are unchanged. No production connection or remote setting change occurred. The optional external LiveDocker backend test was excluded because no dedicated live environment was configured; no production target was substituted.

Local operator confirmation is still not independently verifiable GitHub/two-person authorization. Dedicated key/host pin/ACL setup, removal of broad sudo access, independently verified installation of the receipt entry and staging interrupted-SSH/recovery checks remain required. A disconnect/cancellation or missing receipt is unconfirmed, never grounds for automatic retry/rollback. Host-verified external HTTPS failure remains distinct from engine/state failure. No administrator PEM, runner, automatic migration, barrier clearing or ingress broadening is introduced.

Audit interruption fix: the SSH root wrapper starts the engine in its own session with SIGHUP ignored during spawn. A network-free synthetic child sends SIGHUP to itself and proves it remains alive/session leader. Actual hard-kill/reboot/cgroup behavior remains a staging boundary; the client still treats missing evidence as unconfirmed and never retries. No change was made to operations.py's engine or durable-state logic.

## Complete reviewed commit file list (28 files)

- .github/workflows/deploy-production.yml
- .github/workflows/local-controller-safety.yml
- .trail/learnings/2026-11-18-use-local-windows-authorization-with-registry-vreboton.md
- .trail/learnings/2026-11-18-use-restricted-ssh-with-verified-runner-vreboton.md
- .trail/learnings/2026-11-18-validate-credential-ancestry-and-openssh-literal-vreboton.md
- deploy/aws/README.md
- deploy/install-server.sh
- deploy/scripts/local_verify.py
- deploy/scripts/ssh-deploy.sh
- deploy/scripts/ssh-dispatch.sh
- deploy/scripts/ssh_entry.py
- deploy/scripts/ssm_bootstrap.py
- deploy/scripts/ssm_deploy.py
- deploy/scripts/ssm_entry.py
- deploy/scripts/transport_common.py
- deploy/tests/Test-LocalDeployment.ps1
- deploy/tests/test_local_controller.py
- deploy/tests/test_publication_recovery.py
- deploy/tests/test_ssm.py
- deploy/windows/Invoke-RaceBibDeployment.ps1
- deploy/windows/RaceBib.Deployment.psm1
- deploy/windows/config.example.json
- docs/deployment/aws-ec2.md
- docs/deployment/github-actions-ssh.md
- docs/deployment/github-actions-ssm.md
- docs/deployment/milestone-2.4.2-verification.md
- docs/deployment/milestone-2.4.3-verification.md
- docs/deployment/windows-local-deployment.md


Final path audit also validates local executable owners/write rights and checks credential directory components top-down, before following a reparse point. Windows tests include a synthetic local junction, unsafe executable ACL and untrusted ancestor replacement rights; OS-owned directories trust SYSTEM/Administrators/TrustedInstaller, while private files still require the operator owner and protected ACL. No executable or credential validation bypass was added.

Final staged-diff secret scan: gitleaks scanned approximately 112 KB with redacted output and found no leaks. Reviewed code is staged on the requested feature branch; this audit does not certify real production configuration. The complete 28-file commit list above is exhaustive; unrelated preexisting untracked files are excluded.
