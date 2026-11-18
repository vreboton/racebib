import contextlib
import copy
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import local_verify
import ssh_entry

ROOT = Path(__file__).resolve().parents[2]
SHA = "a" * 40


def verified():
    return {"manifest_digest": "sha256:" + "b" * 64, "manifest": {
        "source_sha": SHA, "api": "ghcr.io/vreboton/racebib-api@sha256:" + "c" * 64,
        "web": "ghcr.io/vreboton/racebib-web@sha256:" + "d" * 64}}


class LocalControllerTests(unittest.TestCase):
    def test_preflight_requires_release_provenance_then_live_registry_for_canonical_and_recovery(self):
        for tag in ["", "racebib-" + SHA + "-recovery-1-1"]:
            with patch("local_verify.verify_release", return_value=verified()) as provenance, \
                 patch("local_verify.verify_registry_images") as registry:
                evidence = local_verify.verify(SHA, tag)
                provenance.assert_called_once_with(SHA, release_tag=tag or None)
                registry.assert_called_once_with(verified()["manifest"])
                self.assertEqual(evidence["manifest_digest"], verified()["manifest_digest"])
                self.assertEqual(evidence["release_tag"], tag or "racebib-" + SHA)

    def test_bad_selector_and_failed_provenance_or_registry_fail_closed(self):
        with patch("local_verify.verify_release") as provenance, self.assertRaises(ValueError):
            local_verify.verify(SHA, "--migrate")
        provenance.assert_not_called()
        with patch("local_verify.verify_release", side_effect=RuntimeError("failed tests")), \
             patch("local_verify.verify_registry_images") as registry, self.assertRaises(RuntimeError):
            local_verify.verify(SHA, "")
        registry.assert_not_called()
        with patch("local_verify.verify_release", return_value=verified()), \
             patch("local_verify.verify_registry_images", side_effect=RuntimeError("digest mismatch")), self.assertRaises(RuntimeError):
            local_verify.verify(SHA, "")

    def test_host_state_receipt_requires_idle_current_history_and_schema(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "state").mkdir()
            path = root / "state/release-state.json"
            with self.assertRaises(RuntimeError):
                ssh_entry.inspect_state(SHA, root)
            state = {"format": 1, "current": verified(), "previous": None, "inflight": None,
                     "history": [{"release": verified(), "schema_hash": "e" * 64}]}
            path.write_text(json.dumps(state))
            receipt = ssh_entry.inspect_state(SHA, root)
            self.assertEqual(receipt["manifest_digest"], verified()["manifest_digest"])
            self.assertEqual(receipt["api"], verified()["manifest"]["api"])
            for mutate in [lambda s: s.update(inflight={"stage": "backed-up"}),
                           lambda s: s.update(history=[]),
                           lambda s: s["history"][-1].update(schema_hash="bad"),
                           lambda s: s["history"][-1]["release"].update(manifest_digest="sha256:" + "f" * 64),
                           lambda s: s["current"]["manifest"].update(source_sha="f" * 40)]:
                invalid = copy.deepcopy(state)
                mutate(invalid)
                path.write_text(json.dumps(invalid))
                with self.assertRaises(RuntimeError):
                    ssh_entry.inspect_state(SHA, root)

    def test_host_wrapper_never_emits_receipt_on_engine_state_or_selector_failure(self):
        receipt = {"source_sha": SHA, "release_tag": "racebib-" + SHA, "manifest_digest": verified()["manifest_digest"]}
        with patch.dict(os.environ, {"BASH_ENV": "/tmp/untrusted", "PYTHONPATH": "/tmp/untrusted", "GH_TOKEN": "synthetic"}), \
             patch.object(os, "geteuid", return_value=0, create=True), patch("ssh_entry.os.umask"), \
             patch("ssh_entry.tempfile.mkstemp", return_value=(42, "/opt/racebib/state/ssh-deployment-test.log")), \
             patch("ssh_entry.os.fdopen", unittest.mock.mock_open()), patch("ssh_entry.subprocess.run") as run, \
             patch("ssh_entry.inspect_state", return_value=receipt) as state:
            run.return_value.returncode = 0
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                ssh_entry.deploy(SHA, "")
            self.assertIn("RACEBIB_RESULT ", output.getvalue())
            self.assertEqual(run.call_args.args[0], ["/bin/bash", "/opt/racebib/scripts/deploy.sh", SHA])
            self.assertNotIn("BASH_ENV", run.call_args.kwargs["env"])
            self.assertNotIn("PYTHONPATH", run.call_args.kwargs["env"])
            self.assertNotIn("GH_TOKEN", run.call_args.kwargs["env"])
            self.assertEqual(run.call_args.kwargs["env"]["PATH"], "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin")
            for stage in ["engine", "state", "selector"]:
                run.return_value.returncode = 1 if stage == "engine" else 0
                state.side_effect = RuntimeError("barrier") if stage == "state" else None
                state.return_value = dict(receipt, release_tag="wrong") if stage == "selector" else receipt
                output = io.StringIO()
                with contextlib.redirect_stdout(output), self.assertRaises(RuntimeError):
                    ssh_entry.deploy(SHA, "")
                self.assertNotIn("RACEBIB_RESULT ", output.getvalue())

    def test_command_line_cannot_request_migrations(self):
        for script in ["local_verify.py", "ssh_entry.py"]:
            result = subprocess.run([sys.executable, str(ROOT / "deploy/scripts" / script), SHA, "--migrate"], capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(b"unrecognized arguments", result.stderr)

    @unittest.skipUnless(os.name == "posix", "Host engine requires POSIX")
    def test_isolated_engine_session_survives_hangup_without_network_or_production(self):
        program = "import os,signal; os.kill(os.getpid(),signal.SIGHUP); print(os.getsid(0)==os.getpid(), flush=True)"
        with tempfile.TemporaryFile(mode="w+") as log:
            result = ssh_entry.run_engine([sys.executable, "-c", program], log, os.environ.copy())
            log.seek(0)
            self.assertEqual(result.returncode, 0)
            self.assertEqual(log.read().strip(), "True")

    def test_ssm_workflow_is_archived_without_runner_or_controller_dispatch(self):
        workflow = (ROOT / ".github/workflows/deploy-production.yml").read_text()
        self.assertIn("INACTIVE-SSM", workflow)
        self.assertIn("github.event_name == 'disabled_ssm_transport'", workflow)
        self.assertIn("ssm_deploy.py", workflow)
        self.assertNotIn("self-hosted", workflow)
        self.assertNotIn("Invoke-RaceBibDeployment", workflow)


if __name__ == "__main__":
    unittest.main()
