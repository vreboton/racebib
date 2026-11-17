"""Release provenance shared by CI and the server. Standard library only; never execute a manifest."""
import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

REPO = "vreboton/racebib"
API = "https://api.github.com/repos/" + REPO
SHA = re.compile(r"[0-9a-f]{40}")
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
BUILD_JOBS = {"backend", "backend-mysql-integration", "frontend"}
PUBLISH_JOBS = {"publish-api", "publish-web", "release"}
SAFETY_JOBS = {"deployment-safety"}


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
    token = os.environ.get("GH_TOKEN")
    if token:
        headers["Authorization"] = "Bearer " + token
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(API + path, data=None if data is None else canonical(data), headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
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


def verify_run(run_id, attempt, workflow, event, source, required, *, publishing=False):
    require(type(run_id) is int and run_id > 0 and type(attempt) is int and attempt > 0, "Invalid run identity")
    definition = request("/actions/workflows/" + workflow)
    run = request(f"/actions/runs/{run_id}/attempts/{attempt}")
    require(definition and run and run.get("workflow_id") == definition.get("id"), "Workflow identity mismatch")
    require(run.get("event") == event and run.get("status") == "completed" and
            run.get("conclusion") == "success", "Workflow attempt was not successful")
    require(run.get("repository", {}).get("full_name") == REPO and
            run.get("head_repository", {}).get("full_name") == REPO and run.get("head_branch") == "main",
            "Untrusted repository or branch")
    if not publishing:
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


def verify_release(source):
    require(SHA.fullmatch(source) is not None, "A full lowercase source SHA is required")
    tag = "racebib-" + source
    release = request("/releases/tags/" + tag)
    require(release and release.get("immutable") is True and release.get("draft") is False and
            release.get("tag_name") == tag, "Release must exist, be published and immutable")
    ref = request("/git/ref/tags/" + tag)
    require(ref and ref.get("object", {}).get("type") == "commit" and
            ref["object"]["sha"] == source, "Release tag is not the expected lightweight source tag")
    comparison = request(f"/compare/{source}...main")
    require(comparison and comparison.get("status") in ("ahead", "identical"), "Source is not an ancestor of main")
    assets = [asset for asset in release.get("assets", []) if asset.get("name") == "release.json"]
    require(len(assets) == 1 and assets[0].get("state") == "uploaded" and
            DIGEST.fullmatch(assets[0].get("digest", "")) is not None and
            0 < assets[0].get("size", 0) <= 16384, "Missing or unhashed release manifest")
    url = f"https://github.com/{REPO}/releases/download/{tag}/release.json"
    try:
        # No Authorization header: public download, including redirects to GitHub's asset host.
        with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "racebib-release-verifier"}), timeout=30) as response:
            raw = response.read(16385)
    except OSError:
        raise SafetyError("Manifest download failed") from None
    require(len(raw) <= 16384 and sha256(raw) == assets[0]["digest"], "Manifest asset digest mismatch")
    manifest = validate_manifest(json.loads(raw), source)
    build, publication = manifest["build"], manifest["publication"]
    verify_run(build["run_id"], build["attempt"], "build.yml", "push", source, BUILD_JOBS)
    verify_run(publication["run_id"], publication["attempt"], "publish-images.yml", "workflow_run", source,
               PUBLISH_JOBS, publishing=True)
    safety = manifest["safety"]
    verify_run(safety["run_id"], safety["attempt"], "deployment-safety.yml", "push", source, SAFETY_JOBS)
    return {"manifest": manifest, "manifest_digest": sha256(raw), "release_id": release["id"]}


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
    parser.add_argument("command", choices=("preflight", "verify", "manifest"))
    parser.add_argument("sha")
    args = parser.parse_args()
    if args.command == "preflight":
        safety = preflight(args.sha, int(os.environ["BUILD_RUN_ID"]), int(os.environ["BUILD_ATTEMPT"]))
        print("safety_run_id=" + str(safety["run_id"]))
        print("safety_attempt=" + str(safety["attempt"]))
    elif args.command == "verify":
        result = verify_release(args.sha)
        print(json.dumps(result, indent=2))
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
    except (SafetyError, KeyError, ValueError, TypeError):
        print("Release verification failed; inspect source/run identities and immutable release settings. No release accepted.", file=sys.stderr)
        sys.exit(1)
