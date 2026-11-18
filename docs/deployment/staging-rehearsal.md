# Temporary AWS staging rehearsal (Milestone 2.4.5)

Status: implemented for review; no AWS rehearsal has been performed. The inspected
`main` contains the Milestone 2.4.3 controller, not an identifiable 2.4.4 change.
This rehearsal cannot certify missing 2.4.4 changes or authorize production use.
Run it against a reviewed immutable published release after approval of this code,
the generated template, permissions and cost. GitHub environment approval tests
remain separate; local execution is authorized by the operator, not by that gate.

## Design and boundaries

`deploy/staging/Invoke-StagingRehearsal.ps1` is a separate Windows entry point.
CloudFormation is the lightweight provisioning/state engine. Every plan creates
a nonce-named stack in `ap-southeast-2`: its own VPC, public subnet, route table,
internet gateway, security group and Ubuntu 24.04 x86_64 `t3.small`. The instance
has 30 GiB encrypted gp3 root storage, delete-on-termination, standard CPU credits,
IMDSv2 required and no instance profile. It creates no NAT, EIP, load balancer,
DNS, RDS, S3, IAM or production resources. Only this PC's explicit public IPv4
`/32` can reach 22 and 443. Egress is TCP 443; Ubuntu repositories use HTTPS.
DNS uses the VPC resolver. No port 80 ingress is needed.

Tags: `Environment=racebib-staging`, `ManagedBy=racebib-rehearsal`,
`Temporary=true`, `StageRun=<nonce>`. Do not edit tags, stack resources or run
files during a rehearsal. Staging shares image digests, never production data,
secrets, keys or state. `/opt/racebib` and the existing Compose project name are
reused only on the new isolated VM, preserving the engine without parameterizing
its production roots. No script from this directory belongs on production.

Two fresh staging SSH identities are generated locally. `racebib-deploy` uses the
unchanged production forced-command protocol. `racebib-rehearsal` has a separate
staging-only forced control protocol for one-time reviewed bundle installation,
fresh schema initialization, a synthetic backup failure and explicit recovery.
Neither account has shell, arbitrary command, file-transfer or forwarding access.
The staging control is privileged: root marker, run nonce, IMDS instance ID and
public IP must match before it acts. It must never be installed on production.
The source bundle contains reviewed scripts/Compose/Nginx only, and its digest
is embedded in user data. Generated DB/JWT secrets stay on the disposable VM.

The PC obtains the new Ed25519 host key and public CA through authenticated AWS
`GetConsoleOutput`, after account, exact stack template, tags and instance/VPC
checks. It does not trust `ssh-keyscan`. A fresh one-day private CA signs the
staging hostname and public IP. Its private key stays on the VM. Windows uses
custom root trust for these staging requests only, with hostname checking, no
redirects and no proxy; no OS trust store is modified. The VM trusts its own CA
for the unchanged engine's HTTPS checks. Production host pins and public HTTPS
validation are untouched. This tests TLS plumbing, not public DNS/ACME renewal.

## Cost review before creation

The offline plan estimates **USD 0.32 for the default two-hour run**, including a
$0.25 contingency: `hours * (0.0264 + 0.005 + 30 * 0.096 / 730) + 0.25`.
Assumptions: Sydney Linux t3.small $0.0264/hour, public IPv4 $0.005/hour,
gp3 $0.096/GiB-month; no free-tier credit assumed, standard CPU credits, no paid
snapshots. These are planning rates, not a quote. Check regional rates/taxes,
egress, account eligibility and quotas before approval. See [EC2 T3](https://aws.amazon.com/ec2/instance-types/t3/),
[VPC pricing](https://aws.amazon.com/vpc/pricing/) and [EBS pricing](https://aws.amazon.com/ebs/pricing/).
The $1 default local ceiling rejects a larger estimate; it is **not a billing cap**.

Cloud-init schedules instance shutdown/termination after the configured 30–180
minutes (120 default), as a best-effort fuse. Bootstrap failure before scheduling,
clock/service failure or a stalled deletion can leave charges. Terminating the
instance alone does not remove the stack. Keep the PC awake and supervise until
cleanup verification succeeds. No always-on runner is installed or needed.

## Prerequisites and IAM

Use Windows PowerShell 7.4+, Windows OpenSSH Client, Python 3.11+ and AWS CLI v2
installed at `%ProgramFiles%\Amazon\AWSCLIV2\aws.exe` (the executor validates
this fixed executable and its ACL ancestry rather than resolving it from PATH).
Install Python in a trusted fixed local directory. Existing executable and ancestor
ACL checks apply. Avoid Downloads, Temp, network drives and writable shared tool
directories. Use a new owner-only run directory outside the checkout, cloud sync
and persistent CI workspaces. Run as the intended operator; administration is
not required and cannot bypass an unsafe ancestry ACL.

Create a short-lived AWS profile for a reviewed account (prefer a staging-only
account). `provisioner-policy.example.json` documents the required permissions;
replace `ACCOUNT_ID` and `AMI_ID` before separately reviewing/installing it.
It permits scoped CloudFormation creation/deletion, read-only discovery and
console output, and EC2/VPC operations constrained to staging-tagged resources.
It separates new resource tags from existing VPC/subnet/SG dependencies. It has
no IAM/PassRole, SSM, Route53, production ingress changes or stack-update rights.
Do not grant `AdministratorAccess`/`EC2FullAccess` to make a failed policy pass.
CloudFormation tagging/action dependencies require validation in your account;
an IAM denial is a blocker, not permission to widen production access.

Tagged stack creation additionally needs `cloudformation:TagResource`. The example
grants it only for the staging stack ARN prefix and `cloudformation:CreateAction=CreateStack`,
with the four expected request tag keys, staging ownership values and a 32-character
run identity. It grants no unrestricted stack retagging or updates. Creation now
submits all four ownership tags. Tests also check that the rendered example fits
the 6,144-character managed-policy limit. These allows do not override a broader
policy: use a dedicated least-privilege identity. Live CloudFormation tag context,
EC2 tag propagation and organizational restrictions still require an approved
account rehearsal; none has been performed. See [AWS tag-on-create authorization](https://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/security_iam_id-based-policy-examples.html).

Select a current Canonical-owned (`099720109477`) Ubuntu 24.04 amd64 server AMI
in Sydney; the executor independently checks owner, name, state and architecture.
Review its ID explicitly. Select a tested immutable `mysql:8.4@sha256:<64 hex>`
digest. The deployment release must already exist in GHCR, have successful
trusted main build/test provenance and a valid immutable manifest. Preflight
verifies provenance and API/web image digests before creating infrastructure.

Plan also requires `-ProtectedResourceIds`: independently obtain the production
instance and VPC IDs from existing approved inventory, plus all known protected
subnet, security-group, route-table, gateway, volume and interface IDs. At least an
instance and VPC ID are mandatory. This script never discovers production. The
denylist supplements positive per-resource ownership checks; do not invent IDs
or remove entries to get a failed check to pass. Older plans without this field
are incompatible: with no infrastructure created, make a fresh plan. For any
existing stack, preserve its original state and follow manual investigation below.

Provide NEW expiring staging-only read credentials in the invoking process:
`RACEBIB_STAGING_GH_TOKEN`, `RACEBIB_STAGING_GHCR_TOKEN`, and
`RACEBIB_STAGING_GHCR_USERNAME`. Use minimum GitHub Actions/contents and package
read access required by the existing verifier. Never reuse the production host
auth file, administrator PEM or production deployment key. Populate environment
values from your credential manager without literals in shell history or
transcripts. Tokens are sent through pinned encrypted SSH stdin, never CLI
arguments, local state or CloudFormation user data. Root-only staging files and
Docker auth contain them until VM deletion. Revoke them after the run and clear
the three process environment variables in a `finally` block.

## Execute after explicit infrastructure approval

From the reviewed feature checkout, first perform the offline plan. Replace all
placeholders; `$sha` must be exactly 40 lowercase hex characters. An optional
`-RecoveryTag racebib-$sha-recovery-N-N` must match the existing validated format.

```powershell
$run = Join-Path $HOME ('RaceBibRehearsal-' + [guid]::NewGuid().ToString('N'))
./deploy/staging/Invoke-StagingRehearsal.ps1 -Action Plan -RunDirectory $run `
  -SourceSha $sha -ExpectedAccount $stagingAccount -AwsProfile $stagingProfile `
  -OperatorCidr $pcPublicIpv4Cidr -AmiId $reviewedUbuntuAmi `
  -MySqlImage $immutableMySqlImage -PythonPath $trustedPythonExe `
  -ProtectedResourceIds $reviewedProductionResourceIds
# Review template.json, bundle.json, run.json, settings.json and the estimate.
# Execute contacts GitHub/GHCR, then requests an exact CREATE STAGING <nonce> approval.
./deploy/staging/Invoke-StagingRehearsal.ps1 -Action Execute -RunDirectory $run
```

Plan performs no AWS/GitHub calls. Execute is single-use: it persists creation
intent before submitting one CreateStack request. It never retries creation or
deployment after a lost response. Read-only bootstrap polling is bounded. On a
normal exit, including errors, `finally` deletes only the guarded staging stack
and checks cleanup. Keep the run directory for that purpose even after failure.

The automated sequence installs Docker/Compose and the reviewed root engine,
generates independent application configuration, initializes a fresh synthetic
schema, deploys the verified release and validates the complete production
receipt contract. It separately checks host current release/schema readiness,
HTTPS API health and frontend shell. It attempts forbidden shell, SCP/SFTP,
migration/injection arguments, forwarding, wrong identity and wrong host pin.
Then it injects a backup failure in a single staging engine invocation under the
real lock. Only the exact exception instance raised by the installed backup hook
at `writers-stopped` can produce a root-owned `racebib-stage-backup-v1` proof.
Unexpected stop-writers errors, including cleanup errors swallowed by the engine,
or any unrelated backup/migration/deployment error produce no proof and stop the
rehearsal. The proof binds the run, instance, full SHA, recovery tag, fresh controller
challenge and entire retained release-state digest. The controller validates it
before proceeding. A separate staging-only maintenance-rejection probe (below)
must also succeed. Recovery checks both forms of evidence and the unchanged state
under one continuous engine lock, then durably claims the authorization before
its single attempt; failure/disconnect cannot reuse it. Only that
explicitly pre-authorized synthetic recovery without migrations is automatic.
Deployment/receipt/readiness checks then repeat.
No production failure is injected. Production deployments never get automatic
recovery from this code.

Successful execution requires both owner-protected `acceptance.json` and
`cleanup.json`. SSH exit status alone and HTTP 200 alone are insufficient.

## Failure handling and cleanup

Do not rerun Execute after disconnect, PC shutdown, timeout or failed receipt.
The remote engine may still be running. There is no automatic retry, rollback,
migration or maintenance clearing after ambiguity. For this disposable rehearsal,
inspect only its AWS stack identity and destroy it; make a fresh Plan for a new
attempt. Automatic teardown intentionally discards staging diagnostics/data.
The teardown does not signal application rollback or certify deployment success.

Negative tests require both the expected exit status and the expected rejection
diagnostic: fixed server forced-command refusal, SSH administrative forwarding
denial, host-key mismatch, or public-key denial for the deliberately wrong identity.
Raw SSH diagnostics stay in memory and are not logged. Connection failures,
timeouts, unexpected authentication failures, unknown diagnostics and client
configuration errors fail the test as inconclusive. Nonzero alone never passes.

### Maintenance-rejection evidence without production protocol changes

The unchanged production `ssh_entry.py` announces `Root-only deployment log:
ssh-deployment-<8 characters>.log` on stdout before running the engine. Its exit-1
stderr is deliberately generic for all failures. Neither is maintenance evidence.
The staging controller now accepts exactly that single announcement and exact
generic diagnostic as a transport envelope only; extra/missing output, success,
authentication/connection/client errors and timeouts stop the rehearsal.

Before the single SSH attempt, a new root-only `prepare-barrier-probe` action
validates the existing synthetic proof and unchanged state, binds a fresh probe
nonce and snapshots existing SSH log names. After the attempt, `verify-barrier-probe`
reads only the announced fixed-format basename under `/opt/racebib/state`, under
the real deployment lock. It requires a new, fresh, root-owned mode-600 regular
file with one link, refuses symlinks and oversized logs, and verifies the exact
terminal maintenance exception plus ordered stack frames from the real
`Operations.deploy -> ReleaseState.require_idle -> require` path. Identical error
text from another function, generic errors and unrelated failures are rejected.
The attempt is marked consumed before reading the log; it cannot be retried into
successful evidence. No raw log contents are returned to Windows or printed.

Only successful verification creates the root-owned `racebib-stage-barrier-v2`
receipt, binding both nonces, run/instance, SHA/tag, retained-state digest, log name
and log digest. The controller uses the existing strict evidence JSON parser:
duplicate, extra, non-string or mismatched fields fail closed. The host also
requires this verified probe before permitting the existing single-use synthetic
failure recovery. A generic deployment error and unchanged maintenance state are
never sufficient. All new capabilities belong exclusively to the staging forced
control command; production entry points, engine, diagnostics and receipts remain
unchanged.

This verifier intentionally depends on the reviewed Python traceback structure,
fixed installed paths and current tempfile basename format. A Python/runtime or
diagnostic change, truncated log, clock adjustment or unsafe filesystem metadata
can make it fail closed. Investigate or destroy the disposable stack; do not relax
matching or auto-retry recovery. Root-owned engine/log integrity remains part of
the trust boundary; this is not protection against a compromised root account.

### Atomic staging recovery and immutable proof claims

Create a fresh staging plan from the reviewed checkout. Staging attestation v2
requires the atomic recovery handler; the controller rejects older v1 host plans
before recovery. This does not change the production SSH or receipt protocol.

Production `recover.sh` calls `operations.main()`, which acquires the non-blocking
`/opt/racebib/.deployment.lock` before constructing Operations or verifying a release.
`Operations.deploy()` itself does not acquire that lock. The earlier staging path
validated and deleted its proof before starting that separate locked process;
concurrent requests could therefore recover a changed barrier or delete a replacement
proof. Staging now acquires the exact same engine lock once and, without releasing
it, validates the run/instance/nonces/SHA/tag/probe, compares the entire current
state, claims authorization, re-verifies immutable provenance, and directly calls
the unchanged `Operations.deploy(migrate=False, recovery=True, reason=...)`.
Backup, writer stopping, schema/readiness checks, state transitions and commit
remain the existing engine implementation. No nested `recover.sh`/`operations.main()`
lock acquisition or second independent lock is used. Production sources are unchanged.

Proofs are retained, never unlinked or renamed. A descriptor opened with
`O_NOFOLLOW` verifies root ownership, mode 600, regular type, one link, stable
device/inode/metadata and content hash. Replacement or in-place changes are rejected
before dispatch. `recovery-claim-<failure nonce>.json` is created with exclusive
`O_CREAT|O_EXCL`, file-fsynced and directory-fsynced before recovery can run. That
immutable claim binds the exact proof/file identity and retained state. Its existence
means spent authorization even if empty, partially written, interrupted or failed.
An old process never deletes or overwrites a newer proof. Both proof identity and
state are checked again after claiming and immediately before engine dispatch,
while the same lock is still held.

The lock remains held through engine completion and root-only outcome recording.
Lock contention fails immediately, before claiming or calling the engine: there
is no server lock wait, timeout loop or automatic retry. A request delayed in SSH
or scheduling must validate the then-current state after it acquires the lock.
All authorized state writers must use this existing lock; arbitrary root edits or
storage corruption remain outside this protocol's guarantees.

Recovery uses a fixed local root environment, including `/root` Docker credentials;
inherited Docker targets, shell/Python variables, credentials and TLS/proxy overrides
are not forwarded. Output and tracebacks go only to the private
`state/staging-recovery-<failure nonce>.log`. The claim's `.result.json` sidecar records
success or failure type and whether dispatch began. SIGINT/SIGHUP/SIGTERM and command
timeouts fail without retry. On operational failure writers are stopped best-effort,
maintenance remains, and a late commit error that left an idle marker reinstates
the attested baseline under the lock. SIGKILL, power loss or failing storage may
prevent final diagnostics or restoration; the durable claim and original proof
retain investigation evidence. They do not authorize another automatic attempt.

After a failed/ambiguous recovery, retain proof, probe, claim, result, state and
private log for authorized investigation, then use the guarded teardown procedure.
Never delete a claim, proof or barrier to retry. Even an apparently empty claim
requires manual investigation; a fresh rehearsal uses a fresh stack/run/nonce.

After interruption, or to repeat the cleanup verification:

```powershell
./deploy/staging/Invoke-StagingRehearsal.ps1 -Action Destroy -RunDirectory $run
./deploy/staging/Invoke-StagingRehearsal.ps1 -Action VerifyCleanup -RunDirectory $run
```

Deletion authenticates the expected account, exact stack ARN/name, all nonce
tags and original template before requesting DeleteStack. A lost CreateStack
response can be reconciled by the exact nonce name; mismatches fail closed.
Deletion is idempotent for an already deleted known stack. No broad tag-based
delete or guessed resource IDs are used. Verification checks stack deletion,
stage-tagged nonterminated EC2/volumes/addresses/NAT/snapshots and tracked NICs
and volumes even if their tags were removed. This is scoped evidence, not an
account-wide billing audit: check EC2 volumes/public IPs and Cost Explorer later
for unexpected resources or delayed billing. Never delete an unknown resource
to make the report pass. On DELETE_FAILED, inspect the exact tagged stack event
and dependency; preserve run files and resolve that staging dependency only.

Before DeleteStack, the tool now checks the exact logical IDs and resource types
(a subset is permitted only for a quiescent failed/rolled-back partial creation).
It requires unique CloudFormation creation evidence, rejects import/update history,
persists observed physical IDs and attached storage/interfaces, and rejects later
replacement/disappearance. Each remaining root resource needs all four staging
tags plus exact CloudFormation stack/logical ownership tags. Live VPC, subnet,
firewall, route table, gateway, host, disk and interface identities/relationships
are verified. Untaggable route/association/attachment resources require creation
evidence and matching live parent relationships. Protected IDs and the production
IP are rejected before deletion. In-progress or unprovable states fail closed.

For an abnormal state (missing tags, import/replacement, unknown ID, missing creation
evidence, unavailable EC2 resource, or DELETE_FAILED), preserve the complete run
directory and stop. Do not edit run.json, synthesize an inventory, change tags,
disable checks or grant administrator permissions. Using the expected account and
region, independently inspect the exact stack ARN, original template, full stack
events and resource descriptions read-only; compare them with the saved inventory
and protected production inventory. Wait for in-progress operations to quiesce.
If ownership remains uncertain, escalate to the AWS account owner and leave the
resource untouched, accepting possible ongoing charges. Only after independent
review establishes every affected resource is staging may the owner separately
approve a manual cleanup of that exact stack/resource set. Never delete an entire
stack containing a protected or unproven resource. After manual cleanup, use
VerifyCleanup for scoped residual checks and review later billing. This is an
explicit exception procedure, not an automatic fallback around validation.

After verified deletion, revoke the staging tokens, clear process environment
variables, and delete this run's two private keys with `Remove-Item -LiteralPath`
using reviewed exact paths. Retain sanitized acceptance/cleanup records if needed.
Never reuse keys, CA, marker, bundle/state or directory for the next run. Key
rotation is a fresh Plan/stack; it never touches production authorized_keys.

Common blockers: a changed PC public IP (new plan, no SG widening), unsafe Windows
ACL/tool path (install in a trusted location), denied IAM/tagging dependency
(review the minimal policy), no console trust marker (cloud-init/bootstrap failed),
expired staging tokens, image pull quota, memory pressure or TTL expiration.
The t3.small/1 GiB swap choice favors cost over performance; increase capacity
only through a separately reviewed template/estimate change. Do not bypass
host pins, CA validation or production engine barriers to diagnose a failure.

## Local tests and acceptance checklist

No AWS account, production endpoint or tokens are required for local tests:

```powershell
python -B -m unittest discover -s deploy/tests
./deploy/tests/Test-LocalDeployment.ps1
./deploy/tests/Test-StagingRehearsal.ps1
```

The staging Python tests mock AWS. PowerShell tests mock all SSH, HTTPS and AWS
operations and validate real Windows ACLs plus synthetic private-CA chains.
Hosted Windows CI runs both controller suites; no production-connected runner
or secrets are involved. Linux hosted CI discovers the added Python tests.
The root-only descriptor/claim/concurrency tests are skipped on an unprivileged
host and run in full in a separate pinned, network-disabled root Linux container.
To run the complete Python suite from Windows using that same isolation:

```powershell
docker run --rm --network none `
  --mount "type=bind,source=$PWD,target=/repo,readonly" -w /repo `
  python:3.11-slim@sha256:07d4824036af339fa1b6ef769cc99d9c10a141769538b52b3b597c84f7a7b535 `
  python3 -B -m unittest discover -s deploy/tests -v
```

Twelve deterministic concurrency tests use real flock and the actual recovery
state machine with only release-network and application/Docker boundaries mocked.
Events establish ordering; deadlines only bound a failed test, never select an
interleaving. They cover two authorized requests, post-validation inode replacement,
replacement during claim creation, in-place proof mutation, state mutation at handoff,
a newer barrier while an old request is queued, post-claim failure, immediate lock
contention, command timeout, process interruption, late commit failure and inherited
environment isolation. Stale requests cannot dispatch, remove a newer proof or
reuse spent authorization. Windows mocks also verify no retry/deployment/barrier
clearing after host recovery failure or contention.

The real-entry integration rehearsal runs separately in a fresh, pinned Python
Linux container with no network, ports or Docker socket, and a read-only checkout:

```powershell
docker run --rm --network none -e RACEBIB_BARRIER_FIXTURE=1 `
  --mount "type=bind,source=$PWD,target=/repo,readonly" -w /repo `
  python:3.11-slim@sha256:07d4824036af339fa1b6ef769cc99d9c10a141769538b52b3b597c84f7a7b535 `
  python3 -B deploy/tests/staging_barrier_entry_rehearsal.py
```

It refuses existing `/opt/racebib` or `/etc/racebib-staging` paths and requires the
explicit container fixture marker. It copies the unchanged scripts inside that
disposable filesystem and invokes actual `ssh_entry.main()`, mocking only
`run_engine`. The maintenance case invokes the real Operations/ReleaseState guard
before any Docker or network operation; actual stdout/stderr, root log files,
receipt parsing and failure handling are exercised. Twelve cases cover correct
rejection, unrelated/generic errors, a forged terminal message, identical error
text from the wrong origin, engine exceptions, success, stale/symlink/unsafe logs,
changed state and log replay. Windows tests consume the resulting synthetic JSON
fixtures and reject malformed/mismatched receipts and transport failures. Hosted
Linux CI runs this integration test; fixtures must be regenerated from that actual
entry point after an intentional protocol change, not hand-edited to create success.

## What this rehearsal can certify

| Production safeguard | Coverage |
| --- | --- |
| Full SHA/recovery selector, provenance/build jobs/manifest/image digests | Same selector and local verifier; host verifier runs again on staging. |
| Windows protected files/executables and pinned known_hosts | Same configuration/ACL/executable and SSH-argument helpers, plus live staging SSH pin tests. |
| Deployment receipt matching independently verified release | Same strict receipt parser and comparisons. |
| Root engine lock, backup, schema, readiness and durable barriers | Same unchanged engine on a fresh isolated VM; synthetic failure proof is staging-only. |
| Production explicit DEPLOY authorization and configuration reread | Existing Test-LocalDeployment.ps1 safely exercises the actual production orchestration with all I/O mocked. Staging's CREATE STAGING approval is separate and does not certify production authorization. |
| Production CLI wrapper | New scoped mock test invokes the real wrapper and verifies SHA/tag/config forwarding; it makes no network calls. |
| Fixed production HTTPS endpoint | Unchanged and deliberately never called by staging. Staging uses its own IP/private CA and cannot certify public production TLS/DNS/ACME. |
| Production native SSH process path | Existing controller mock tests cover orchestration; staging uses its own captured-stdio process implementation. Neither mocks nor staging are a live test of the complete production controller. |

The staging tests now reproduce the reviewed unexpected pre-backup failure and
swallowed cleanup failure, missing/mismatched/single-use state-bound proof,
wrong/imported/replaced/unowned/protected inventories, partial creation, and
negative SSH transport/authentication/client errors. Passing these local tests
is not authorization to provision or deploy. No production source safeguards
were altered to support staging.

- [ ] Review code, immutable release, AMI, image digest, IAM and regional cost.
- [ ] Approve only the new nonce stack; independently confirm production unchanged.
- [ ] Provenance, build/test jobs, manifest and image digests verified.
- [ ] Signed-console host trust, exact pin and deployment-key restrictions verified.
- [ ] Successful receipt, matching host state/schema, API and frontend confirmed.
- [ ] Forbidden commands/keys/forwarding/pins rejected without successful state changes.
- [ ] Backup failure retains barrier and state; explicit staging recovery succeeds.
- [ ] Acceptance and cleanup records present; stack and billable resource checks clear.
- [ ] Tokens revoked, local keys removed, later billing checked.
- [ ] Resolve the missing 2.4.4 baseline and platform differences before a production decision.

Passing local mocks establishes code-level regression evidence only. A real,
approved staging rehearsal is still required. Ubuntu 24.04 staging and a private
CA do not prove the production OS, public DNS/certificate lifecycle, real account
IAM compatibility, capacity or internet path. Production readiness remains
unconfirmed until those acceptance items and the actual 2.4.4 scope are reviewed.
