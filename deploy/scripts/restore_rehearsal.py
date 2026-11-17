"""Import/verify a backup in fresh, uniquely named resources on a DISPOSABLE separate host.
Never touches production containers/volumes and never automatically removes any database.
"""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import uuid

from operations import run
from release import SafetyError, require, verify_release


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("backup")
    parser.add_argument("source_sha")
    parser.add_argument("--release-tag")
    parser.add_argument("--mysql-image", required=True)
    parser.add_argument("--confirm-disposable-host", required=True, action="store_true")
    args = parser.parse_args()
    require(os.name == "posix" and os.geteuid() == 0, "Run as root on a separate disposable Ubuntu host")
    os.umask(0o077)
    import re
    require(re.fullmatch(r"mysql:8\.4@sha256:[0-9a-f]{64}", args.mysql_image), "A tested MySQL digest is required")
    require(not run(["docker", "container", "ls", "--all", "--quiet", "--filter", "label=com.docker.compose.project=racebib-production"]).strip(),
            "Production resources detected. Use a separate disposable host; never compete for its 1 GiB RAM")
    backup = Path(args.backup).resolve(strict=True)
    metadata = json.loads(Path(str(backup) + ".json").read_text())
    with backup.open("rb") as source:
        require("sha256:" + hashlib.file_digest(source, "sha256").hexdigest() == metadata["digest"], "Backup checksum mismatch")
    verified = verify_release(args.source_sha, release_tag=args.release_tag)
    project = "racebib-rehearsal-" + uuid.uuid4().hex[:12]
    # Unique project means fresh volume/network; no existing database is reset.
    with tempfile.TemporaryDirectory(prefix="racebib-rehearsal-") as temporary:
        directory = Path(temporary)
        password = secrets.token_hex(32)
        root_password = secrets.token_hex(32)
        db_env = directory / "database.env"
        db_env.write_text(f"MYSQL_IMAGE={args.mysql_image}\nMYSQL_DATABASE=racebib_restore_test\nMYSQL_USER=racebib_test\nMYSQL_PASSWORD={password}\nMYSQL_ROOT_PASSWORD={root_password}\n")
        environment = os.environ.copy()
        environment.update(MYSQL_IMAGE=args.mysql_image, MYSQL_DATABASE="racebib_restore_test", MYSQL_USER="racebib_test",
                           MYSQL_PASSWORD=password, MYSQL_ROOT_PASSWORD=root_password)
        command = ["docker", "compose", "-p", project, "--env-file", str(db_env), "-f", "/opt/racebib/compose.database.yml"]
        config = json.loads(run(command + ["config", "--format", "json"], env=environment))
        require(config["volumes"]["mysql_data"]["name"] == project + "_mysql_data" and
                config["networks"]["database"]["name"] == project + "_database" and
                not config["services"]["mysql"].get("ports"), "Rehearsal resources are not isolated")
        require(not run(["docker", "volume", "ls", "--quiet", "--filter", "name=^" + project + "_mysql_data$"]).strip(),
                "Rehearsal volume unexpectedly already exists")
        run(command + ["pull", "mysql"], env=environment)
        run(command + ["up", "-d", "--wait", "--wait-timeout", "420", "mysql"], env=environment)
        try:
            with gzip.open(backup, "rb") as source, tempfile.TemporaryFile() as errors:
                process = subprocess.Popen(command + ["exec", "-T", "mysql", "sh", "-c",
                    'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot "$MYSQL_DATABASE"'], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=errors, env=environment)
                try:
                    shutil.copyfileobj(source, process.stdin, length=64 * 1024)
                    process.stdin.close()
                    require(process.wait(timeout=60) == 0, "Rehearsal import failed")
                finally:
                    if process.poll() is None:
                        process.terminate()
                        process.wait(timeout=30)
            api_env = directory / "api.env"
            api_env.write_text("ASPNETCORE_ENVIRONMENT=Production\nDOTNET_GCHeapHardLimit=0xA000000\n"
                               f"Jwt__SigningKey={secrets.token_hex(32)}\n"
                               f"ConnectionStrings__DefaultConnection=Server=mysql;Database=racebib_restore_test;User=racebib_test;Password={password};MaximumPoolSize=5\n")
            image = verified["manifest"]["api"]
            run(["docker", "pull", image])
            output = run(["docker", "run", "--rm", "--network", project + "_database", "--user", "app",
                          "--memory", "256m", "--memory-swap", "512m", "--cap-drop", "ALL",
                          "--security-opt", "no-new-privileges:true", "--env-file", str(api_env), image, "--verify-schema"])
            result = json.loads(output.decode().splitlines()[-1])
            require(result.get("status") == "ready", "Restored database did not pass application verification")
            print("Isolated restore/schema/application-read rehearsal passed:", project, result["schemaHash"])
        finally:
            run(command + ["stop", "mysql"], env=environment)
            print("Only fresh rehearsal resources remain for inspection. Project:", project)
            print("After inspection, remove ONLY this project's stopped mysql container and named mysql_data volume explicitly.")


if __name__ == "__main__":
    try:
        main()
    except (SafetyError, OSError, ValueError, KeyError, subprocess.SubprocessError):
        print("Isolated restore rehearsal failed; production resources were not selected. Inspect the rehearsal project.", file=sys.stderr)
        sys.exit(1)
