import copy
import json
import os
import shutil
import subprocess
from pathlib import Path
import sys
import tempfile
import unittest
import urllib.request
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import release as subject
from release_state import ReleaseState


class Evidence:
    def __init__(self):
        self.source = "a" * 40
        self.tag = "racebib-" + self.source
        self.recovery_tag = subject.recovery_tag(self.source, 30, 2)
        self.manifest = {"format": 1, "repository": subject.REPO, "source_sha": self.source,
                         "api": "ghcr.io/vreboton/racebib-api@sha256:" + "c" * 64,
                         "web": "ghcr.io/vreboton/racebib-web@sha256:" + "d" * 64,
                         "build": {"run_id": 10, "attempt": 1}, "safety": {"run_id": 15, "attempt": 1},
                         "publication": {"run_id": 20, "attempt": 1}}
        self.raw = subject.canonical(self.manifest) + b"\n"
        self.receipt = {"format": 1, "repository": subject.REPO, "source_sha": self.source,
                        "release_tag": self.recovery_tag, "manifest_digest": subject.sha256(self.raw),
                        "source_release_tag": self.tag, "source_release_id": 101,
                        "recovery": {"run_id": 30, "attempt": 2}}
        self.new_visible = True
        self.orphan_tag = False
        self.recovery_status = "completed"
        self.recovery_conclusion = "success"
        self.publication_conclusion = "failure"
        self.release_conclusion = "failure"
        self.skipped_image = False
        self.original_draft = False
        self.original_assets = True
        self.new_immutable = True
        self.wrong_ref = False
        self.wrong_log = False
        self.wrong_image_output = False

    def asset(self, identifier, name, raw):
        return {"id": identifier, "name": name, "state": "uploaded", "size": len(raw), "digest": subject.sha256(raw)}

    def receipt_bytes(self):
        return subject.canonical(self.receipt) + b"\n"

    def request(self, path):
        if path == "/releases/tags/" + self.tag:
            return {"id": 101, "tag_name": self.tag, "immutable": False, "draft": self.original_draft,
                    "assets": [self.asset(1, "release.json", self.raw)] if self.original_assets else []}
        if path == "/releases/tags/" + self.recovery_tag:
            if not self.new_visible:
                return None
            return {"id": 202, "tag_name": self.recovery_tag, "immutable": self.new_immutable, "draft": False,
                    "assets": [self.asset(1, "release.json", self.raw), self.asset(2, "recovery.json", self.receipt_bytes())]}
        if path.startswith("/git/ref/tags/"):
            if path.endswith(self.recovery_tag) and not self.new_visible and not self.orphan_tag:
                return None
            return {"object": {"type": "commit", "sha": "b" * 40 if self.wrong_ref else self.source}}
        if path.startswith("/compare/"):
            return {"status": "ahead"}
        if path.startswith("/actions/workflows/"):
            return {"id": {"build.yml": 1, "deployment-safety.yml": 2, "publish-images.yml": 3,
                           "recover-publication.yml": 4}[path.rsplit("/", 1)[-1]]}
        if path.startswith("/actions/runs/"):
            run = int(path.split("/")[3])
            if "/jobs?" in path:
                if run == 20:
                    return {"jobs": [{"id": identifier, "name": name, "status": "completed",
                                      "conclusion": self.release_conclusion if name == "release" else
                                      "skipped" if name == "publish-web" and self.skipped_image else "success"}
                                     for identifier, name in [(21, "preflight"), (22, "publish-api"), (23, "publish-web"), (24, "release")]]}
                required = {10: subject.BUILD_JOBS, 15: subject.SAFETY_JOBS, 30: {"recover"}}[run]
                return {"jobs": [{"id": run * 100 + i, "name": name, "status": "completed", "conclusion": "success"}
                                 for i, name in enumerate(required)]}
            return {"workflow_id": {10: 1, 15: 2, 20: 3, 30: 4}[run], "status": self.recovery_status if run == 30 else "completed",
                    "conclusion": self.publication_conclusion if run == 20 else self.recovery_conclusion if run == 30 else "success",
                    "event": "workflow_run" if run == 20 else "workflow_dispatch" if run == 30 else "push", "head_branch": "main",
                    "head_sha": "b" * 40 if run == 30 else self.source,
                    "display_title": ("recover publication " if run == 30 else "publish ") + self.source,
                    "repository": {"full_name": subject.REPO}, "head_repository": {"full_name": subject.REPO}}
        raise AssertionError("Unexpected API route: " + path)

    def read_url(self, url, headers=None, limit=None):
        if "/releases/assets/1" in url:
            return self.raw, {}
        if "/releases/assets/2" in url:
            return self.receipt_bytes(), {}
        if "/actions/jobs/" in url:
            job = int(url.split("/")[-2])
            if job in (22, 23):
                digest = self.manifest["api" if job == 22 else "web"].split("@", 1)[1]
                if self.wrong_image_output:
                    digest = "sha256:" + "f" * 64
                return ("2026-11-17T01:02:03Z ##[group]Digest\n2026-11-17T01:02:03Z " + digest +
                        "\n2026-11-17T01:02:03Z ##[endgroup]\n").encode(), {}
            assert job == 24
            values = {"SOURCE": self.source, "BUILD_RUN_ID": "999" if self.wrong_log else "10", "BUILD_ATTEMPT": "1",
                      "SAFETY_RUN_ID": "15", "SAFETY_ATTEMPT": "1", "API_DIGEST": self.manifest["api"].split("@", 1)[1],
                      "WEB_DIGEST": self.manifest["web"].split("@", 1)[1]}
            return "\n".join("2026-11-17T01:02:03Z   " + k + ": " + v for k, v in values.items()).encode(), {}
        raise AssertionError("Unexpected read URL: " + url)


class PublicationRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.evidence = Evidence()
        self.addCleanup(patch.stopall)
        patch("release.request", side_effect=self.evidence.request).start()
        patch("release.read_url", side_effect=self.evidence.read_url).start()
        patch("release.auth", return_value={"github_token": "test-only"}).start()
        self.registry = patch("release.verify_registry_images", return_value={"api": "verified", "web": "verified"}).start()

    def test_mutable_original_is_retained_but_never_accepted_for_deployment(self):
        with self.assertRaises(subject.SafetyError):
            subject.verify_release(self.evidence.source)
        with self.assertRaises(subject.SafetyError):
            subject.preflight(self.evidence.source, 10, 1)
        plan, raw = subject.recovery_plan(self.evidence.source)
        self.assertEqual(raw, self.evidence.raw)
        self.assertEqual(plan["manifest"], self.evidence.manifest)
        self.assertEqual(plan["manifest_digest"], subject.sha256(raw))
        self.registry.assert_called_once_with(self.evidence.manifest)

    def test_explicit_immutable_recovery_preserves_original_manifest_identity(self):
        verified = subject.verify_release(self.evidence.source, self.evidence.recovery_tag)
        self.assertEqual(verified["manifest"], self.evidence.manifest)
        self.assertEqual(verified["manifest_digest"], subject.sha256(self.evidence.raw))
        self.assertEqual(verified["release_tag"], self.evidence.recovery_tag)

    def test_recovery_metadata_preserves_previous_distinct_release_on_redeployment(self):
        verified = subject.verify_release(self.evidence.source, self.evidence.recovery_tag)
        prior = copy.deepcopy(verified)
        prior["manifest"]["source_sha"] = "b" * 40
        prior["manifest_digest"] = "sha256:" + "e" * 64
        prior.pop("release_tag")
        with tempfile.TemporaryDirectory() as root:
            state = ReleaseState(Path(root) / "state.json")
            for release in (prior, verified, verified):
                state.begin("deployment", release)
                state.stage("application-verified")
                state.commit(release, "f" * 64)
            restored = ReleaseState(Path(root) / "state.json")
            self.assertEqual(restored.value["current"], verified)
            self.assertEqual(restored.rollback_target(), prior)
            self.assertEqual(restored.value["current"]["release_tag"], self.evidence.recovery_tag)

    def test_recovery_does_not_accept_a_failed_or_in_progress_recovery_run(self):
        for status, conclusion in [("completed", "failure"), ("in_progress", None), ("completed", "cancelled")]:
            with self.subTest(status=status, conclusion=conclusion):
                self.evidence.recovery_status, self.evidence.recovery_conclusion = status, conclusion
                with self.assertRaises(subject.SafetyError):
                    subject.verify_release(self.evidence.source, self.evidence.recovery_tag)

    def test_recovery_rejects_skipped_image_or_mismatched_provenance_and_digests(self):
        for attribute in ("skipped_image", "wrong_log", "wrong_image_output", "wrong_ref"):
            with self.subTest(attribute=attribute):
                setattr(self.evidence, attribute, True)
                with self.assertRaises(subject.SafetyError):
                    subject.recovery_plan(self.evidence.source)
                setattr(self.evidence, attribute, False)

    def test_tag_and_receipt_mismatches_fail_closed(self):
        with self.assertRaises(subject.SafetyError):
            subject.verify_release(self.evidence.source, "racebib-" + "b" * 40 + "-recovery-30-2")
        for name, bad in [("manifest_digest", "sha256:" + "0" * 64), ("source_sha", "b" * 40),
                          ("source_release_tag", "racebib-" + "b" * 40), ("release_tag", self.evidence.tag)]:
            original = self.evidence.receipt[name]
            self.evidence.receipt[name] = bad
            with self.subTest(name=name), self.assertRaises(subject.SafetyError):
                subject.verify_release(self.evidence.source, self.evidence.recovery_tag)
            self.evidence.receipt[name] = original
        self.evidence.receipt["recovery"]["attempt"] = 3
        with self.assertRaises(subject.SafetyError):
            subject.verify_release(self.evidence.source, self.evidence.recovery_tag)

    def test_interrupted_original_without_manifest_is_not_reconstructed_by_guessing(self):
        self.evidence.original_assets = False
        with self.assertRaises(subject.SafetyError):
            subject.recovery_plan(self.evidence.source)
        self.registry.assert_not_called()

    def test_interrupted_draft_with_complete_verified_manifest_can_be_copied(self):
        self.evidence.original_draft = True
        self.evidence.publication_conclusion = "cancelled"
        self.evidence.release_conclusion = "cancelled"
        plan, raw = subject.recovery_plan(self.evidence.source)
        self.assertEqual(plan["source_release_id"], 101)
        self.assertEqual(raw, self.evidence.raw)

    def test_interrupted_recovery_never_reuses_or_overwrites_a_target(self):
        self.evidence.new_visible = False
        self.evidence.orphan_tag = True
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ, GITHUB_RUN_ID="30", GITHUB_RUN_ATTEMPT="2"):
            destination = Path(root) / "new"
            with self.assertRaises(subject.SafetyError):
                subject.prepare_recovery(self.evidence.source, destination)
            self.assertFalse(destination.exists())
        self.assertNotEqual(subject.recovery_tag(self.evidence.source, 30, 2), subject.recovery_tag(self.evidence.source, 30, 3))

    def test_prepare_creates_only_new_local_evidence_and_keeps_exact_bytes(self):
        self.evidence.new_visible = False
        self.evidence.recovery_status = "in_progress"
        with tempfile.TemporaryDirectory() as root, patch.dict(os.environ, GITHUB_RUN_ID="30", GITHUB_RUN_ATTEMPT="2"):
            destination = Path(root) / "new"
            tag = subject.prepare_recovery(self.evidence.source, destination)
            self.assertEqual(tag, self.evidence.recovery_tag)
            self.assertEqual((destination / "release.json").read_bytes(), self.evidence.raw)
            receipt = json.loads((destination / "recovery.json").read_bytes())
            self.assertEqual(receipt["manifest_digest"], subject.sha256(self.evidence.raw))
            self.assertEqual(receipt["source_release_id"], 101)

    def test_post_publish_check_requires_immutability_and_exact_assets(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            (path / "release.json").write_bytes(self.evidence.raw)
            (path / "recovery.json").write_bytes(self.evidence.receipt_bytes())
            subject.check_recovery_created(self.evidence.source, self.evidence.recovery_tag, root)
            self.evidence.new_immutable = False
            with self.assertRaises(subject.SafetyError):
                subject.check_recovery_created(self.evidence.source, self.evidence.recovery_tag, root)
            self.evidence.new_immutable = True
            (path / "release.json").write_bytes(b"wrong")
            with self.assertRaises(subject.SafetyError):
                subject.check_recovery_created(self.evidence.source, self.evidence.recovery_tag, root)


class RegistryAndAuthenticationTests(unittest.TestCase):
    def test_private_assets_use_authenticated_api_and_redirects_drop_credentials(self):
        raw = b"{}"
        release = {"tag_name": "unused", "assets": [{"id": 123, "name": "release.json", "state": "uploaded",
                                                     "size": len(raw), "digest": subject.sha256(raw)}]}
        with patch("release.auth", return_value={"github_token": "test-only"}), patch("release.read_url", return_value=(raw, {})) as read:
            self.assertEqual(subject.read_asset(release, "release.json"), raw)
            self.assertTrue(read.call_args.args[0].startswith(subject.API + "/releases/assets/123"))
            self.assertEqual(read.call_args.args[1]["Authorization"], "Bearer test-only")
        request = urllib.request.Request(subject.API + "/releases/assets/123", headers={"Authorization": "Bearer test-only"})
        redirect = subject.StripCredentialsOnRedirect()
        result = redirect.redirect_request(request, None, 302, "", {}, "https://release-assets.githubusercontent.com/file")
        self.assertIsNone(result.get_header("Authorization"))
        with self.assertRaises(subject.SafetyError):
            redirect.redirect_request(request, None, 302, "", {}, "http://example.test/file")

    def test_asset_checksum_and_size_mismatch_are_rejected(self):
        asset = {"id": 1, "name": "release.json", "state": "uploaded", "size": 2, "digest": subject.sha256(b"{}")}
        with patch("release.auth", return_value={"github_token": "test-only"}), patch("release.read_url", return_value=(b"[]", {})):
            with self.assertRaises(subject.SafetyError):
                subject.read_asset({"assets": [asset]}, "release.json")

    def test_registry_checks_digest_tag_platform_and_source_without_writes(self):
        source = "a" * 40
        config = {"architecture": "amd64", "os": "linux", "config": {"Labels": {
            "org.opencontainers.image.revision": source, "org.opencontainers.image.source": "https://github.com/" + subject.REPO}}}
        config_raw = subject.canonical(config)
        image_raw = subject.canonical({"mediaType": "application/vnd.oci.image.manifest.v1+json", "config": {"digest": subject.sha256(config_raw)}, "layers": []})
        index_raw = subject.canonical({"mediaType": "application/vnd.oci.image.index.v1+json", "manifests": [
            {"digest": subject.sha256(image_raw), "platform": {"os": "linux", "architecture": "amd64"}}]})
        manifest = {"source_sha": source, "publication": {"run_id": 20, "attempt": 1},
                    "api": "ghcr.io/vreboton/racebib-api@" + subject.sha256(index_raw),
                    "web": "ghcr.io/vreboton/racebib-web@" + subject.sha256(index_raw)}
        corrupt = None
        calls = []
        def read(url, headers=None, limit=None):
            calls.append(url)
            if "/token?" in url:
                return b'{"token":"test-only"}', {}
            if "/blobs/" in url:
                return config_raw, {}
            reference = url.rsplit("/", 1)[-1]
            raw = image_raw if reference == subject.sha256(image_raw) else index_raw
            if corrupt == "digest" and reference == subject.sha256(index_raw):
                raw += b" "
            if corrupt == "tag" and reference == source + "-20-1":
                raw = image_raw
            return raw, {"docker-content-digest": subject.sha256(raw)}
        with patch("release.auth", return_value={"ghcr_token": None, "ghcr_username": None}), patch("release.read_url", side_effect=read):
            evidence = subject.verify_registry_images(manifest)
            self.assertEqual(set(evidence), {"api", "web"})
            for failure in ("digest", "tag"):
                corrupt = failure
                with self.subTest(failure=failure), self.assertRaises(subject.SafetyError):
                    subject.verify_registry_images(manifest)
            corrupt = None
            # Even internally consistent alternate image bytes cannot claim another source SHA.
            wrong = copy.deepcopy(manifest)
            wrong["source_sha"] = "b" * 40
            # Supply the original index for its generated tag so the source-label gate is exercised.
            with self.assertRaises(subject.SafetyError):
                subject.verify_registry_images(wrong)
        self.assertTrue(all(url.startswith("https://ghcr.io/") for url in calls))


class ForcedCommandTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "posix" and shutil.which("bash"), "Requires POSIX Bash")
    def test_recovery_selection_is_passed_as_arguments_and_injection_is_rejected(self):
        source = "a" * 40
        tag = subject.recovery_tag(source, 30, 2)
        dispatcher = Path(__file__).resolve().parents[1] / "scripts" / "ssh-dispatch.sh"
        with tempfile.TemporaryDirectory() as root:
            sudo = Path(root) / "sudo"
            sudo.write_text('#!/bin/sh\nprintf "%s\\n" "$@"\n')
            sudo.chmod(0o700)
            env = dict(os.environ, PATH=root + os.pathsep + os.environ["PATH"])
            for migrate in ("", " --migrate"):
                env["SSH_ORIGINAL_COMMAND"] = f"deploy {source}{migrate} --release-tag {tag}"
                result = subprocess.run(["bash", str(dispatcher)], env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout.splitlines(), ["-n", "/usr/local/sbin/racebib-deploy", source] +
                                 (["--migrate"] if migrate else []) + ["--release-tag", tag])
            for command in (f"deploy {source} --release-tag racebib-{'b' * 40}-recovery-30-2",
                            f"deploy {source} --release-tag {tag}; id", f"deploy {source} --release-tag latest"):
                env["SSH_ORIGINAL_COMMAND"] = command
                result = subprocess.run(["bash", str(dispatcher)], env=env, capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")


if __name__ == "__main__":
    unittest.main()
