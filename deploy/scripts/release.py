"""Release provenance shared by CI and the server. Standard library only; never execute a manifest."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

REPO = "vreboton/racebib"
API = "https://api.github.com/repos/" + REPO
SHA = re.compile(r"[0-9a-f]{40}")
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
BUILD_JOBS = {"backend", "backend-mysql-integration", "frontend"}
PUBLISH_JOBS = {"publish-api", "publish-web", "release"}
SAFETY_JOBS = {"deployment-safety"}
IMAGE_JOBS = {"preflight", "publish-api", "publish-web"}
RECOVERY_TAG = re.compile(r"racebib-([0-9a-f]{40})-recovery-([1-9][0-9]*)-([1-9][0-9]*)")


class StripCredentialsOnRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        require(urllib.parse.urlparse(newurl).scheme == "https", "Refuse non-HTTPS redirect")
        redirected = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirected is not None:
            redirected.remove_header("Authorization")
        return redirected


def auth():
    # Optional host-only file for forced-command SSH deployments of a private repository.
    # Never load credentials from the checkout or execute a configuration file.
    value = {}
    path = Path("/opt/racebib/github-auth.json")
    if os.name == "posix" and path.exists():
        info = path.stat()
        require(path.is_file() and not path.is_symlink() and info.st_uid == 0 and
                stat.S_IMODE(info.st_mode) == 0o600, "GitHub credential file must be root-owned mode 600")
        value = json.loads(path.read_text())
        require(isinstance(value, dict) and all(isinstance(item, str) for item in value.values()), "Invalid GitHub credential file")
    return {"github_token": os.environ.get("GH_TOKEN") or value.get("github_token"),
            "ghcr_token": os.environ.get("GHCR_TOKEN") or value.get("ghcr_token") or os.environ.get("GH_TOKEN") or value.get("github_token"),
            "ghcr_username": os.environ.get("GHCR_USERNAME") or os.environ.get("GITHUB_ACTOR") or value.get("ghcr_username")}


def read_url(url, headers=None, limit=1024 * 1024):
    require(urllib.parse.urlparse(url).scheme == "https", "HTTPS is required")
    req = urllib.request.Request(url, headers=headers or {})
    try:
        with urllib.request.build_opener(StripCredentialsOnRedirect()).open(req, timeout=30) as response:
            raw = response.read(limit + 1)
            metadata = {key.lower(): value for key, value in response.headers.items()}
        require(len(raw) <= limit, "Response exceeds size limit")
        return raw, metadata
    except urllib.error.HTTPError as error:
        raise SafetyError("Read-only request failed (HTTP %s); no release accepted" % error.code) from None
    except OSError:
        raise SafetyError("Read-only request unavailable; no release accepted") from None


class SafetyError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise SafetyError(message)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def sha256(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def request(path, *, method="GET", data=None, binary=False):
    # Tokens are sent to the GitHub API only, never to a redirected asset host.
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "racebib-release-verifier",
               "X-GitHub-Api-Version": "2022-11-28"}
    token = auth()["github_token"]
    if token:
        headers["Authorization"] = "Bearer " + token
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(API + path, data=None if data is None else canonical(data), headers=headers, method=method)
    try:
        with urllib.request.build_opener(StripCredentialsOnRedirect()).open(req, timeout=30) as response:
            raw = response.read(1024 * 1024 + 1)
        require(len(raw) <= 1024 * 1024, "GitHub response exceeds size limit")
        return raw if binary else json.loads(raw)
    except urllib.error.HTTPError as error:
        if error.code == 404 and method == "GET":
            return None
        raise SafetyError("GitHub API request failed (HTTP %s); no state was accepted" % error.code) from None
    except (OSError, ValueError):
        raise SafetyError("GitHub API unavailable or invalid; no state was accepted") from None


def require_jobs(jobs, required):
    for name in required:
        matches = [job for job in jobs if job.get("name") == name]
        require(len(matches) == 1 and matches[0].get("status") == "completed" and
                matches[0].get("conclusion") == "success", "Required job did not execute successfully: " + name)


def verify_run(run_id, attempt, workflow, event, source, required, *, publishing=False,
               recovery=False, conclusions=("success",)):
    require(type(run_id) is int and run_id > 0 and type(attempt) is int and attempt > 0, "Invalid run identity")
    definition = request("/actions/workflows/" + workflow)
    run = request(f"/actions/runs/{run_id}/attempts/{attempt}")
    require(definition and run and run.get("workflow_id") == definition.get("id"), "Workflow identity mismatch")
    require(run.get("event") == event and run.get("status") == "completed" and
            run.get("conclusion") in conclusions, "Workflow attempt was not successful")
    require(run.get("repository", {}).get("full_name") == REPO and
            run.get("head_repository", {}).get("full_name") == REPO and run.get("head_branch") == "main",
            "Untrusted repository or branch")
    if recovery:
        require(run.get("display_title") == "recover publication " + source, "Recovery source identity mismatch")
        require(SHA.fullmatch(run.get("head_sha", "")), "Invalid recovery workflow revision")
        comparison = request(f"/compare/{run['head_sha']}...main")
        require(comparison and comparison.get("status") in ("ahead", "identical"), "Untrusted recovery code revision")
    elif not publishing:
        require(run.get("head_sha") == source, "Build source SHA mismatch")
    else:
        # workflow_run.head_sha is the default-branch revision, not necessarily the built SHA.
        require(run.get("display_title") == "publish " + source, "Publication source identity mismatch")
    jobs = []
    for page in range(1, 11):
        result = request(f"/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100&page={page}")
        require(isinstance(result, dict) and isinstance(result.get("jobs"), list), "Missing job records")
        batch = result["jobs"]
        jobs.extend(batch)
        if len(batch) < 100:
            break
    else:
        raise SafetyError("Too many job records")
    require_jobs(jobs, required)
    return jobs


def validate_manifest(manifest, source):
    require(isinstance(manifest, dict) and type(manifest.get("format")) is int and manifest["format"] == 1 and manifest.get("repository") == REPO,
            "Unsupported manifest")
    require(SHA.fullmatch(source) is not None and manifest.get("source_sha") == source, "Manifest source mismatch")
    for name in ("api", "web"):
        image = manifest.get(name)
        prefix = f"ghcr.io/{REPO}-{name}@" # explicit packages are racebib-api / racebib-web
        require(isinstance(image, str) and image.startswith(prefix) and DIGEST.fullmatch(image[len(prefix):]) is not None,
                "Invalid pinned " + name + " image")
    for field in ("build", "publication", "safety"):
        identity = manifest.get(field)
        require(isinstance(identity, dict) and type(identity.get("run_id")) is int and identity["run_id"] > 0 and
                type(identity.get("attempt")) is int and identity["attempt"] > 0, "Invalid " + field + " identity")
    return manifest


def read_asset(release, name):
    assets = [asset for asset in release.get("assets", []) if asset.get("name") == name]
    require(len(assets) == 1 and assets[0].get("state") == "uploaded" and
            DIGEST.fullmatch(assets[0].get("digest") or "") is not None and
            type(assets[0].get("id")) is int and 0 < assets[0].get("size", 0) <= 16384,
            "Missing, duplicate or unhashed release asset: " + name)
    asset = assets[0]
    if auth()["github_token"]:
        headers = {"Accept": "application/octet-stream", "User-Agent": "racebib-release-verifier",
                   "Authorization": "Bearer " + auth()["github_token"]}
        raw, _ = read_url(API + f"/releases/assets/{asset['id']}", headers, limit=16384)
    else:
        url = f"https://github.com/{REPO}/releases/download/{release['tag_name']}/{name}"
        raw, _ = read_url(url, {"User-Agent": "racebib-release-verifier"}, limit=16384)
    require(len(raw) == asset["size"] and sha256(raw) == asset["digest"], "Release asset digest/size mismatch")
    return raw


def verify_source_tag(source, tag):
    require(tag == "racebib-" + source or
            (RECOVERY_TAG.fullmatch(tag) and RECOVERY_TAG.fullmatch(tag)[1] == source), "Release tag/source mismatch")
    ref = request("/git/ref/tags/" + tag)
    require(ref and ref.get("object", {}).get("type") == "commit" and ref["object"]["sha"] == source,
            "Release tag is not the expected lightweight source tag")
    comparison = request(f"/compare/{source}...main")
    require(comparison and comparison.get("status") in ("ahead", "identical"), "Source is not an ancestor of main")


def verify_checks(manifest):
    source = manifest["source_sha"]
    build, safety = manifest["build"], manifest["safety"]
    verify_run(build["run_id"], build["attempt"], "build.yml", "push", source, BUILD_JOBS)
    verify_run(safety["run_id"], safety["attempt"], "deployment-safety.yml", "push", source, SAFETY_JOBS)


def job_logs(job):
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "racebib-release-verifier"}
    if auth()["github_token"]:
        headers["Authorization"] = "Bearer " + auth()["github_token"]
    raw, _ = read_url(API + f"/actions/jobs/{job['id']}/logs", headers, limit=16 * 1024 * 1024)
    text = re.sub(r"\x1b\[[0-9;]*m", "", raw.decode("utf-8-sig"))
    return re.sub(r"(?m)^\d{4}-\d\d-\d\dT[^ ]+ ", "", text)


def verify_failed_publication(manifest):
    """Only explicit recovery can accept an overall failed/cancelled publication.
    Both actual image jobs and preflight MUST have succeeded; logs bind all manifest fields.
    """
    source, publication = manifest["source_sha"], manifest["publication"]
    jobs = verify_run(publication["run_id"], publication["attempt"], "publish-images.yml", "workflow_run", source,
                      IMAGE_JOBS, publishing=True, conclusions=("failure", "cancelled"))
    finalizers = [job for job in jobs if job.get("name") == "release"]
    require(len(finalizers) == 1 and finalizers[0].get("status") == "completed" and
            finalizers[0].get("conclusion") in ("failure", "cancelled"), "Publication did not fail at finalization")
    log = job_logs(finalizers[0])
    expected = {"SOURCE": source, "BUILD_RUN_ID": str(manifest["build"]["run_id"]),
                "BUILD_ATTEMPT": str(manifest["build"]["attempt"]),
                "SAFETY_RUN_ID": str(manifest["safety"]["run_id"]), "SAFETY_ATTEMPT": str(manifest["safety"]["attempt"]),
                "API_DIGEST": manifest["api"].split("@", 1)[1], "WEB_DIGEST": manifest["web"].split("@", 1)[1]}
    for name, value in expected.items():
        found = re.findall(r"(?m)^\s+" + name + r":\s*([^\r\n]+)", log)
        require(found and set(found) == {value}, "Manifest does not match publication inputs: " + name)
    for component in ("api", "web"):
        job = next(job for job in jobs if job["name"] == "publish-" + component)
        matches = re.findall(r"##\[group\]Digest\s*\n(sha256:[0-9a-f]{64})\s*\n##\[endgroup\]", job_logs(job))
        require(matches == [manifest[component].split("@", 1)[1]], "Manifest differs from successful publisher output")


def verify_release(source, release_tag=None):
    require(SHA.fullmatch(source) is not None, "A full lowercase source SHA is required")
    tag = release_tag or "racebib-" + source
    require(tag == "racebib-" + source or
            (RECOVERY_TAG.fullmatch(tag) and RECOVERY_TAG.fullmatch(tag)[1] == source), "Release tag/source mismatch")
    release = request("/releases/tags/" + tag)
    require(release and release.get("immutable") is True and release.get("draft") is False and
            release.get("tag_name") == tag, "Release must exist, be published and immutable")
    verify_source_tag(source, tag)
    raw = read_asset(release, "release.json")
    manifest = validate_manifest(json.loads(raw), source)
    verify_checks(manifest)
    result = {"manifest": manifest, "manifest_digest": sha256(raw), "release_id": release["id"]}
    if RECOVERY_TAG.fullmatch(tag):
        receipt = json.loads(read_asset(release, "recovery.json"))
        validate_recovery_receipt(receipt, source, tag, sha256(raw))
        verify_failed_publication(manifest)
        verify_registry_images(manifest)
        recovery = receipt["recovery"]
        verify_run(recovery["run_id"], recovery["attempt"], "recover-publication.yml", "workflow_dispatch", source,
                   {"recover"}, recovery=True)
        result["release_tag"] = tag
    else:
        publication = manifest["publication"]
        verify_run(publication["run_id"], publication["attempt"], "publish-images.yml", "workflow_run", source,
                   PUBLISH_JOBS, publishing=True)
    return result


def verify_registry_images(manifest):
    """GET only: compare original attempt tags, OCI digests, amd64 config and source labels.
    Never login, push, rebuild, retag, or run image contents.
    """
    evidence = {}
    for component in ("api", "web"):
        package = REPO + "-" + component
        token_headers = {"User-Agent": "racebib-release-verifier"}
        credentials = auth()
        if credentials["ghcr_token"]:
            require(credentials["ghcr_username"], "Set GHCR_USERNAME for private package reads")
            basic = credentials["ghcr_username"] + ":" + credentials["ghcr_token"]
            token_headers["Authorization"] = "Basic " + base64.b64encode(basic.encode()).decode()
        url = "https://ghcr.io/token?" + urllib.parse.urlencode(
            {"service": "ghcr.io", "scope": "repository:" + package + ":pull"})
        data, _ = read_url(url, token_headers)
        token = json.loads(data).get("token")
        require(isinstance(token, str) and token, "Missing GHCR pull authorization")
        headers = {"Authorization": "Bearer " + token, "User-Agent": "racebib-release-verifier",
                   "Accept": "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json"}

        def get_object(kind, reference):
            require(DIGEST.fullmatch(reference) or re.fullmatch(r"[0-9a-f]{40}-[1-9][0-9]*-[1-9][0-9]*", reference), "Invalid registry reference")
            raw, metadata = read_url(f"https://ghcr.io/v2/{package}/{kind}/{reference}", headers, limit=2 * 1024 * 1024)
            digest = sha256(raw)
            if DIGEST.fullmatch(reference):
                require(digest == reference, "Registry object digest mismatch")
            if kind == "manifests":
                require(metadata.get("docker-content-digest") == digest, "Registry digest header mismatch")
            return raw

        digest = manifest[component].split("@", 1)[1]
        publication = manifest["publication"]
        attempt_tag = f"{manifest['source_sha']}-{publication['run_id']}-{publication['attempt']}"
        raw = get_object("manifests", digest)
        require(sha256(get_object("manifests", attempt_tag)) == digest, "Original image attempt tag was moved or mismatched")
        image = json.loads(raw)
        if image.get("mediaType") in ("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json"):
            choices = [item for item in image.get("manifests", []) if item.get("platform", {}).get("os") == "linux" and
                       item.get("platform", {}).get("architecture") == "amd64"]
            require(len(choices) == 1, "Image must contain exactly one linux/amd64 platform")
            require(DIGEST.fullmatch(choices[0].get("digest", "")), "Invalid amd64 manifest digest")
            image = json.loads(get_object("manifests", choices[0]["digest"]))
        require(image.get("mediaType") in ("application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json"),
                "Unsupported image manifest")
        config_digest = image.get("config", {}).get("digest", "")
        require(DIGEST.fullmatch(config_digest), "Invalid image config digest")
        config = json.loads(get_object("blobs", config_digest))
        labels = config.get("config", {}).get("Labels") or {}
        require(config.get("architecture") == "amd64" and config.get("os") == "linux" and
                labels.get("org.opencontainers.image.revision") == manifest["source_sha"] and
                labels.get("org.opencontainers.image.source") == "https://github.com/" + REPO,
                "Image platform/source labels do not match the source commit")
        evidence[component] = {"image": manifest[component], "attempt_tag": attempt_tag, "config_digest": config_digest}
    return evidence


def recovery_tag(source, run_id, attempt):
    require(SHA.fullmatch(source) and type(run_id) is int and run_id > 0 and type(attempt) is int and attempt > 0,
            "Invalid recovery identity")
    return f"racebib-{source}-recovery-{run_id}-{attempt}"


def validate_recovery_receipt(receipt, source, tag, manifest_digest):
    require(isinstance(receipt, dict) and type(receipt.get("format")) is int and receipt["format"] == 1 and
            receipt.get("repository") == REPO and receipt.get("source_sha") == source and
            receipt.get("release_tag") == tag and receipt.get("manifest_digest") == manifest_digest and
            receipt.get("source_release_tag") == "racebib-" + source and
            type(receipt.get("source_release_id")) is int and receipt["source_release_id"] > 0,
            "Recovery receipt does not match the source, tag or exact manifest bytes")
    identity = receipt.get("recovery")
    require(isinstance(identity, dict), "Missing recovery identity")
    require(tag == recovery_tag(source, identity.get("run_id"), identity.get("attempt")), "Recovery tag/run identity mismatch")


def recovery_plan(source):
    require(SHA.fullmatch(source), "A full lowercase source SHA is required")
    tag = "racebib-" + source
    release = request("/releases/tags/" + tag)
    require(release and release.get("tag_name") == tag and release.get("immutable") is False and
            type(release.get("id")) is int, "Recovery requires the existing non-immutable source release; nothing will be replaced")
    verify_source_tag(source, tag)
    raw = read_asset(release, "release.json")
    manifest = validate_manifest(json.loads(raw), source)
    require(set(manifest) == {"format", "repository", "source_sha", "api", "web", "build", "safety", "publication"}, "Unexpected source manifest fields")
    verify_checks(manifest)
    verify_failed_publication(manifest)
    images = verify_registry_images(manifest)
    return {"source_sha": source, "source_release_tag": tag, "source_release_id": release["id"],
            "manifest_digest": sha256(raw), "manifest": manifest, "images": images}, raw


def prepare_recovery(source, output_directory):
    plan, raw = recovery_plan(source)
    run_id, attempt = int(os.environ["GITHUB_RUN_ID"]), int(os.environ["GITHUB_RUN_ATTEMPT"])
    tag = recovery_tag(source, run_id, attempt)
    # An interruption never authorizes clobbering even an orphan/draft target. Use a new attempt/tag.
    require(request("/releases/tags/" + tag) is None and request("/git/ref/tags/" + tag) is None,
            "Recovery target already exists; leave it untouched and use a new workflow attempt")
    definition = request("/actions/workflows/recover-publication.yml")
    run = request(f"/actions/runs/{run_id}/attempts/{attempt}")
    require(definition and run and run.get("workflow_id") == definition["id"] and run.get("event") == "workflow_dispatch" and
            run.get("head_branch") == "main" and run.get("status") == "in_progress" and
            run.get("repository", {}).get("full_name") == REPO and run.get("head_repository", {}).get("full_name") == REPO and
            run.get("display_title") == "recover publication " + source, "Preparation requires the active trusted manual recovery workflow")
    receipt = {"format": 1, "repository": REPO, "source_sha": source, "release_tag": tag,
               "manifest_digest": plan["manifest_digest"], "source_release_tag": plan["source_release_tag"],
               "source_release_id": plan["source_release_id"], "recovery": {"run_id": run_id, "attempt": attempt}}
    validate_recovery_receipt(receipt, source, tag, sha256(raw))
    directory = Path(output_directory)
    directory.mkdir(parents=True, exist_ok=False)
    (directory / "release.json").write_bytes(raw) # Preserve the ORIGINAL asset bytes and digest.
    (directory / "recovery.json").write_bytes(canonical(receipt) + b"\n")
    return tag


def check_recovery_created(source, tag, directory):
    """Final in-workflow check; consumers separately require this workflow attempt to finish successfully."""
    require(SHA.fullmatch(source) and isinstance(tag, str) and RECOVERY_TAG.fullmatch(tag) and
            RECOVERY_TAG.fullmatch(tag)[1] == source, "Invalid recovery tag/source selection")
    release = request("/releases/tags/" + tag)
    require(release and release.get("immutable") is True and release.get("draft") is False and release.get("tag_name") == tag,
            "Recovery release is not immutable; retain it but DO NOT deploy it")
    verify_source_tag(source, tag)
    for name in ("release.json", "recovery.json"):
        require(read_asset(release, name) == (Path(directory) / name).read_bytes(), "Published recovery asset differs from prepared evidence")
    raw = (Path(directory) / "release.json").read_bytes()
    validate_manifest(json.loads(raw), source)
    validate_recovery_receipt(json.loads((Path(directory) / "recovery.json").read_bytes()), source, tag, sha256(raw))


def preflight(source, build_id, attempt):
    require(SHA.fullmatch(source) is not None, "Invalid source")
    # Fail on an existing draft as well as an existing published mapping. Never clobber assets.
    require(request("/releases/tags/racebib-" + source) is None, "Source release already exists; do not republish")
    require(request("/git/ref/tags/racebib-" + source) is None, "Source release tag already exists; investigate interrupted publication")
    verify_run(build_id, attempt, "build.yml", "push", source, BUILD_JOBS)
    # Build and deployment-safety are independent workflows. Wait for the exact source's
    # main-push safety run, not a green PR check or a differently sourced latest run.
    deadline = time.monotonic() + 900
    while True:
        response = request(f"/actions/workflows/deployment-safety.yml/runs?event=push&head_sha={source}&per_page=100")
        require(response and isinstance(response.get("workflow_runs"), list), "Missing deployment-safety workflow")
        runs = [r for r in response["workflow_runs"] if r.get("head_sha") == source and r.get("head_branch") == "main"]
        if runs:
            latest = max(runs, key=lambda r: (r["id"], r["run_attempt"]))
            if latest.get("status") == "completed":
                verify_run(latest["id"], latest["run_attempt"], "deployment-safety.yml", "push", source, SAFETY_JOBS)
                return {"run_id": latest["id"], "attempt": latest["run_attempt"]}
        require(time.monotonic() < deadline, "Timed out waiting for source-matched deployment-safety checks")
        time.sleep(10)
    # Do not request repository Administration permission just to read this setting.
    # Finalization and every consumer must verify the published release's immutable flag.


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("preflight", "verify", "manifest", "recovery-plan", "prepare-recovery", "check-recovery"))
    parser.add_argument("sha")
    parser.add_argument("--release-tag")
    parser.add_argument("--output-dir")
    args = parser.parse_args()
    if args.command == "preflight":
        safety = preflight(args.sha, int(os.environ["BUILD_RUN_ID"]), int(os.environ["BUILD_ATTEMPT"]))
        print("safety_run_id=" + str(safety["run_id"]))
        print("safety_attempt=" + str(safety["attempt"]))
    elif args.command == "verify":
        result = verify_release(args.sha, release_tag=args.release_tag)
        print(json.dumps(result, indent=2))
    elif args.command == "recovery-plan":
        plan, _ = recovery_plan(args.sha)
        print(json.dumps(plan, indent=2))
    elif args.command == "prepare-recovery":
        require(args.output_dir, "An exclusive new output directory is required")
        print("release_tag=" + prepare_recovery(args.sha, args.output_dir))
    elif args.command == "check-recovery":
        require(args.output_dir and args.release_tag, "Prepared output directory and recovery tag are required")
        check_recovery_created(args.sha, args.release_tag, args.output_dir)
    else:
        manifest = {"format": 1, "repository": REPO, "source_sha": args.sha,
                    "api": f"ghcr.io/{REPO}-api@" + os.environ["API_DIGEST"],
                    "web": f"ghcr.io/{REPO}-web@" + os.environ["WEB_DIGEST"],
                    "build": {"run_id": int(os.environ["BUILD_RUN_ID"]), "attempt": int(os.environ["BUILD_ATTEMPT"])},
                    "safety": {"run_id": int(os.environ["SAFETY_RUN_ID"]), "attempt": int(os.environ["SAFETY_ATTEMPT"])},
                    "publication": {"run_id": int(os.environ["GITHUB_RUN_ID"]), "attempt": int(os.environ["GITHUB_RUN_ATTEMPT"])}}
        validate_manifest(manifest, args.sha)
        print(canonical(manifest).decode())


if __name__ == "__main__":
    try:
        main()
    except (SafetyError, KeyError, ValueError, TypeError, OSError) as error:
        message = str(error) if isinstance(error, SafetyError) else "Invalid or unavailable release evidence"
        print("Release verification failed: " + message + ". No release accepted.", file=sys.stderr)
        sys.exit(1)
