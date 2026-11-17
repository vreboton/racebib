# Deployment safety remediation verification — November 17, 2026

Verification was performed before committing the remediation. No GitHub settings, tags, packages or releases were changed; no SSH/AWS deployment was performed. Existing application/development databases were not selected or reset. Only newly created UUID-labelled local test resources were removed after their rehearsals.

| Verification | Actual result |
| --- | --- |
| Backend Release build | Passed; zero warnings/errors |
| Default backend suite | 42 passed, seven skipped (six require the disposable MySQL connection; one requires a preconfigured external live environment) |
| Follow-up existing MySQL HTTP integration suite | All six executed and passed against the new disposable local MySQL instance; none skipped |
| Deployment unit/failure-injection suite on Linux | All 22 passed, including real flock contention |
| Windows deployment unit suite | 21 passed; Linux-specific flock test skipped, subsequently passed in Linux |
| Disposable MySQL rehearsal | Passed under MySQL 448 MiB RAM / 768 MiB combined RAM+swap and one CPU; API used the production 160 MiB managed-heap cap |
| npm ci | Succeeded; six moderate advisories reported |
| Frontend tests | Four files / eight tests passed |
| Frontend production build | Passed; existing large-chunk warning remains |
| Compose validation | Database-only, combined production overlays and unchanged development Compose passed using synthetic values |
| Dockerfile BuildKit checks | Both passed, no warnings |
| GitHub workflow actionlint | All three new workflows passed |
| Bash syntax | Operational shell scripts and installer passed |
| Python syntax | Parsed successfully |
| Git diff whitespace check | Passed |
| Compatibility diff | Existing build.yml, development Compose, frontend files and both existing Dockerfiles unchanged relative to HEAD |

The MySQL rehearsal explicitly verified:

- An empty schema fails readiness before migrations.
- Committed migrations produce a compatible schema, including EF alternate keys represented as MySQL unique indexes.
- Normal EF reads and the running API readiness endpoint pass against that schema.
- An unexpected column fails both command-line and running-API readiness; no weakened comparison was used to accommodate it.
- Missing migration history fails verification.
- Production bootstrap succeeds transactionally, is idempotent for the same approved active admin/current password, and refuses a password-reset attempt.
- A real mysqldump of synthetic data was compressed, imported into a **new** database and passed the same schema/application-read checks with its user record preserved.
- The clean import used the same named, non-root, read-only MySQL client pattern with 128 MiB RAM / 256 MiB combined memory+swap and shared database network namespace; no remote root grant was introduced.
- The existing six relational HTTP tests execute successfully on the disposable test target.

The deployment unit suite covers repeated-release history, failed pulls/backups/migrations/schema/running-application checks, persistent barriers across process reconstruction, explicit recovery, incompatible rollback, atomic-write failure, corrupt/missing state, database configuration drift, restore without application release/configuration, nonempty restore rejection, restricted-HTTP policy rejection, detached-migration protection, fixed digest references, complete provenance and asset checksum validation, and rejection of skipped/failed/missing/duplicate publishing jobs.

## Not established by local tests

Local Docker was Engine 27.3.1 / Compose 2.29.7, not target Engine 29.9.3 / Compose 5.7.1. The real API image's 256 MiB total native+managed memory cap, total t3.micro host pressure, swap persistence, MySQL upgrade/reboot behavior and 20 GB disk headroom still need a staging rehearsal. The local API rehearsal ran the compiled DLL on the workstation with the managed-heap cap, not inside the production API container.

No immutable GitHub release or public GHCR image was published. Provenance has automated mocked positive/negative tests and workflow linting; real GitHub permissions, release immutability, package visibility, workflow completion/approval behavior and anonymous server access remain to be verified after authorized commit/merge/publication. The standalone digest-pinned restore-rehearsal tool requires such a real immutable release and was not run against a real backup. The synthetic local restore above is not evidence of AWS disaster recovery.

Host Nginx HTTP/TLS templates, certificate renewal, SSH forced commands/sudoers/host keys, AWS security groups, EC2 reboot and recovery after actual host/power interruption remain unverified on the target. Failure-injection tests model durable state boundaries; they do not certify EBS/filesystem guarantees. Explicitly rehearse these using aws-ec2.md on a separate controlled staging host before admitting users.

Remaining application risks include the preserved .NET 8 end of support (November 10, 2026), six moderate npm advisories, proxy-grouped invitation throttling and lack of a password-change UI. Schema verification covers the documented structural contract and application reads, not semantic data invariants, defaults/triggers/stored code or arbitrary custom SQL correctness. Migration review and clean-target restoration drills remain mandatory.

Recommendation: ready for code review/commit as a controlled staging-readiness change; not approval for unrestricted production deployment.
