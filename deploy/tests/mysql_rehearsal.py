"""Disposable local/CI MySQL rehearsal. Creates and removes ONLY UUID-labelled test resources.
Never selects an existing database, volume, or production Compose project.
"""
import argparse
import gzip
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--api-dll", required=True)
    args = parser.parse_args()
    dll = str(Path(args.api_dll).resolve(strict=True))
    name = "racebib-safety-" + uuid.uuid4().hex[:12]
    volume = name + "-data"
    password, root_password = secrets.token_hex(32), secrets.token_hex(32)
    environment = os.environ.copy()
    environment.update(MYSQL_DATABASE="racebib_test_safety", MYSQL_USER="racebib_test",
                       MYSQL_PASSWORD=password, MYSQL_ROOT_PASSWORD=root_password)

    def execute(command, **kwargs):
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=240, **kwargs)
        if result.returncode:
            # Only this script's new synthetic target is ever involved. Do not echo credential environment.
            raise RuntimeError("Rehearsal command failed: " + command[0] + " " + result.stderr.decode(errors="replace")[-2000:])
        return result.stdout

    def sql(statement, database="racebib_test_safety"):
        return execute(["docker", "exec", "-i", name, "sh", "-c",
                        'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot --batch --skip-column-names ' + database], input=statement.encode())

    api = None
    created = False
    restore_client = name + "-restore"
    try:
        execute(["docker", "volume", "create", "--label", "racebib.safety-rehearsal=" + name, volume])
        execute(["docker", "run", "-d", "--name", name, "--label", "racebib.safety-rehearsal=" + name,
                 "--memory", "448m", "--memory-swap", "768m", "--cpus", "1",
                 "--publish", "127.0.0.1::3306", "--mount", f"type=volume,source={volume},target=/var/lib/mysql",
                 "-e", "MYSQL_DATABASE", "-e", "MYSQL_USER", "-e", "MYSQL_PASSWORD", "-e", "MYSQL_ROOT_PASSWORD", "mysql:8.4",
                 "--innodb-buffer-pool-size=128M", "--max-connections=30", "--performance-schema=OFF",
                 "--temptable-max-ram=16M", "--innodb-log-buffer-size=8M", "--binlog-expire-logs-seconds=604800"], env=environment)
        created = True
        value = json.loads(execute(["docker", "inspect", name]))[0]
        assert value["Config"]["Labels"]["racebib.safety-rehearsal"] == name
        port = value["NetworkSettings"]["Ports"]["3306/tcp"][0]["HostPort"]
        for attempt in range(120):
            try:
                sql("SELECT 1;")
                break
            except RuntimeError:
                time.sleep(1)
        else:
            raise RuntimeError("MySQL did not initialize under the production memory limits")
        environment.update(ASPNETCORE_ENVIRONMENT="Testing", Jwt__SigningKey=secrets.token_hex(32),
                           Jwt__Issuer="RaceBib.Rehearsal", Jwt__Audience="RaceBib.Rehearsal", DOTNET_GCHeapHardLimit="0xA000000")
        def connection(database):
            return f"Server=127.0.0.1;Port={port};Database={database};User=racebib_test;Password={password};MaximumPoolSize=5"
        environment["ConnectionStrings__DefaultConnection"] = connection("racebib_test_safety")

        def maintenance(command, success=True):
            result = subprocess.run(["dotnet", dll, command], env=environment, capture_output=True, timeout=180)
            assert (result.returncode == 0) == success, (command + " unexpected result: " + result.stderr.decode(errors="replace")[-2000:])
            return result.stdout

        maintenance("--verify-schema", success=False) # Empty schema must not be considered ready.
        maintenance("--migrate")
        result = json.loads(maintenance("--verify-schema").decode().splitlines()[-1])
        assert result["status"] == "ready"
        environment.update(SeedAdmin__Email="bootstrap@example.test", SeedAdmin__Password="Synthetic!" + secrets.token_hex(16))
        maintenance("--provision-admin")
        maintenance("--provision-admin") # Existing approved admin is idempotent, never a password reset.
        saved_password = environment["SeedAdmin__Password"]
        environment["SeedAdmin__Password"] = "DifferentSynthetic!" + secrets.token_hex(16)
        maintenance("--provision-admin", success=False)
        environment["SeedAdmin__Password"] = saved_password

        with socket.socket() as candidate:
            candidate.bind(("127.0.0.1", 0))
            api_port = candidate.getsockname()[1]
        environment["ASPNETCORE_URLS"] = f"http://127.0.0.1:{api_port}"
        with tempfile.TemporaryFile() as output:
            api = subprocess.Popen(["dotnet", dll], env=environment, stdout=output, stderr=output)
            endpoint = f"http://127.0.0.1:{api_port}/health/ready"
            for attempt in range(60):
                try:
                    with urllib.request.urlopen(endpoint, timeout=3) as response:
                        assert json.load(response)["schemaHash"] == result["schemaHash"]
                    break
                except (OSError, ValueError):
                    assert api.poll() is None, "API terminated unexpectedly"
                    time.sleep(0.5)
            else:
                raise RuntimeError("Running API did not become database-ready")
            sql("ALTER TABLE Events ADD COLUMN RehearsalOnly int NULL;")
            maintenance("--verify-schema", success=False)
            try:
                urllib.request.urlopen(endpoint, timeout=15)
                raise AssertionError("Altered schema incorrectly passed running-API readiness")
            except urllib.error.HTTPError as error:
                assert error.code == 503
            sql("ALTER TABLE Events DROP COLUMN RehearsalOnly;")
            api.terminate()
            api.wait(timeout=30)
            api = None

        # A partial migration/history discrepancy cannot be hidden by process liveness.
        migration = sql("SELECT MigrationId,ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;").decode().strip().split("\t")
        sql("DELETE FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;")
        maintenance("--verify-schema", success=False)
        assert all(all(c.isalnum() or c in "_." for c in item) for item in migration)
        sql(f"INSERT INTO __EFMigrationsHistory (MigrationId,ProductVersion) VALUES ('{migration[0]}','{migration[1]}');")
        maintenance("--verify-schema")

        # Exercise a real logical backup and clean-target import without ever dropping a database.
        dump = execute(["docker", "exec", name, "sh", "-c",
                        'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysqldump -uroot --single-transaction --quick --routines --events --triggers --no-tablespaces --set-gtid-purged=OFF "$MYSQL_DATABASE"'])
        compressed = gzip.compress(dump)
        assert gzip.decompress(compressed) == dump
        restored = "racebib_test_restore_" + uuid.uuid4().hex[:8]
        sql(f"CREATE DATABASE {restored}; GRANT ALL ON {restored}.* TO 'racebib_test'@'%';")
        client_environment = environment.copy()
        client_environment["MYSQL_DATABASE"] = restored
        execute(["docker", "run", "--rm", "-i", "--name", restore_client, "--label", "racebib.safety-rehearsal=" + name,
                 "--network", "container:" + name, "--user", "mysql", "--memory", "128m", "--memory-swap", "256m",
                 "--read-only", "--tmpfs", "/tmp:rw,nosuid,nodev,size=16m", "--tmpfs", "/var/lib/mysql:rw,size=1m",
                 "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "-e", "MYSQL_ROOT_PASSWORD",
                 "-e", "MYSQL_DATABASE", "--entrypoint", "sh", "mysql:8.4", "-c",
                 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql --protocol=TCP -h127.0.0.1 -uroot "$MYSQL_DATABASE"'],
                input=gzip.decompress(compressed), env=client_environment)
        environment["ConnectionStrings__DefaultConnection"] = connection(restored)
        restored_result = json.loads(maintenance("--verify-schema").decode().splitlines()[-1])
        assert restored_result == result
        assert sql("SELECT COUNT(*) FROM AspNetUsers;", restored).strip() == b"1"
        environment.update(RACEBIB_MYSQL_TEST_CONNECTION=connection("racebib_test_safety"),
                           RACEBIB_MYSQL_TEST_REQUIRED="true", RACEBIB_CI_JWT_SIGNING_KEY=secrets.token_hex(32))
        project = Path(__file__).resolve().parents[2] / "backend/RaceTiming.Api.Tests/RaceTiming.Api.Tests.csproj"
        tests = execute(["dotnet", "test", str(project), "--configuration", "Release", "--no-build", "--no-restore",
                         "--filter", "Category=MySqlIntegration"], env=environment)
        print(tests.decode(errors="replace"))
        print("PASS: empty schema rejected; migrations verified; structural/history drift rejected; running readiness checked; bootstrap atomic/idempotent; synthetic backup restored and verified on a new database.")
    finally:
        if api and api.poll() is None:
            api.terminate()
            api.wait(timeout=30)
        if created:
            client = execute(["docker", "container", "ls", "--all", "--quiet", "--filter", "name=^/" + restore_client + "$"])
            if client.strip():
                info = json.loads(execute(["docker", "inspect", restore_client]))[0]
                assert info["Config"]["Labels"]["racebib.safety-rehearsal"] == name
                execute(["docker", "stop", restore_client])
                execute(["docker", "rm", restore_client])
            value = json.loads(execute(["docker", "inspect", name]))[0]
            assert value["Config"]["Labels"]["racebib.safety-rehearsal"] == name
            execute(["docker", "stop", name])
            execute(["docker", "rm", name])
        info = json.loads(execute(["docker", "volume", "inspect", volume]))[0]
        assert info["Labels"]["racebib.safety-rehearsal"] == name
        execute(["docker", "volume", "rm", volume])


if __name__ == "__main__":
    main()
