import contextlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import subprocess
import textwrap
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import ssm_deploy
import ssm_entry

ROOT = Path(__file__).resolve().parents[2]
SHA = "a" * 40


class SsmTests(unittest.TestCase):
    def test_cli_disables_implicit_send_retries_and_never_prints_raw_errors(self):
        with patch("ssm_deploy.subprocess.run") as run:
            run.return_value.returncode = 0
            run.return_value.stdout = '{"Command": {"CommandId": "id"}}'
            ssm_deploy.aws("send-command", "--parameters", "{}")
            self.assertEqual(run.call_args.kwargs["env"]["AWS_MAX_ATTEMPTS"], "1")
            run.return_value.returncode = 1
            run.return_value.stderr = "sensitive error details"
            with self.assertRaises(RuntimeError) as error:
                ssm_deploy.aws("send-command")
            self.assertNotIn("sensitive", str(error.exception))

    def test_rejects_shell_options_short_sha_and_mismatched_recovery(self):
        for source, tag in [("main", ""), (SHA + ";id", ""), (SHA, "--migrate"),
                            (SHA, "racebib-" + "b" * 40 + "-recovery-1-1")]:
            with self.subTest(source=source, tag=tag), self.assertRaises(ValueError):
                ssm_deploy.parameters(source, tag)
        tag = "racebib-" + SHA + "-recovery-123-1"
        self.assertEqual(ssm_deploy.parameters(SHA, tag)["ReleaseTag"], [tag])

    def test_poll_handles_eventual_consistency_and_requires_success(self):
        replies = iter([None, {"Status": "InProgress"}, {"Status": "Success", "ResponseCode": 0}])
        ssm_deploy.wait("id", "instance", client=lambda *a: next(replies), sleep=lambda n: None)
        for status in ["Failed", "TimedOut", "Cancelled", "DeliveryTimedOut", "Undeliverable"]:
            with self.subTest(status=status), self.assertRaises(RuntimeError):
                ssm_deploy.wait("id", "instance", client=lambda *a: {"Status": status})
        with self.assertRaises(RuntimeError):
            ssm_deploy.wait("id", "instance", client=lambda *a: {"Status": "Success", "ResponseCode": 1})

    def test_timeout_does_not_resubmit_or_cancel(self):
        ticks = iter([0, 4000])
        calls = []
        with self.assertRaisesRegex(RuntimeError, "outcome unknown"):
            ssm_deploy.wait("id", "instance", clock=lambda: next(ticks), client=lambda *a: calls.append(a))
        self.assertEqual(calls, [])

    def test_state_projection_rejects_wrong_target_and_barrier_without_leaking(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "state").mkdir()
            state = {"current": {"manifest": {"source_sha": SHA}}, "inflight": None, "secret": "sensitive"}
            for inflight, target, succeeds in [(None, SHA, True), (None, "b" * 40, False),
                                                ({"stage": "backed-up"}, SHA, False)]:
                state["inflight"] = inflight
                (root / "state/release-state.json").write_text(json.dumps(state))
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    if succeeds:
                        ssm_entry.inspect_state(target, root)
                    else:
                        with self.assertRaises(RuntimeError):
                            ssm_entry.inspect_state(target, root)
                self.assertNotIn("sensitive", output.getvalue())

    def test_host_entry_uses_existing_deploy_without_migrations_and_checks_failure_state(self):
        env = {"SSM_SourceSha": SHA, "SSM_ReleaseTag": ""}
        with patch.dict(os.environ, env, clear=True), patch.object(os, "geteuid", return_value=0, create=True), \
             patch("ssm_entry.os.umask"), patch("ssm_entry.tempfile.mkstemp", return_value=(42, "/opt/racebib/state/ssm-deployment-test.log")), \
             patch("ssm_entry.os.fdopen", unittest.mock.mock_open()), \
             patch("ssm_entry.subprocess.run") as run, patch("ssm_entry.inspect_state") as inspect:
            run.return_value.returncode = 0
            ssm_entry.main()
            self.assertEqual(run.call_args.args[0], ["/bin/bash", "/opt/racebib/scripts/deploy.sh", SHA])
            inspect.assert_called_once_with(SHA)
            run.return_value.returncode = 1
            with self.assertRaises(RuntimeError):
                ssm_entry.main()
            inspect.assert_called_with(None)

    def test_document_has_only_fixed_command_and_validated_environment_parameters(self):
        doc = json.loads((ROOT / "deploy/aws/deploy-document.json").read_text())
        self.assertEqual(set(doc["parameters"]), {"SourceSha", "ReleaseTag"})
        for param in doc["parameters"].values():
            self.assertEqual(param["interpolationType"], "ENV_VAR")
            self.assertIn("allowedPattern", param)
        self.assertEqual(doc["mainSteps"][0]["inputs"]["runCommand"],
                         ["exec /usr/bin/python3 -I -B /opt/racebib/scripts/ssm_bootstrap.py"])

    def test_workflow_dispatch_gate_permissions_and_transport(self):
        workflow = (ROOT / ".github/workflows/deploy-production.yml").read_text()
        self.assertIn("workflow_dispatch:", workflow)
        self.assertNotIn("  push:", workflow)
        self.assertNotIn("  workflow_run:", workflow)
        self.assertIn("environment: production", workflow)
        self.assertIn("github.event_name == 'disabled_ssm_transport'", workflow)
        self.assertIn("id-token: write", workflow)
        self.assertIn("cancel-in-progress: false", workflow)
        for forbidden in ["--migrate", "DEPLOY_SSH_KEY", "StrictHostKeyChecking=no", "curl -k"]:
            self.assertNotIn(forbidden, workflow)
        self.assertLess(workflow.index("release.py verify"), workflow.index("configure-aws-credentials@"))
        self.assertIn("--proto '=https'", workflow)
        self.assertIn("if: inputs.verify_external_https", workflow)
        self.assertIn("verify_external_https:", workflow)
        self.assertIn("default: false", workflow.split("verify_external_https:")[1].split("permissions:")[0])
        self.assertIn("steps.remote.outputs.remote_command", workflow)
        self.assertIn("steps.remote.outputs.host_state", workflow)
        self.assertIn("steps.external.outcome", workflow)
        self.assertNotIn("continue-on-error", workflow)
        for forbidden in ["authorize-security-group", "revoke-security-group", "recover.sh", "rollback.sh"]:
            self.assertNotIn(forbidden, workflow)

    def test_receipt_requires_exact_target_and_single_verified_result(self):
        receipt = {"deployment": "succeeded", "host_state": "verified", "source_sha": SHA}
        line = "RACEBIB_RESULT " + json.dumps(receipt)
        ssm_deploy.verify_receipt("non-secret projection\n" + line, SHA)
        for stdout in ["", line + "\n" + line, line.replace(SHA, "b" * 40),
                       line.replace('"verified"', '"unconfirmed"'), "RACEBIB_RESULT invalid"]:
            with self.subTest(stdout=stdout), self.assertRaises((RuntimeError, ValueError)):
                ssm_deploy.verify_receipt(stdout, SHA)

    def test_submission_pins_version_hash_and_emits_independent_results(self):
        env = {"SOURCE": SHA, "INSTANCE_ID": "i-" + "a" * 17,
               "DOCUMENT_NAME": "RaceBib-DeployVerifiedRelease", "DOCUMENT_SHA256": "b" * 64}
        receipt = 'RACEBIB_RESULT ' + json.dumps({"deployment": "succeeded", "host_state": "verified", "source_sha": SHA})
        with patch.dict(os.environ, env, clear=True), patch("ssm_deploy.aws") as aws, \
             patch("ssm_deploy.wait", return_value={"StandardOutputContent": receipt}) as wait, \
             patch("ssm_deploy.output") as output:
            aws.return_value = {"Command": {"CommandId": "command"}}
            ssm_deploy.main()
            args = aws.call_args.args
            self.assertEqual(args[args.index("--document-version") + 1], "1")
            self.assertEqual(args[args.index("--document-hash") + 1], "b" * 64)
            self.assertEqual(args[args.index("--document-hash-type") + 1], "Sha256")
            aws.assert_called_once()
            output.assert_any_call("remote_command", "succeeded")
            output.assert_any_call("host_state", "verified")
            output.reset_mock()
            wait.return_value = {"StandardOutputContent": "missing receipt"}
            with self.assertRaises(RuntimeError):
                ssm_deploy.main()
            output.assert_called_once_with("remote_command", "succeeded")

    def test_missing_or_invalid_document_hash_prevents_submission(self):
        env = {"SOURCE": SHA, "INSTANCE_ID": "i-" + "a" * 17, "DOCUMENT_NAME": "RaceBib-DeployVerifiedRelease"}
        for value in ["", "$LATEST", "c" * 63, "A" * 64]:
            with self.subTest(value=value), patch.dict(os.environ, dict(env, DOCUMENT_SHA256=value), clear=True), \
                 patch("ssm_deploy.aws") as aws, self.assertRaises(ValueError):
                ssm_deploy.main()
            aws.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "Health shell runs on the Linux runner")
    def test_external_probe_checks_tls_http_and_exit_status_once(self):
        workflow = (ROOT / ".github/workflows/deploy-production.yml").read_text()
        step = workflow.split("      - name: Verify external HTTPS", 1)[1].split("      - name: Report outcome", 1)[0]
        script = textwrap.dedent(step.split("        run: |\n", 1)[1])
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            curl = root / "curl"
            curl.write_text('#!/bin/bash\necho call >> "$CALLS"\nprintf "%s" "$HTTP_CODE"\nexit "$CURL_EXIT"\n')
            curl.chmod(0o755)
            for code, exit_code, succeeds in [("200", "0", True), ("301", "0", False),
                                               ("000", "60", False), ("200", "18", False)]:
                calls = root / "calls"
                calls.write_text("")
                env = dict(os.environ, PATH=temp + os.pathsep + os.environ["PATH"],
                           CALLS=str(calls), HTTP_CODE=code, CURL_EXIT=exit_code)
                result = subprocess.run(["bash", "-c", script], env=env, capture_output=True)
                self.assertEqual(result.returncode == 0, succeeds)
                self.assertEqual(calls.read_text().splitlines(), ["call"])

    def test_iam_only_allows_fixed_document_and_instance(self):
        policy = json.loads((ROOT / "deploy/aws/github-permissions.json").read_text())
        send, read = policy["Statement"]
        self.assertEqual(send["Action"], "ssm:SendCommand")
        self.assertEqual(len(send["Resource"]), 2)
        self.assertTrue(all("*" not in resource for resource in send["Resource"]))
        self.assertEqual(read["Action"], "ssm:GetCommandInvocation")
        trust = json.loads((ROOT / "deploy/aws/github-trust.json").read_text())
        condition = trust["Statement"][0]["Condition"]["StringEquals"]
        self.assertEqual(condition["token.actions.githubusercontent.com:sub"],
                         "repo:vreboton/racebib:environment:production")


if __name__ == "__main__":
    unittest.main()
