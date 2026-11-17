"""Root-only EC2 operations. Fixed paths, no shell interpolation, durable fail-closed state.

Unit tests inject a temporary root/backend; the command line cannot override the production root.
"""
import argparse
import contextlib
import copy
import gzip
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import ssl
import stat
import subprocess
import sys
import tempfile
import time
import urllib.request
import urllib.parse
import uuid

from release import SafetyError, require, canonical, sha256, verify_release, validate_manifest
from release_state import ReleaseState, atomic_json, now

PROJECT = "racebib-production"
ONEOFF = "racebib-maintenance"


@contextlib.contextmanager
def operation_lock(path):
    import fcntl
    with Path(path).open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise SafetyError("Another deployment/backup/restore is active") from None
        yield


def validate_http_policy(text):
    rules = [line.split("#", 1)[0].strip() for line in text.splitlines()]
    rules = [line for line in rules if line]
    require(rules and rules[-1] == "deny all;", "Restricted HTTP must end with deny all")
    for rule in rules[:-1]:
        require(rule.startswith("allow ") and rule.endswith(";"), "Only explicit tester CIDR allow rules are permitted")
        network = ipaddress.ip_network(rule[6:-1], strict=False)
        require(network.prefixlen >= (24 if network.version == 4 else 120), "HTTP tester allow ranges are too broad")


def run(command, *, env=None, stdin=None, timeout=420):
    try:
        result = subprocess.run(command, input=stdin, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                env=env, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise SafetyError("Operation could not finish; retain the maintenance barrier and inspect privileged container logs") from None
    require(result.returncode == 0, "Operation failed (exit %s); inspect privileged container logs; recovery may be required" % result.returncode)
    return result.stdout


def private_file(path):
    require(path.is_file() and not path.is_symlink(), "Missing regular private configuration file")
    info = path.stat()
    require(stat.S_IMODE(info.st_mode) == 0o600 and info.st_uid == 0, "Configuration must be root-owned, mode 600")


def byte_count(value):
    # Compose JSON may encode bytes as decimal strings; inspect uses integers.
    require(type(value) is int or isinstance(value, str) and re.fullmatch(r"[0-9]{1,19}", value),
            "Invalid MySQL resource limit: expected a positive integer byte count")
    result = int(value)
    require(0 < result <= 2**63 - 1, "Invalid MySQL resource limit: expected a positive integer byte count")
    return result


class Operations:
    def __init__(self, root=Path("/opt/racebib"), runner=run):
        self.root = Path(root)
        self.run = runner
        require(not (self.root / "state/database.json").exists() or (self.root / "state/release-state.json").is_file(),
                "Deployment state is missing beside an initialized database; explicit forensic recovery is required")
        self.state = ReleaseState(self.root / "state/release-state.json")
        self.environment = os.environ.copy()
        # These must come from a verified manifest, never an inherited shell or .env value.
        self.environment.pop("API_IMAGE", None)
        self.environment.pop("WEB_IMAGE", None)
        self.target = None

    def dc(self, *arguments, database=False, stdin=None):
        command = ["docker", "compose", "--project-name", PROJECT, "--env-file", str(self.root / ".env"),
                   "-f", str(self.root / "compose.database.yml")]
        if not database:
            command += ["-f", str(self.root / "compose.production.yml")]
        return self.run(command + list(arguments), env=self.environment, stdin=stdin)

    def db_configuration(self):
        private_file(self.root / ".env")
        config = json.loads(self.dc("config", "--format", "json", database=True))
        mysql = config["services"]["mysql"]
        env = mysql["environment"]
        require(re.fullmatch(r"mysql:8\.4@sha256:[0-9a-f]{64}", mysql["image"]) is not None, "Pin the MySQL 8.4 image digest")
        require(all(re.fullmatch(r"[A-Za-z0-9_]{1,32}", env[k]) for k in ("MYSQL_DATABASE", "MYSQL_USER")), "Invalid database/user name")
        require(all(re.fullmatch(r"[0-9a-fA-F]{64,}", env[k]) for k in ("MYSQL_PASSWORD", "MYSQL_ROOT_PASSWORD")), "Database secrets must be independent >=32 random bytes as hex")
        require(env["MYSQL_PASSWORD"] != env["MYSQL_ROOT_PASSWORD"], "Use independent database secrets")
        return config

    def db_baseline(self, config):
        path = self.root / "state/database.json"
        require(path.is_file(), "Initialize/adopt the database explicitly with database-init.sh before deploying")
        baseline = json.loads(path.read_text())
        require(baseline["configuration_digest"] == sha256(canonical(config)),
                "Database configuration changed. Ordinary deployment cannot apply this; use reviewed database maintenance")
        return baseline

    def save_db_baseline(self, config):
        atomic_json(self.root / "state/database.json", {"configuration_digest": sha256(canonical(config)),
                    "mysql_image": config["services"]["mysql"]["image"], "at": now()})

    def db_runtime(self, config, *, healthy=True):
        ids = self.dc("ps", "--all", "--quiet", "mysql", database=True).decode().split()
        require(len(ids) == 1, "Exactly one initialized MySQL container is required")
        value = json.loads(self.run(["docker", "container", "inspect", ids[0]]))[0]
        mysql = config["services"]["mysql"]
        expected_env = mysql["environment"]
        actual_env = dict(item.split("=", 1) for item in value["Config"]["Env"])
        require(value["Config"]["Image"] == mysql["image"] and
                all(actual_env.get(k) == v for k, v in expected_env.items()) and value["Config"]["Cmd"] == mysql["command"],
                "Running MySQL configuration/image differs from the approved baseline")
        require(not value["HostConfig"].get("PortBindings"), "MySQL must not publish host ports")
        network = config["networks"]["database"]["name"]
        require(set(value["NetworkSettings"]["Networks"]) == {network} and
                json.loads(self.run(["docker", "network", "inspect", network]))[0].get("Internal") is True,
                "MySQL must use only the approved internal network")
        require(value["HostConfig"]["RestartPolicy"]["Name"] == "unless-stopped", "MySQL restart policy differs")
        limits = [(byte_count(value["HostConfig"].get(actual)), byte_count(mysql.get(expected)))
                  for actual, expected in (("Memory", "mem_limit"), ("MemorySwap", "memswap_limit"))]
        require(all(actual == expected for actual, expected in limits), "MySQL runtime resource limits differ")
        volume = config["volumes"]["mysql_data"]["name"]
        require(any(m.get("Name") == volume and m["Destination"] == "/var/lib/mysql" for m in value["Mounts"]),
                "Unexpected MySQL data volume")
        if healthy:
            require(value["State"]["Running"] and value["State"].get("Health", {}).get("Status") == "healthy", "MySQL is not healthy")

    def no_oneoff(self):
        active = self.run(["docker", "container", "ls", "--all", "--filter", "name=^/" + ONEOFF + "$", "--quiet"])
        require(not active.strip(), "A maintenance container still exists; investigate/wait for it before recovery")

    def bind(self, verified):
        manifest = verified["manifest"]
        validate_manifest(manifest, manifest["source_sha"])
        for old in self.state.value["history"]:
            if old["release"]["manifest"]["source_sha"] == manifest["source_sha"]:
                require(old["release"]["manifest_digest"] == verified["manifest_digest"], "Historical release mapping changed")
        self.target = verified
        self.environment["API_IMAGE"] = manifest["api"]
        self.environment["WEB_IMAGE"] = manifest["web"]

    def application_configuration(self):
        config = json.loads(self.dc("config", "--format", "json"))
        api = config["services"]["api"]["environment"]
        mysql = config["services"]["mysql"]["environment"]
        key = api["Jwt__SigningKey"]
        require(re.fullmatch(r"[0-9a-fA-F]{64,}", key) is not None and
                key not in (mysql["MYSQL_PASSWORD"], mysql["MYSQL_ROOT_PASSWORD"]), "Use an independent strong JWT key")
        require(api["ASPNETCORE_ENVIRONMENT"] == "Production", "Production mode is mandatory")
        self.access_configuration(api["Application__BaseUrl"], api["Deployment__AccessMode"])

    def access_configuration(self, base_url, mode):
        self.run(["nginx", "-t"])
        if mode == "restricted-http":
            require(re.fullmatch(r"http://[A-Za-z0-9.-]+(?::[0-9]+)?", base_url) is not None,
                    "Restricted HTTP testing requires an explicit origin without path")
            active = Path("/etc/nginx/sites-enabled/racebib")
            require(active.is_file() and active.read_bytes() == (self.root / "nginx/racebib.conf").read_bytes(),
                    "Restricted HTTP requires the installed fail-closed RaceBib Nginx template")
            require(b"include /etc/nginx/racebib-access.conf;" in active.read_bytes(), "Tester policy is not included in the active site")
            access = Path("/etc/nginx/racebib-access.conf")
            require(access.is_file() and access.stat().st_uid == 0 and not access.stat().st_mode & 0o022,
                    "Access policy must be root-controlled")
            validate_http_policy(access.read_text())
            self.run(["systemctl", "reload", "nginx"])
        else:
            require(mode == "https" and re.fullmatch(r"https://[A-Za-z0-9.-]+", base_url) is not None,
                    "Normal access requires an HTTPS origin; HTTP is disposable restricted testing only")
            hostname = urllib.parse.urlparse(base_url).hostname
            template = (self.root / "nginx/racebib-https.conf.example").read_text().replace("__DOMAIN__", hostname)
            active = Path("/etc/nginx/sites-enabled/racebib")
            require(active.is_file() and active.read_text() == template,
                    "HTTPS requires the reviewed TLS template: port 80 redirects and never proxies authentication")
            self.run(["systemctl", "reload", "nginx"])
            try:
                # A first deployment has no web service yet. Verify TLS now; HTTP readiness after startup.
                with socket.create_connection((hostname, 443), timeout=15) as connection:
                    with ssl.create_default_context().wrap_socket(connection, server_hostname=hostname):
                        pass
            except OSError:
                raise SafetyError("Verified public HTTPS is unavailable") from None
            self.public_url = base_url

    def stop_writers(self):
        # This must also work during restore with no release, JWT key or application configuration.
        for service in ("web", "api"):
            ids = self.run(["docker", "container", "ls", "--quiet", "--filter",
                            "label=com.docker.compose.project=" + PROJECT, "--filter",
                            "label=com.docker.compose.service=" + service]).decode().split()
            if ids:
                containers = json.loads(self.run(["docker", "container", "inspect", *ids]))
                # A timed-out docker CLI can leave a migration running in the daemon.
                # Stop regular writers only; never automatically kill the one-shot migration.
                writers = [item["Id"] for item in containers if item["Name"] != "/" + ONEOFF and
                           item["Config"].get("Labels", {}).get("com.docker.compose.oneoff", "false").lower() != "true"]
                if writers:
                    self.run(["docker", "container", "stop", "--time", "30", *writers])

    def pull(self):
        self.dc("pull", "api", "web") # Never pull/recreate MySQL as part of an application release.
        for component in ("api", "web"):
            image = self.target["manifest"][component]
            details = json.loads(self.run(["docker", "image", "inspect", image]))[0]
            labels = details["Config"].get("Labels") or {}
            require(details.get("Os") == "linux" and details.get("Architecture") == "amd64" and
                    labels.get("org.opencontainers.image.revision") == self.target["manifest"]["source_sha"] and
                    labels.get("org.opencontainers.image.source") == "https://github.com/vreboton/racebib",
                    "Pulled image platform/source labels do not match the verified manifest")

    def oneoff(self, command, *, extra_env=None):
        self.no_oneoff()
        arguments = ["run", "--rm", "--no-deps", "--name", ONEOFF]
        for key in extra_env or ():
            arguments += ["-e", key]
        previous = self.environment.copy()
        self.environment.update(extra_env or {})
        try:
            return self.dc(*arguments, "api", command)
        finally:
            self.environment = previous

    def schema(self):
        # API logs may precede its final JSON line. Never print captured output.
        raw = self.oneoff("--verify-schema").decode().splitlines()
        require(raw, "Schema verifier returned no result")
        result = json.loads(raw[-1])
        require(result.get("status") == "ready" and re.fullmatch(r"[0-9a-f]{64}", result.get("schemaHash", "")),
                "Schema verifier did not prove readiness")
        return result["schemaHash"]

    def application_ready(self, schema_hash):
        self.dc("up", "-d", "--no-deps", "--wait", "--wait-timeout", "240", "api")
        raw = self.dc("exec", "-T", "api", "curl", "-fsS", "--max-time", "90", "http://127.0.0.1:8080/health/ready")
        result = json.loads(raw)
        require(result.get("status") == "ready" and result.get("schemaHash") == schema_hash,
                "Running API database-backed checks did not match the verified schema")
        self.dc("up", "-d", "--no-deps", "--wait", "--wait-timeout", "180", "web")
        self.run(["curl", "-fsS", "--max-time", "15", "http://127.0.0.1:8080/health"])
        if getattr(self, "public_url", None):
            try:
                with urllib.request.urlopen(self.public_url + "/health", timeout=15) as response:
                    require(response.status == 200 and response.url.startswith("https://"), "Public HTTPS routing check failed")
            except OSError:
                raise SafetyError("Public HTTPS routing check failed") from None

    def backup(self):
        directory = self.root / "backups"
        directory.mkdir(mode=0o700, exist_ok=True)
        os.chmod(directory, 0o700)
        destination = directory / ("mysql-" + time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + uuid.uuid4().hex[:12] + ".sql.gz")
        temporary = destination.with_suffix(destination.suffix + ".partial")
        command = ["docker", "compose", "--project-name", PROJECT, "--env-file", str(self.root / ".env"),
                   "-f", str(self.root / "compose.database.yml"), "exec", "-T", "mysql", "sh", "-c",
                   'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysqldump -uroot --single-transaction --quick --routines --events --triggers --no-tablespaces --set-gtid-purged=OFF "$MYSQL_DATABASE"']
        # gzip in this process bounds RAM; private temporary stderr never reaches application/CI logs.
        with tempfile.TemporaryFile() as errors:
            process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=errors, env=self.environment)
            try:
                with gzip.open(temporary, "wb") as output:
                    shutil.copyfileobj(process.stdout, output, length=64 * 1024)
                require(process.wait(timeout=30) == 0, "Database backup failed; no completed backup published")
                with gzip.open(temporary, "rb") as check:
                    while check.read(64 * 1024):
                        pass
                with temporary.open("rb") as data:
                    digest = "sha256:" + hashlib.file_digest(data, "sha256").hexdigest()
                    os.fsync(data.fileno())
                os.replace(temporary, destination)
                atomic_json(Path(str(destination) + ".json"), {"digest": digest, "at": now(), "database_only": True})
            except BaseException:
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=30)
                temporary.unlink(missing_ok=True)
                raise
        print("Verified gzip/exit status/checksum backup:", destination.name)
        return str(destination)

    def retain_backups(self):
        protected = set()
        def collect(value):
            if isinstance(value, dict):
                if isinstance(value.get("backup"), str):
                    protected.add(value["backup"])
                for nested in value.values():
                    collect(nested)
            elif isinstance(value, list):
                for nested in value:
                    collect(nested)
        collect(self.state.value["inflight"])
        collect(self.state.value["history"][-2:])
        for file in (self.root / "backups").glob("mysql-*.sql.gz"):
            sidecar = Path(str(file) + ".json")
            if str(file) not in protected and sidecar.is_file() and time.time() - file.stat().st_mtime > 7 * 86400:
                file.unlink()
                sidecar.unlink()

    def deploy(self, verified, *, migrate=False, recovery=False, reason=None):
        if not recovery:
            self.state.require_idle()
        else:
            require(self.state.value["inflight"] is not None, "No interrupted operation to recover")
        self.bind(verified)
        config = self.db_configuration()
        self.db_baseline(config)
        self.db_runtime(config)
        self.no_oneoff()
        self.application_configuration()
        self.pull() # Network/artifact errors cannot disturb a working release.
        self.state.begin("recovery" if recovery else "deployment", verified, reason, recovery=recovery)
        try:
            self.stop_writers()
            self.state.stage("writers-stopped")
            backup = self.backup()
            self.state.stage("backed-up", backup=backup)
            if migrate:
                self.state.stage("migration-in-progress")
                self.oneoff("--migrate")
            self.state.stage("schema-verification")
            schema_hash = self.schema() # Also mandatory for rollback and redeployment.
            self.state.stage("schema-verified", schema_hash=schema_hash)
            self.application_ready(schema_hash)
            self.state.stage("application-verified")
            self.state.commit(verified, schema_hash)
        except BaseException:
            try:
                self.stop_writers()
            except BaseException:
                pass # The persistent barrier is authoritative even if Docker or this host dies.
            raise
        print("Verified release:", verified["manifest"]["source_sha"], verified["manifest_digest"])

    def provision_admin(self, current, values):
        """State-changing phase after CLI configuration and credential validation."""
        self.state.begin("administrator-provisioning", current)
        commit_checkpoint = None
        try:
            self.stop_writers() # Avoid a fourth simultaneous container on the 1 GiB host.
            backup = self.backup()
            self.state.stage("administrator-provisioning", backup=backup)
            self.oneoff("--provision-admin", extra_env=values)
            schema_hash = self.schema()
            self.application_ready(schema_hash)
            self.state.stage("application-verified")
            commit_checkpoint = copy.deepcopy(self.state.value)
            self.state.commit(current, schema_hash)
        except BaseException:
            try:
                self.stop_writers()
            except BaseException:
                pass # Match deploy(): cleanup must not mask the original failure.
            if commit_checkpoint is not None:
                # commit() clears its in-memory barrier before saving. Reinstate the last
                # maintenance state even if a failed directory fsync followed file replacement.
                self.state.value = commit_checkpoint
                try:
                    self.state.save()
                except BaseException:
                    pass # Failed storage requires incident recovery; never report success.
            raise
        print("Administrator provisioned. Remove admin.env and retain the credential only in the approved password manager.")

    def initialize_database(self, adopt=False, *, resume=False, reason=None):
        if resume:
            pending = self.state.value["inflight"] or {}
            require(adopt and pending.get("kind") == "database-initialization" and
                    pending.get("stage") == "prepared" and pending.get("target") is None and
                    self.state.value["current"] is None and self.state.value["previous"] is None and
                    not self.state.value["history"],
                    "Resume requires adoption of an interrupted first database initialization")
            require(reason and len(reason.strip()) >= 12, "A meaningful non-secret initialization recovery reason is required")
        else:
            self.state.require_idle()
        require(not (self.root / "state/database.json").exists(), "Database already initialized; use database-start or maintenance")
        config = self.db_configuration()
        self.no_oneoff()
        volumes = self.run(["docker", "volume", "ls", "--filter", "name=^" + config["volumes"]["mysql_data"]["name"] + "$", "--quiet"])
        require(adopt or not volumes.strip(), "Existing data volume found; review it and explicitly adopt, never reset it")
        self.state.begin("database-initialization", reason=reason, recovery=resume)
        if adopt:
            self.db_runtime(config, healthy=False) # Adoption must not silently replace the existing image/config.
            self.dc("start", "--wait", "--wait-timeout", "420", "mysql", database=True)
        else:
            self.dc("pull", "mysql", database=True)
            self.dc("up", "-d", "--wait", "--wait-timeout", "420", "mysql", database=True)
        self.db_runtime(config)
        self.save_db_baseline(config)
        self.state.stage("database-initialized")
        # Initialization creates a barrier too. First deployment MUST explicitly recover/verify the initial schema.
        print("Database initialized. Run recover.sh SHA --resume-migrations --reason '...initial reviewed schema...' next.")

    def database_start(self):
        config = self.db_configuration()
        self.db_baseline(config)
        self.db_runtime(config, healthy=False)
        self.dc("start", "--wait", "--wait-timeout", "420", "mysql", database=True)
        self.db_runtime(config)

    def database_maintenance(self, verified, reason):
        self.state.require_idle()
        self.bind(verified)
        config = self.db_configuration()
        require((self.root / "state/database.json").exists(), "Database baseline required")
        self.no_oneoff()
        self.state.begin("database-maintenance", verified, reason)
        self.stop_writers()
        backup = self.backup() # Old container and OLD credentials; before any recreation.
        self.state.stage("database-change-in-progress", backup=backup)
        self.dc("pull", "mysql", database=True)
        self.dc("up", "-d", "--wait", "--wait-timeout", "420", "mysql", database=True)
        self.db_runtime(config)
        self.save_db_baseline(config)
        self.state.stage("database-changed-recovery-required")
        print("Database change completed; writers remain stopped. Explicit recover.sh verification is required.")

    def restore_clean(self, filename, reason):
        config = self.db_configuration()
        self.db_baseline(config)
        self.db_runtime(config)
        self.no_oneoff()
        file = Path(filename).resolve(strict=True)
        require(file.parent == (self.root / "backups").resolve() and file.name.endswith(".sql.gz"), "Use a root-controlled backup in /opt/racebib/backups")
        private_file(file)
        metadata = json.loads(Path(str(file) + ".json").read_text())
        with file.open("rb") as data:
            require("sha256:" + hashlib.file_digest(data, "sha256").hexdigest() == metadata["digest"], "Backup checksum mismatch")
        # Clean-target only: do not delete/drop existing production databases or restore over existing tables.
        count = self.dc("exec", "-T", "mysql", "sh", "-c",
                        'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot -Nse "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=\'$MYSQL_DATABASE\'"', database=True)
        require(count.strip() == b"0", "Target is not empty; restore on a separately initialized clean database/host")
        self.state.begin("clean-restore", reason=reason, recovery=self.state.value["inflight"] is not None)
        self.stop_writers()
        # No application release/JWT/base URL is needed to restore a clean target.
        database_id = self.dc("ps", "--quiet", "mysql", database=True).decode().strip()
        require(database_id and re.fullmatch(r"[0-9a-f]+", database_id), "Missing initialized MySQL container")
        # A named client keeps recovery blocked if this orchestrator/SSH session dies during import.
        # Sharing only MySQL's network namespace allows root loopback access without a remote root grant.
        command = ["docker", "run", "--rm", "-i", "--name", ONEOFF, "--network", "container:" + database_id,
                   "--user", "mysql", "--memory", "128m", "--memory-swap", "256m", "--cpus", "0.5",
                   "--read-only", "--tmpfs", "/tmp:rw,nosuid,nodev,size=16m", "--tmpfs", "/var/lib/mysql:rw,size=1m",
                   "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true",
                   "-e", "MYSQL_ROOT_PASSWORD", "-e", "MYSQL_DATABASE", "--entrypoint", "sh", config["services"]["mysql"]["image"],
                   "-c", 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql --protocol=TCP -h127.0.0.1 -uroot "$MYSQL_DATABASE"']
        client_environment = self.environment.copy()
        client_environment.update(config["services"]["mysql"]["environment"])
        self.state.stage("restore-in-progress", backup=str(file))
        with gzip.open(file, "rb") as source, tempfile.TemporaryFile() as errors:
            process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=errors, env=client_environment)
            try:
                shutil.copyfileobj(source, process.stdin, length=64 * 1024)
                process.stdin.close()
                require(process.wait(timeout=60) == 0, "Restore import failed; target remains quarantined")
            finally:
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=30)
        self.state.stage("restored-verification-required")
        print("Imported into clean target only. Recovery barrier remains; run recover.sh with the matching immutable release.")


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("deploy", "recover", "database-maintenance"):
        p = sub.add_parser(name)
        p.add_argument("sha")
        p.add_argument("--release-tag")
        if name != "database-maintenance":
            p.add_argument("--migrate" if name == "deploy" else "--resume-migrations", action="store_true")
        if name != "deploy":
            p.add_argument("--reason", required=True)
    sub.add_parser("rollback")
    sub.add_parser("backup")
    sub.add_parser("database-start")
    p = sub.add_parser("database-init")
    p.add_argument("--adopt-existing", action="store_true")
    p.add_argument("--resume-initialization", action="store_true")
    p.add_argument("--reason")
    p = sub.add_parser("restore")
    p.add_argument("backup")
    p.add_argument("--clean-target", action="store_true", required=True)
    p.add_argument("--reason", required=True)
    sub.add_parser("provision-admin")
    args = parser.parse_args()
    require(os.name == "posix" and os.geteuid() == 0, "Run this fixed-path operation as root on the Ubuntu host")
    os.umask(0o077)
    root = Path("/opt/racebib")
    require(root.is_dir() and root.stat().st_uid == 0 and not root.stat().st_mode & 0o022, "Deployment root must be root-owned and not group/world writable")
    with operation_lock(root / ".deployment.lock"):
        ops = Operations(root)
        if args.command == "database-init":
            ops.initialize_database(args.adopt_existing, resume=args.resume_initialization, reason=args.reason)
        elif args.command == "database-start":
            ops.database_start()
        elif args.command == "backup":
            config = ops.db_configuration()
            ops.db_runtime(config) # No successful application marker/JWT is needed.
            ops.no_oneoff()
            ops.backup()
            ops.retain_backups()
        elif args.command == "restore":
            ops.restore_clean(args.backup, args.reason)
        elif args.command == "rollback":
            target = ops.state.rollback_target()
            verified = verify_release(target["manifest"]["source_sha"], release_tag=target.get("release_tag"))
            require(verified["manifest_digest"] == target["manifest_digest"], "Rollback manifest changed")
            ops.deploy(verified)
        elif args.command == "provision-admin":
            ops.state.require_idle()
            current = ops.state.value["current"]
            require(current, "A successful verified release is required before administrator provisioning")
            ops.bind(current)
            config = ops.db_configuration()
            ops.db_baseline(config)
            ops.db_runtime(config)
            ops.application_configuration()
            ops.no_oneoff()
            private_file(root / "admin.env")
            values = {}
            for line in (root / "admin.env").read_text().splitlines():
                if not line or line.startswith("#"):
                    continue
                key, separator, value = line.partition("=")
                require(separator and key in ("SeedAdmin__Email", "SeedAdmin__Password") and key not in values, "Invalid admin.env keys")
                values[key] = value
            require(all(values.get(k) for k in ("SeedAdmin__Email", "SeedAdmin__Password")), "Missing bootstrap credentials")
            ops.provision_admin(current, values)
        elif args.command == "database-maintenance":
            require(len(args.reason.strip()) >= 12, "A meaningful non-secret maintenance reason is required")
            ops.database_maintenance(verify_release(args.sha, release_tag=args.release_tag), args.reason)
        else:
            ops.deploy(verify_release(args.sha, release_tag=args.release_tag), migrate=getattr(args, "migrate", False) or getattr(args, "resume_migrations", False),
                       recovery=args.command == "recover", reason=getattr(args, "reason", None))


if __name__ == "__main__":
    def interrupt(signum, frame):
        raise SafetyError("Operation interrupted; persistent recovery barrier remains")
    signal.signal(signal.SIGTERM, interrupt)
    signal.signal(signal.SIGINT, interrupt)
    try:
        main()
    except (SafetyError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        # Only deliberately sanitized errors reach logs. Never echo configuration, credentials or child output.
        message = str(error) if isinstance(error, SafetyError) else "Operation failed; inspect root-controlled state and container status"
        print(message, file=sys.stderr)
        sys.exit(1)
