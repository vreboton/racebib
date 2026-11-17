# RaceBib AWS EC2 deployment and recovery

Milestone 2.4.2 replaces routine CI SSH deployment with [approved OIDC/SSM deployment](github-actions-ssm.md). Follow that runbook for current GitHub variables, IAM policies, approval prerequisites and first deployment. SSH configuration below is retained for legacy/manual access; the current workflow uses no SSH secrets and exposes no migration option.

RaceBib is under active development. This system prepares controlled staging/production-readiness deployment; it does not certify the race timing platform. No AWS resources, deployment, or GitHub settings are changed by adding these files.

As of November 17, 2026, the preserved .NET 8 stack has passed its November 10, 2026 end of support. Plan a separately reviewed framework/dependency upgrade before unrestricted real production use; this deployment remediation deliberately preserves the requested application framework. See [Microsoft's lifecycle notice](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/). The existing frontend audit also reports six moderate advisories. Neither issue is fixed by container isolation or TLS.

## Safety model

A source SHA selects an **immutable GitHub release** named `racebib-SHA`; it is not an image tag. Its `release.json` contains the repository, source SHA, exact API/web GHCR digest references, and build/deployment-safety/publication run IDs and attempt numbers. Consumers verify the release's immutable flag, asset SHA-256, source tag, main ancestry, exact workflows/attempts and actual successful jobs. A skipped job is rejected. Both CI and the root-owned server run the verifier independently. A public API outage/rate limit fails closed; do not bypass provenance during an incident.

GitHub release immutability is mandatory. The workflow creates a lightweight source tag, creates a draft with the manifest asset, then publishes and verifies immutability. Existing source releases/tags are rejected; assets are never clobbered. Attempt-qualified GHCR tags are discovery aids only. Deployment and rollback pull digest references, never tags. Keep released manifests and referenced images indefinitely for the rollback window; digest integrity cannot prevent deletion/unavailability.

`/opt/racebib/state/release-state.json` atomically holds current, previous **distinct** successful release, history, and an `inflight` maintenance barrier. Each state replacement flushes the file and parent directory. The exclusive host lock serializes all production operations. Before changing services/schema/data, an operation durably records the barrier. Failure, SIGTERM, SSH interruption or host loss cannot record success; a surviving marker blocks ordinary deployment and rollback. A named `racebib-maintenance` container blocks recovery while a detached maintenance process might still be executing. There is no automatic marker deletion or automatic schema downgrade.

Success requires exact EF migration history, relational structure compatibility and normal EF materialization of Identity, event, invitation and audit entities. Verification examines tables, column types/nullability, primary and alternate keys, indexes and foreign keys/delete rules. Unexpected tables/columns are rejected. It runs before starting writers and again through the **running API's** internal `/health/ready` endpoint, followed by web/routing checks. Neither a liveness response nor an unauthenticated 401 establishes readiness. Metadata checks are conservative, not a general proof of data invariants: custom SQL, stored code, defaults, triggers, data transformations and semantic compatibility still require migration review and rehearsal.

Only equal migration histories and equal supported structural contracts are accepted for image rollback. Even an apparently additive migration can therefore prevent rollback to an older image. Use a forward fix or a reviewed clean-target restoration; never edit history rows to trick production verification.

## Architecture and resource limits

Internet -> host Nginx -> web at 127.0.0.1:8080 -> private API -> private MySQL. API/MySQL have no host-published ports. MySQL's Docker network is internal. API has a separate application network for web and SMTP egress. API runs as `app` with no Linux capabilities. Swagger and development seeding remain Development-only. The readiness endpoint is internal; frontend Nginx does not forward its path to API.

Production uses **both** `compose.database.yml` and `compose.production.yml`. Database-only operations use the first file and need no JWT, base URL, application release or application image. The original `docker-compose.yml`, existing CI workflow and Dockerfiles remain unchanged; backend build exclusions omit generated outputs/secrets.

The target inventory is Ubuntu 26.04.1 x86_64, t3.micro (~1 GiB physical RAM), 2 GiB persistent swap, 20 GB encrypted gp3 root disk, Docker 29.9.3 and Compose 5.7.1 in ap-southeast-2. Verify these on the server; local validation with other Docker versions does not establish target-version compatibility.

Normal memory caps: MySQL 448 MiB, API 256 MiB, web 64 MiB (768 MiB total). Memory+swap caps are respectively 768/512/128 MiB; these are combined totals, not extra swap reservations. MySQL buffer pool is 128 MiB, connection limit 30, performance schema disabled, binlog retention seven days. API pool is 20 and managed heap cap 160 MiB (native allocations still count against 256 MiB).

Initialization runs MySQL alone. Migration, verification, administrator provisioning, and database upgrades stop API/web before running a single maintenance API container. A dump process shares MySQL's cap; host Python gzip streams 64 KiB chunks. Scheduled online backups can still compete with the normal workload; schedule off-peak and monitor swap/OOM. Do not run restore rehearsals or image builds on the 1 GiB production host. Swap improves survival, not capacity. Load tests and peak resident memory on the actual target remain required.

Clean-target import uses a named 128 MiB MySQL client (256 MiB combined memory+swap), while API/web stay stopped. It shares only the database container's network namespace for root loopback access; no remote root grant or public port is added. The same maintenance-container guard prevents recovery from racing a surviving import after SSH/process interruption.

## GitHub preparation

1. Review and merge only with authorization. Protect main with PR review, restricted bypass/no force pushes, and required `backend`, `backend-mysql-integration`, `frontend` and `deployment-safety` checks. Protect workflow/deployment code through CODEOWNERS/review. The existing `build` workflow name and job names are release-provenance contracts; update the verifier deliberately when changing them.
2. Repository Settings -> General -> Releases -> **Enable release immutability**, before the first publication. Protect `racebib-*` release tags against manual replacement/deletion. The workflow does not need an administrator PAT; the published immutable flag is checked with ordinary repository access. If immutability is disabled, the final publishing step and consumers fail closed.
3. Publishing uses commit-pinned official actions and `GITHUB_TOKEN`: preflight has contents/actions read; image jobs have contents read and packages write; finalization alone has contents write/actions read for tags and immutable manifest release creation. It verifies all three required build jobs executed successfully on the same main push and attempt, then waits up to 15 minutes for the source-matched main-push deployment-safety workflow (including the MySQL rehearsal). The two explicit publishing jobs must both succeed. Feature/PR runs never publish deployable releases. Maintain the reviewed action pins through normal dependency review.
4. After initial image publication, make **each** package `racebib-api` and `racebib-web` public in Package settings. A public repository does not imply public packages. Link packages to vreboton/racebib and permit its Actions access. Verify anonymous pulls. No GHCR credential is needed on EC2 for public packages.
5. Create the GitHub environment `production`, restrict it to main, require a reviewer and prevent self-review where supported. This external setting cannot be enforced by YAML alone. Keep workflow dispatch as the only deployment trigger. No automatic deployment is enabled.
6. Configure the environment variables and restricted OIDC/SSM role in [the current CI/CD runbook](github-actions-ssm.md). The workflow no longer reads `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` or `DEPLOY_KNOWN_HOSTS`. Retire legacy CI access only after approved SSM validation; preserve protected administrator recovery access.
7. Archive CI/publication records for the rollback period. The verifier rejects unavailable or non-successful attempts and does not accept workflow summaries or display titles alone as publication evidence.

Interrupted publication leaves its source tag/draft or non-immutable release untouched and blocks ordinary publication reruns. Do not delete, overwrite or republish these artifacts. Enabling immutability applies to future releases; it does not repair old releases. Use the reviewed, manually dispatched `recover-publication.yml` procedure in [Publication recovery](publication-recovery.md). It creates only a fresh `racebib-SHA-recovery-RUN-ATTEMPT` tag/release, copies the original `release.json` bytes unchanged and adds a separate recovery receipt. It never rebuilds, pushes or retags an image. Both original image jobs, source checks, logged digest outputs and registry objects must verify. A failed/skipped publisher or missing source manifest is not recoverable through this path.

`vreboton/racebib` is currently **public**. Earlier notes describing the repository as private are superseded. GHCR package visibility is independent; previous anonymous pull failures do not establish repository visibility or prove image deletion. Verify both packages separately; public packages may be pulled anonymously, while private packages need approved expiring package-read credentials and a root Docker credential helper/login. Public GitHub API reads may still require authenticated read access for rate limits or retained workflow/job evidence. Where credentials are needed, install `/opt/racebib/github-auth.json` outside the checkout, root-owned mode `0600`, with `github_token`, `ghcr_username`, and `ghcr_token` fields as appropriate; no write/admin scopes are required by the verifier. Manage credentials through a protected administrator session, never commands containing tokens or CI output. The verifier rejects a symlink or incorrect owner/mode. Environment equivalents are `GH_TOKEN`, `GHCR_USERNAME`, `GHCR_TOKEN`. HTTPS requests drop credentials on asset redirects.

Registry verification credentials do not configure Docker's pull credential store. For private images, separately authenticate the root Docker client using an approved credential helper or `docker login ghcr.io --username ACCOUNT --password-stdin` over a protected session; never put a token in an argument or commit Docker auth files. Scope/rotate the credentials and validate package Actions access for the recovery and deployment workflows. Retain the original publication job logs for the full recovery/rollback window: recovery verification fails closed if logs, assets, attempt tags or images become unavailable.

## AWS and host setup

Verify `cat /etc/os-release`, `uname -m`, `docker version`, `docker compose version`, `free -h`, `swapon --show`, `df -h`. Enable Docker/Nginx at boot, persist swap in fstab, apply updates, require IMDSv2, and use minimal instance IAM permissions. Do not reinstall a functioning Docker engine blindly.

Security group: SSH 22 from administrator/VPN or a controlled runner network only. Initial HTTP 80 from individual trusted tester CIDRs only. No 3306, 5080 or 8080 rules. IPv6 needs equivalent restrictions. Open 443 for intended users only after TLS validation. Docker published ports require correct bind addresses/security groups; UFW alone is insufficient. Hosted runner IPs vary: use a controlled production-connected runner/network or private tunnel, rather than opening SSH worldwide. Never execute untrusted PR jobs on a production-connected runner. Permit required DNS, HTTPS pulls/updates and SMTP egress.

Install host Nginx, OpenSSH, curl, Python >=3.11, gzip, Docker/Compose and standard Linux utilities from official sources. The server does not require source builds, the .NET SDK or npm. Copy a reviewed artifact containing the two production Compose files and deploy/ directory to a temporary administrator-owned directory, then run:

```bash
sudo bash deploy/install-server.sh
```

The installer creates root-owned scripts/configuration, mode-700 state/backups directories, mode-755 shell/privileged entry points and mode-644 Python/configuration files. It preserves existing .env, backups, state, databases and host Nginx policy. It starts no containers. Keep /opt/racebib and all scripts/parent directories not writable by the deployment account.

Installed layout:

```text
/opt/racebib/compose.database.yml
/opt/racebib/compose.production.yml
/opt/racebib/scripts/{operations.py,release.py,release_state.py,restore_rehearsal.py,*.sh}
/opt/racebib/nginx/{racebib.conf,racebib-access.conf,racebib-https.conf.example}
/opt/racebib/state/{release-state.json,database.json}
/opt/racebib/backups/
/opt/racebib/.env
/usr/local/sbin/racebib-ssh
/usr/local/sbin/racebib-deploy
```

## Configuration and HTTP boundary

Copy deploy/production.env.example to /opt/racebib/.env with a privileged editor. Set root ownership and mode 600. Generate **independent** DB, root DB and JWT secrets privately with `openssl rand -hex 32`; never generate real production secrets in a checkout or print them in CI/chat. DB names/users use alphanumeric/underscore, <=32 characters. Store credentials in the approved password manager. Do not source .env, enable shell xtrace, or print rendered Compose configuration. `config --quiet` is safe; internal JSON rendering is captured and never logged. Quote provider SMTP values correctly if they contain Compose interpolation characters.

Set MYSQL_IMAGE to a tested `mysql:8.4@sha256:...` manifest digest, obtained with `docker buildx imagetools inspect mysql:8.4` on a workstation. Record it independently. Database initialization records a fingerprint of the entire rendered database configuration; ordinary application deployment rejects any drift and never pulls/recreates MySQL. Changing .env does not change credentials in an initialized database. Credential rotation must update SQL credentials and container configuration through a separately reviewed maintenance plan.

**Public-IP HTTP is disposable restricted testing only.** Use synthetic data and unique disposable passwords. Allowlisting is not encryption. Invitations are unavailable because the existing production application requires an HTTPS base URL. SMTP alone cannot fix that. Do not relax this application requirement.

For initial restricted HTTP:

```bash
sudo install -o root -g root -m 0644 /opt/racebib/nginx/racebib.conf /etc/nginx/sites-available/racebib
sudo install -o root -g root -m 0644 /opt/racebib/nginx/racebib-access.conf /etc/nginx/racebib-access.conf
```

Edit the access file to add individual trusted tester IPs **before** its final `deny all;`. The supplied policy allows loopback only. No broad ranges or `allow all`; the operation validator accepts at most /24 IPv4 or /120 IPv6 ranges. Enable the racebib site symlink and disable the distribution default site only after checking for other applications. Set DEPLOYMENT_ACCESS_MODE=restricted-http and the actual http://PUBLIC_IP origin. Run `sudo nginx -t` and enable/start Nginx. Deployment checks the active site/policy and reloads the verified policy before starting the app. Verify from an untrusted address that access is denied.

Normal access requires DEPLOYMENT_ACCESS_MODE=https and https://YOUR_DOMAIN. Follow [the Certbot HTTPS transition](https-certbot.md). Render racebib-https.conf.example by replacing __DOMAIN__ everywhere with the certificate domain and install it as the active racebib site. Use `certonly --webroot` or separately reviewed DNS validation; do not let an ACME client rewrite the checked template. Port 80 serves only static `/.well-known/acme-challenge/` files from `/var/www/letsencrypt`, returning 404 for missing challenges, and redirects every other request to canonical HTTPS. Authentication is proxied only on TLS 443. Deployment still verifies the exact reviewed template, trusted certificate/hostname and post-start HTTPS routing. Do not enable ASP.NET HTTPS redirection: host Nginx terminates TLS. API does not trust forwarded headers; IP-based invitation throttling currently groups proxy traffic. Configure narrowly trusted forwarding only through a separate reviewed application change.

SMTP/invitation delivery must be tested after HTTPS exists. Use authenticated TLS SMTP, a verified sender and the actual HTTPS base URL. Keep SMTP credentials only in .env. Host/Docker administrators can inspect container environments; this system does not protect secrets from root.

## First deployment: explicit database initialization and verification

Record a successful immutable release's full source SHA. Install reviewed server scripts **before** invoking workflows that depend on them. Initial bootstrap is manual root maintenance, not enabled through the CI SSH identity:

```bash
sudo bash /opt/racebib/scripts/database-init.sh
sudo bash /opt/racebib/scripts/recover.sh FULL_SOURCE_SHA --resume-migrations --reason 'Initialize reviewed first release schema on the new empty database'
```

Initialization creates the persistent volume and database baseline, then deliberately leaves a barrier. Recovery backs up the initialized database, applies explicit migrations, verifies the exact schema, starts API then web without recreating MySQL, checks the running API's database reads and commits success atomically. Without --resume-migrations, recovery is verification-only and rejects a missing schema.

If a matching production data volume already exists, initialization refuses unless --adopt-existing is provided. Adoption verifies the existing container's exact pinned image, environment, command, volume and resource limits; it does not silently replace it. An older prototype configuration that differs must first be inventoried/backed up and deliberately brought through a reviewed adoption/maintenance plan. Never reset it or point this configuration at a development volume.

### Resume initialization after the resource-limit type error

On Compose 5.7.1, rendered memory limits can be decimal byte strings while Docker inspect reports integers. Install the reviewed resource-limit fix in `/opt/racebib/scripts/operations.py` (root-owned, mode 0644, invoked through Python) before retrying. This changes host validation only; keep the approved Compose files, `.env`, MySQL image, running container and named volume unchanged. Preserve a protected copy of the existing state and take an EBS snapshot through the normal backup procedure before maintenance. Never edit/delete state, use `down -v`, remove the volume or run `up --force-recreate` to work around this error.

For the failed first initialization of source `899f0d6a22cdafad3a691c52d3956ffbeb2bbcee`, first inspect state without printing secrets:

```bash
sudo python3 - <<'PY'
import json
from pathlib import Path
root = Path('/opt/racebib')
state = json.loads((root / 'state/release-state.json').read_text())
pending = state.get('inflight') or {}
print('database baseline exists:', (root / 'state/database.json').exists())
print('operation:', pending.get('kind'), 'stage:', pending.get('stage'))
print('successful release exists:', state.get('current') is not None)
PY
# With the corrected validator installed, backup does not require database.json:
sudo bash /opt/racebib/scripts/backup.sh
# Only for a missing database.json and an interrupted first initialization:
sudo bash /opt/racebib/scripts/database-init.sh --adopt-existing \
  --resume-initialization --reason 'Reviewed matching healthy MySQL after resource validation type failure'
```

The resume operation requires the existing `database-initialization` barrier at `prepared`, no successful release/history, no application target and no database baseline. It records the reason and retains the prior barrier. It validates the existing container before issuing only `compose start --wait` (never pull/up/recreate), verifies health, then atomically records `database.json`. Any validation failure leaves the barrier and does not adopt a mismatched container. Retry after investigation is explicit; there is no force-clear option. If the baseline already exists, initialization is intentionally refused: review it and use ordinary schema recovery below. An unrelated barrier, missing container, changed credentials/configuration or existing application history requires separate investigation.

Successful adoption still leaves maintenance active. After reviewing the backup and the immutable source release, the separate first-schema operation is:

```bash
sudo bash /opt/racebib/scripts/recover.sh 899f0d6a22cdafad3a691c52d3956ffbeb2bbcee \
  --resume-migrations --reason 'Resume reviewed first schema after adopting the existing MySQL initialization'
```

This last command applies migrations and starts application writers; execute it only during the separately authorized deployment window with the runbook's access/TLS restrictions satisfied. It verifies the schema and database-backed application readiness before clearing maintenance. These commands were not executed against AWS during development of this fix.

Create root-owned mode-600 /opt/racebib/admin.env with plain assignments:

```text
SeedAdmin__Email=approved-administrator@example.com
SeedAdmin__Password=UNIQUE_PASSWORD_FROM_THE_PASSWORD_MANAGER
```

Use >=12 characters with upper/lowercase, digit and symbol. Run `sudo bash /opt/racebib/scripts/provision-admin.sh`, then remove admin.env. Provisioning stops writers, backs up, verifies schema, uses one maintenance container and a transaction, then verifies/restarts the app. It refuses to promote/reactivate an existing non-admin or reset an existing password. Repeating bootstrap for the same active admin requires its current password. Development seeding retains its previous behavior. A provisioning interruption leaves a recovery barrier; investigate account/role state before verification-only recovery. There is no assumed password-change UI.

## Manual releases, GitHub deployment and rollback

Normal release:

```bash
sudo bash /opt/racebib/scripts/deploy.sh FULL_SOURCE_SHA
# Only for reviewed schema changes:
sudo bash /opt/racebib/scripts/deploy.sh FULL_SOURCE_SHA --migrate
sudo bash /opt/racebib/scripts/rollback.sh
```

The default rollback is the previous distinct successful manifest, not a mutable tag. Repeating the current release preserves that previous release. Rollback never applies migrations and rejects incompatible histories/structures; an unsuccessful attempt keeps the last successful history and leaves a recovery barrier. Recovery to the current compatible release may be needed to resume service. No silent automatic rollback is attempted.

GitHub: Actions -> deploy-production-manual -> Run workflow on main -> verified source_sha and optional matching immutable recovery release_tag -> independent production environment approval. The workflow uses restricted OIDC/SSM and has no migration option. Leave external HTTPS verification off on hosted runners because production 443 is restricted to a trusted IP; verify external availability separately from that IP. The summary separates remote command, host-state receipt and external availability outcomes. No external failure triggers retries, state changes or rollback. See [the current setup and recovery procedure](github-actions-ssm.md).

For a recovered publication, append `--release-tag EXACT_RECOVERY_TAG` to `deploy.sh`, `recover.sh`, the application target of `database-maintenance.sh`, or `restore_rehearsal.py`. Install the reviewed verifier, operations script and forced-command entry point together before using this option. Successful state stores the explicit tag; `rollback.sh` re-verifies that tag automatically. The unchanged original manifest digest prevents recovery from silently remapping a source SHA or resetting release history. This option does not bypass schema checks, maintenance barriers, backups, HTTPS restrictions or production approvals.

For direct read-only Compose inspection, use a root shell and extract the **pinned** image refs from state (not from an old .current-image marker):

```bash
sudo -i
cd /opt/racebib
export API_IMAGE=$(python3 -c 'import json;print(json.load(open("state/release-state.json"))["current"]["manifest"]["api"])')
export WEB_IMAGE=$(python3 -c 'import json;print(json.load(open("state/release-state.json"))["current"]["manifest"]["web"])')
docker compose --env-file .env -f compose.database.yml -f compose.production.yml ps
docker compose --env-file .env -f compose.database.yml -f compose.production.yml logs --tail 100 api web mysql
docker stats --no-stream
free -h
df -h / /opt/racebib
```

These exports are for privileged inspection only. Do not manually run up/restart to bypass a barrier. A normal restart uses deploy.sh for the current source SHA, including schema verification and backup. Database-start.sh may start the existing approved MySQL container without recreating it; it does not clear a barrier.

## Interruption and migration recovery

1. Keep writes stopped. Preserve state/release-state.json, database.json, backups and database files. Inspect the inflight stage/target/backup and container status. MySQL DDL can partially commit; neither process exit nor image rollback undoes it.
2. Inspect `docker ps -a --filter name=^/racebib-maintenance$`. If maintenance is still running, wait/investigate; the tool refuses recovery while that container exists. Do not kill an active migration casually. After a verified exited/orphan container has been investigated, an administrator may remove **only** that stopped maintenance container with `docker rm racebib-maintenance`.
3. Establish whether schema history AND actual DDL match the selected target. Use reviewed forward repair, or restore into a separate clean target. Never forge migration history or delete the barrier.
4. If MySQL is stopped, restore the approved database configuration and run `database-start.sh`. Configuration mismatch requires the separate maintenance/recovery plan; no application operation silently rebaselines it.
5. After investigation, run one explicit recovery path:

```bash
# Schema already repaired or clean-restored; read-only verification before restart:
sudo bash /opt/racebib/scripts/recover.sh MATCHING_SOURCE_SHA --reason 'Reviewed restored schema and selected the matching application release'
# Only after reviewing partial DDL and making retry safe:
sudo bash /opt/racebib/scripts/recover.sh TARGET_SOURCE_SHA --resume-migrations --reason 'Reviewed partial DDL and completed the documented forward repair'
```

Recovery verifies immutable provenance, takes a new backup, performs requested migrations explicitly, verifies schema and running application, then clears the barrier **in the same atomic success update**. A retry failure retains the barrier and prior recovery context. For corrupt/missing state beside an initialized DB, preserve evidence and recover the root-controlled state from a trusted copy/reconcile through a reviewed incident plan; scripts intentionally provide no reset/force-clear switch.

## Database maintenance and upgrades

Ordinary deployment does not pull, up or recreate MySQL. For a separately reviewed change within supported MySQL 8.4, inventory the currently approved image and take an off-instance backup first. Prepare the tested new digest/config in .env/compose.database.yml, keep a copy of the prior configuration, then:

```bash
sudo bash /opt/racebib/scripts/database-maintenance.sh CURRENT_SOURCE_SHA --reason 'Reviewed MySQL patch upgrade with verified off-instance backup'
sudo bash /opt/racebib/scripts/recover.sh CURRENT_SOURCE_SHA --reason 'Verified upgraded MySQL and reviewed application schema compatibility'
```

Maintenance stops writers, dumps the old running database **before** pulling/recreating MySQL, waits for health, verifies the new runtime and atomically records the new database baseline. It leaves the recovery barrier and writers stopped until application verification. Failure retains the old baseline/barrier; do not downgrade database binaries blindly. Recover on a clean host from the verified backup if required. Major upgrades outside 8.4 need a separate reviewed change. Compose-version/config rendering changes can alter the baseline fingerprint too; handle them explicitly rather than weakening comparison.

## Backups, clean restore and disaster recovery

`sudo bash /opt/racebib/scripts/backup.sh` uses only database configuration; no successful application release is needed. It streams mysqldump --single-transaction with routines/events/triggers and no tablespace requirements, checks exit status, validates the complete gzip stream and writes a SHA-256 sidecar `.sql.gz.json`. No completed checksum pair is published on dump failure. Credentials stay inside the running container environment, not CLI arguments or backup names. Backups contain sensitive Identity/application data and require encryption/access controls.

A root cron example is `15 2 * * * /bin/bash /opt/racebib/scripts/backup.sh >> /var/log/racebib-backup.log 2>&1`. Protect/rotate the log and alert on failure or lock contention. After a successful backup, completed pairs older than seven days are pruned, except backups referenced by inflight recovery context and the latest two successful operations. Watch storage growth: retained incident backups can exceed that window. Online snapshots assume no concurrent manual DDL. Only scripts sharing this host lock are serialized.

Local backups alone are insufficient disaster recovery. Upload encrypted copies and sidecars to a private encrypted S3 bucket, block public access, use lifecycle rules and least-privilege upload permissions, alert on failed uploads and test retrieval. Preserve copies the instance cannot delete where feasible. Encrypted EBS snapshots complement logical backups. No S3/IAM resources are created by this milestone.

**Restore only into an empty database on a separately prepared host/volume.** These scripts do not drop/reset an existing database or import over existing tables. Provision a replacement host using the same reviewed MySQL digest/configuration, run database-init.sh, copy the approved backup **and** JSON checksum sidecar to root-only /opt/racebib/backups, chmod both 600, then:

```bash
sudo bash /opt/racebib/scripts/restore.sh /opt/racebib/backups/mysql-TIMESTAMP-ID.sql.gz --clean-target --reason 'Restore verified off-instance backup into the new empty recovery target'
sudo bash /opt/racebib/scripts/recover.sh MATCHING_SOURCE_SHA --reason 'Reviewed restored records and selected the matching immutable release'
```

Restore requires no application release marker/JWT/base URL, verifies checksum, rejects any existing target tables, imports and leaves the recovery barrier. Configure application secrets and Nginx before recover.sh. Verify migration history, data counts, relationships, login and normal workflows before switching DNS/IP or admitting writes. Restore loses writes newer than the backup; plan RPO/RTO. Retain the original host/volume unchanged for investigation.

## Safe isolated rehearsal

On a **separate disposable Ubuntu VM**, install reviewed files and copy an approved backup plus sidecar (treat even test copies as sensitive). Do not configure real production SSH keys. The automated restore rehearsal refuses hosts containing the production Compose project's containers, creates a UUID-scoped fresh volume/network, uses synthetic credentials and no published MySQL port, imports the backup, then runs the matching digest-pinned API's schema/application-read verifier:

```bash
sudo bash /opt/racebib/scripts/rehearse-restore.sh /root/rehearsal/mysql-BACKUP.sql.gz MATCHING_SOURCE_SHA --mysql-image mysql:8.4@sha256:TESTED_DIGEST --confirm-disposable-host
```

It stops and leaves only its new rehearsal database resources for inspection; remove only the reported project's stopped container and exact named volume after review. It does not claim production recovery or delete any existing database.

In a source checkout on a workstation/CI Linux Docker host, run:

```bash
python3 -B -m unittest discover -s deploy/tests -v
dotnet build backend/RaceTiming.sln --configuration Release
python3 -B deploy/tests/mysql_rehearsal.py --api-dll backend/RaceTiming.Api/bin/Release/net8.0/RaceTiming.Api.dll
```

The second rehearsal creates only UUID-labelled local MySQL test resources, uses the production MySQL memory/command settings and loopback ephemeral port, rejects an empty schema, applies migrations, tests bootstrap repeat/refusal semantics, checks actual HTTP readiness, injects structural/history drift, and restores synthetic data into a new database. It removes only its own new labelled resources. It never selects production/development databases.

On the disposable deployment VM additionally rehearse initial recovery, ordinary/repeated releases, compatible/incompatible rollback, interrupted and deliberately failing migrations, failed image pulls, failed readiness, a second concurrent operation, recovery without any successful release marker, a clean restore, SSH rejection of arbitrary commands, environment approval rejection and reboot recovery. Confirm state barriers/last-successful history survive each failure. Do not use a production migration as a fault-injection experiment.

## SSH least privilege and operations

Use a dedicated Ed25519 CI key, never the administrator PEM. Create racebib-deploy without password login or Docker group membership. Root-control authorized_keys and all entry points. The installer gives /usr/local/sbin/racebib-ssh and racebib-deploy mode 755/root ownership. Authorized key:

```text
restrict,command="/usr/local/sbin/racebib-ssh" ssh-ed25519 PUBLIC_KEY racebib-ci
```

Use visudo -f /etc/sudoers.d/racebib-deploy:

```text
racebib-deploy ALL=(root) NOPASSWD: /usr/local/sbin/racebib-deploy
```

The exact root-owned command may receive arguments; both forced-command regex and argparse validate them. No recovery/restore/maintenance/provisioning operation is granted to this identity. Prevent alternate unrestricted keys or account access; test SSH rejection of shell, forwarding and uploaded commands. Rotate independently of administrator access.

Enable Docker/Nginx on boot and persistent swap. Restart policies recover previously running services, but they do not replace post-reboot verification or restart unhealthy containers automatically. Stopped writers from a failed operation remain stopped. Run database-start.sh if needed, inspect the barrier, and use recovery or verified deployment rather than bypassing state.

Troubleshooting: inspect root-only container status/logs, state stage, OOM events, disk and swap. Missing GHCR digest/package visibility, unavailable GitHub provenance, incompatible schema, nonempty restore target and database baseline drift are deliberate failures, not reasons to disable verification. Failed maintenance may leave a stopped application; investigate before resuming. Avoid broad system prune, volume pruning or down -v. Remove only individually selected unused images after preserving exact rollback digests.

Monitor RAM, swap, CPU credits, EBS space/I/O, backup age, certificate expiry, health and failed jobs. Bound host logs as well as container logs. Set AWS Budgets alerts around the USD15/month target and Cost Anomaly Detection; inspect EC2, EBS, IPv4, transfer, snapshots, CPU surplus credits and monitoring charges. Use current Sydney pricing; USD15 is not guaranteed. Avoid NAT gateways/ALBs for this budget unless deliberately costed. Guest memory/disk metrics need explicit collection; default EC2 metrics are insufficient.

## Verification boundary

See verification.md for actual local results. Local rehearsal is not AWS deployment, target-version certification, real-backup restoration, GitHub environment setup or successful SSH rollout. Complete the target staging rehearsal and independent review before allowing real production use.

References: [immutable releases](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes), [release asset digests](https://docs.github.com/en/rest/releases/assets), [EF production migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying), [Compose service controls](https://docs.docker.com/reference/compose-file/services/).
