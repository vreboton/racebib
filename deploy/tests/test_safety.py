import copy
import json
import gzip
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from release import (SafetyError, canonical, sha256, validate_manifest, require_jobs,
                     verify_run, verify_release, preflight, BUILD_JOBS, PUBLISH_JOBS)
from release_state import ReleaseState, atomic_json
from operations import Operations, operation_lock, validate_http_policy


def release(letter):
    manifest = {"format": 1, "repository": "vreboton/racebib", "source_sha": letter * 40,
                "api": "ghcr.io/vreboton/racebib-api@sha256:" + letter * 64,
                "web": "ghcr.io/vreboton/racebib-web@sha256:" + letter * 64,
                "build": {"run_id": 10, "attempt": 1}, "publication": {"run_id": 20, "attempt": 1}}
    manifest["safety"] = {"run_id": 15, "attempt": 1}
    return {"manifest": manifest, "manifest_digest": sha256(canonical(manifest)), "release_id": 30}


class FakeOperations(Operations):
    """Fault injection at external boundaries; real orchestration and durable state run unchanged."""
    def __init__(self, root, fail=None):
        super().__init__(root)
        self.events = []
        self.fail = fail

    def event(self, name):
        self.events.append(name)
        if name == self.fail:
            raise SafetyError("Injected " + name + " failure")

    def db_configuration(self):
        self.event("db-config")
        return {"services": {"mysql": {"image": "mysql:8.4@sha256:" + "a" * 64,
                                      "environment": {"MYSQL_DATABASE": "racebib_test", "MYSQL_ROOT_PASSWORD": "test-only"}}}}

    def db_baseline(self, config):
        self.event("db-baseline")

    def db_runtime(self, config, **kwargs):
        self.event("db-runtime")

    def no_oneoff(self):
        self.event("no-oneoff")

    def application_configuration(self):
        self.event("access-config")

    def pull(self):
        self.event("pull")

    def stop_writers(self):
        self.event("stop")

    def backup(self):
        self.event("backup")
        return "/opt/racebib/backups/test.sql.gz"

    def oneoff(self, command, **kwargs):
        self.event(command)

    def schema(self):
        self.event("schema")
        return "c" * 64

    def application_ready(self, schema):
        self.event("application")


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def test_repeat_deploy_preserves_previous_distinct_release(self):
        ops = FakeOperations(self.root)
        ops.deploy(release("a"))
        ops.deploy(release("b"))
        ops.deploy(release("b"))
        state = ReleaseState(ops.state.path)
        self.assertEqual(state.value["previous"], release("a"))
        self.assertEqual(state.value["current"], release("b"))
        self.assertIsNone(state.value["inflight"])

    def test_failures_and_restart_never_advance_success_and_require_recovery(self):
        for stage in ("backup", "--migrate", "schema", "application"):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as root:
                ops = FakeOperations(Path(root))
                ops.deploy(release("a"))
                ops.fail = stage
                with self.assertRaises(SafetyError):
                    ops.deploy(release("b"), migrate=True)
                persisted = ReleaseState(ops.state.path)
                self.assertEqual(persisted.value["current"], release("a"))
                self.assertEqual(len(persisted.value["history"]), 1)
                self.assertIsNotNone(persisted.value["inflight"])
                self.assertEqual(ops.events[-1], "stop")
                restarted = FakeOperations(Path(root))
                with self.assertRaises(SafetyError):
                    restarted.deploy(release("b"))
                with self.assertRaises(SafetyError):
                    restarted.state.rollback_target()
                self.assertEqual(restarted.events, [])

    def test_pull_failure_does_not_stop_working_application(self):
        ops = FakeOperations(self.root)
        ops.deploy(release("a"))
        ops.events = []
        ops.fail = "pull"
        with self.assertRaises(SafetyError):
            ops.deploy(release("b"))
        self.assertNotIn("stop", ops.events)
        self.assertIsNone(ReleaseState(ops.state.path).value["inflight"])

    def test_rollback_runs_schema_gate_and_rejects_incompatible_target(self):
        ops = FakeOperations(self.root)
        ops.deploy(release("a"))
        ops.deploy(release("b"), migrate=True)
        target = ops.state.rollback_target()
        ops.fail = "schema"
        ops.events = []
        with self.assertRaises(SafetyError):
            ops.deploy(target)
        self.assertNotIn("application", ops.events)
        self.assertNotIn("--migrate", ops.events)
        self.assertEqual(ReleaseState(ops.state.path).value["current"], release("b"))

    def test_recovery_reverifies_and_records_auditable_reason(self):
        ops = FakeOperations(self.root, fail="--migrate")
        with self.assertRaises(SafetyError):
            ops.deploy(release("a"), migrate=True)
        restarted = FakeOperations(self.root)
        restarted.deploy(release("a"), recovery=True, migrate=True, reason="Reviewed partial DDL and repaired test target")
        state = ReleaseState(restarted.state.path).value
        self.assertIsNone(state["inflight"])
        self.assertIsNotNone(state["history"][0]["operation"]["prior"])
        self.assertIn("schema", restarted.events)
        self.assertIn("application", restarted.events)

    def test_interruption_barrier_survives_without_any_successful_release(self):
        state = ReleaseState(self.root / "state/release-state.json")
        state.begin("deployment", release("a"))
        state.stage("migration-in-progress")
        restarted = ReleaseState(state.path)
        with self.assertRaises(SafetyError):
            restarted.require_idle()
        self.assertIsNone(restarted.value["current"])

    def test_success_cannot_be_recorded_without_application_verification(self):
        state = ReleaseState(self.root / "release-state.json")
        state.begin("deployment", release("a"))
        state.stage("schema-verified")
        with self.assertRaises(SafetyError):
            state.commit(release("a"), "c" * 64)

    def test_history_remapping_fails_before_disruption(self):
        ops = FakeOperations(self.root)
        ops.deploy(release("a"))
        bad = release("a")
        bad["manifest_digest"] = "sha256:" + "f" * 64
        ops.events = []
        with self.assertRaises(SafetyError):
            ops.deploy(bad)
        self.assertEqual(ops.events, [])

    def test_atomic_replace_failure_leaves_entire_prior_document(self):
        path = self.root / "state.json"
        atomic_json(path, {"current": "a", "previous": "z"})
        with patch("release_state.os.replace", side_effect=OSError("interrupted")):
            with self.assertRaises(OSError):
                atomic_json(path, {"current": "b", "previous": "a"})
        self.assertEqual(json.loads(path.read_text()), {"current": "a", "previous": "z"})

    def test_corrupt_or_missing_state_is_not_silently_reset(self):
        path = self.root / "state/release-state.json"
        path.parent.mkdir()
        path.write_text("{broken")
        with self.assertRaises(SafetyError):
            ReleaseState(path)
        path.unlink()
        (path.parent / "database.json").write_text("{}")
        with self.assertRaises(SafetyError):
            Operations(self.root)

    def test_database_drift_is_rejected_before_writers_stop(self):
        ops = FakeOperations(self.root, fail="db-baseline")
        with self.assertRaises(SafetyError):
            ops.deploy(release("a"))
        self.assertNotIn("stop", ops.events)

    def test_clean_restore_needs_no_successful_release_or_app_configuration(self):
        ops = FakeOperations(self.root)
        backup = self.root / "backups/mysql-test.sql.gz"
        backup.parent.mkdir()
        backup.write_bytes(gzip.compress(b"-- synthetic clean target only\n"))
        Path(str(backup) + ".json").write_text(json.dumps({"digest": sha256(backup.read_bytes())}))
        class Process:
            stdin = io.BytesIO()
            def wait(self, timeout): return 0
            def poll(self): return 0
        with patch("operations.private_file"), patch.object(ops, "dc", side_effect=[b"0\n", b"abcdef\n"]), \
             patch("operations.subprocess.Popen", return_value=Process()):
            ops.restore_clean(str(backup), "Reviewed clean synthetic recovery target")
        persisted = ReleaseState(ops.state.path).value
        self.assertIsNone(persisted["current"])
        self.assertEqual(persisted["inflight"]["stage"], "restored-verification-required")
        self.assertNotIn("access-config", ops.events)
        self.assertNotIn("API_IMAGE", ops.environment)
        self.assertIn("stop", ops.events)

    def test_nonempty_restore_target_is_rejected_without_import(self):
        ops = FakeOperations(self.root)
        backup = self.root / "backups/mysql-test.sql.gz"
        backup.parent.mkdir()
        backup.write_bytes(gzip.compress(b"test"))
        Path(str(backup) + ".json").write_text(json.dumps({"digest": sha256(backup.read_bytes())}))
        with patch("operations.private_file"), patch.object(ops, "dc", return_value=b"9\n"), \
             patch("operations.subprocess.Popen") as process:
            with self.assertRaises(SafetyError):
                ops.restore_clean(str(backup), "Reviewed but nonempty recovery target")
            process.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "Linux flock behavior is verified by deployment-safety CI")
    def test_concurrent_operation_fails_without_replacing_state(self):
        with operation_lock(self.root / "lock"):
            with self.assertRaises(SafetyError):
                with operation_lock(self.root / "lock"):
                    self.fail("second lock acquisition must not succeed")

    def test_http_policy_rejects_unrestricted_access_and_missing_deny(self):
        validate_http_policy("allow 127.0.0.1/32;\nallow 192.0.2.10;\ndeny all;\n")
        for text in ("allow all;\ndeny all;", "allow 0.0.0.0/0;\ndeny all;", "allow 192.0.2.1;", "deny all;\nallow 192.0.2.1;"):
            with self.subTest(text=text), self.assertRaises((SafetyError, ValueError)):
                validate_http_policy(text)

    def test_failure_cleanup_never_kills_a_detached_migration(self):
        stopped = []
        def runner(command, **kwargs):
            if "ls" in command:
                return b"web-id\n" if command[-1].endswith("=web") else b"api-id\nmigration-id\n"
            if "inspect" in command:
                return json.dumps([{"Id": identifier, "Name": "/racebib-maintenance" if identifier == "migration-id" else "/" + identifier,
                                    "Config": {"Labels": {"com.docker.compose.oneoff": "True" if identifier == "migration-id" else "False"}}}
                                   for identifier in command[3:]]).encode()
            stopped.extend(command[command.index("30") + 1:])
            return b""
        ops = Operations(self.root, runner=runner)
        ops.stop_writers()
        self.assertEqual(stopped, ["web-id", "api-id"])


class ProvenanceTests(unittest.TestCase):
    def test_complete_provenance_is_required_and_asset_digest_is_verified(self):
        raw = canonical(release("a")["manifest"]) + b"\n"
        digest = sha256(raw)
        asset_digest = digest
        def response(path):
            if path.startswith("/releases/tags/"):
                return {"id": 30, "immutable": True, "draft": False, "tag_name": "racebib-" + "a" * 40,
                        "assets": [{"name": "release.json", "state": "uploaded", "size": len(raw), "digest": asset_digest}]}
            if path.startswith("/git/ref/"):
                return {"object": {"type": "commit", "sha": "a" * 40}}
            if path.startswith("/compare/"):
                return {"status": "ahead"}
            if path.startswith("/actions/workflows/"):
                return {"id": {"build.yml": 1, "publish-images.yml": 2, "deployment-safety.yml": 3}[path.rsplit("/", 1)[-1]]}
            run_id = int(path.split("/")[3])
            if "/jobs?" in path:
                required = {10: BUILD_JOBS, 20: PUBLISH_JOBS, 15: {"deployment-safety"}}[run_id]
                return {"jobs": [{"name": name, "status": "completed", "conclusion": "success"} for name in required]}
            return {"workflow_id": {10: 1, 20: 2, 15: 3}[run_id], "event": "workflow_run" if run_id == 20 else "push",
                    "status": "completed", "conclusion": "success", "head_branch": "main", "head_sha": "a" * 40,
                    "display_title": "publish " + "a" * 40, "repository": {"full_name": "vreboton/racebib"},
                    "head_repository": {"full_name": "vreboton/racebib"}}
        with patch("release.request", side_effect=response), patch("release.urllib.request.urlopen") as download:
            download.return_value.__enter__.return_value.read.return_value = raw
            verified = verify_release("a" * 40)
            self.assertEqual(verified["manifest_digest"], digest)
            asset_digest = "sha256:" + "0" * 64
            with self.assertRaises(SafetyError):
                verify_release("a" * 40)

    def test_only_fixed_packages_and_digest_references_are_accepted(self):
        manifest = release("a")["manifest"]
        validate_manifest(manifest, "a" * 40)
        for image in ("ghcr.io/vreboton/racebib-api:latest", "evil.example/api@sha256:" + "a" * 64):
            bad = copy.deepcopy(manifest)
            bad["api"] = image
            with self.assertRaises(SafetyError):
                validate_manifest(bad, "a" * 40)

    def test_skipped_missing_failed_and_duplicate_jobs_are_rejected(self):
        good = [{"name": name, "status": "completed", "conclusion": "success"} for name in PUBLISH_JOBS]
        require_jobs(good, PUBLISH_JOBS)
        for conclusion in ("skipped", "failure", "cancelled", None):
            bad = copy.deepcopy(good)
            bad[0]["conclusion"] = conclusion
            with self.assertRaises(SafetyError):
                require_jobs(bad, PUBLISH_JOBS)
        with self.assertRaises(SafetyError):
            require_jobs(good[:-1], PUBLISH_JOBS)
        with self.assertRaises(SafetyError):
            require_jobs(good + [good[0]], PUBLISH_JOBS)

    def test_successful_workflow_summary_cannot_hide_skipped_jobs(self):
        def response(path):
            if "/workflows/" in path:
                return {"id": 99}
            if "/jobs?" in path:
                return {"jobs": [{"name": name, "status": "completed", "conclusion": "skipped"} for name in BUILD_JOBS]}
            return {"workflow_id": 99, "event": "push", "status": "completed", "conclusion": "success", "head_branch": "main",
                    "head_sha": "a" * 40, "repository": {"full_name": "vreboton/racebib"}, "head_repository": {"full_name": "vreboton/racebib"}}
        with patch("release.request", side_effect=response):
            with self.assertRaises(SafetyError):
                verify_run(1, 1, "build.yml", "push", "a" * 40, BUILD_JOBS)

    def test_mutable_release_and_existing_source_publication_are_rejected(self):
        with patch("release.request", return_value={"immutable": False, "draft": False}):
            with self.assertRaises(SafetyError):
                verify_release("a" * 40)
            with self.assertRaises(SafetyError):
                preflight("a" * 40, 1, 1)

    def test_source_mismatch_and_invalid_run_identity_are_rejected(self):
        with self.assertRaises(SafetyError):
            validate_manifest(release("a")["manifest"], "b" * 40)
        bad = release("a")["manifest"]
        bad["publication"]["run_id"] = True
        with self.assertRaises(SafetyError):
            validate_manifest(bad, "a" * 40)


if __name__ == "__main__":
    unittest.main()
