"""Root-only fixed-path SSH entry; existing engine owns all deployment mutations."""
import argparse
import json
import os
import re
import signal
from pathlib import Path
import subprocess
import sys
import tempfile

# -I ignores inherited Python paths; add only the root-owned installed directory.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from transport_common import parameters
from release_state import ReleaseState


def run_engine(args, log, environment):
    # Client disconnect must not deliver an unhandled SIGHUP into the engine.
    # It may finish after the client is gone; the client must inspect, never retry.
    previous = signal.signal(signal.SIGHUP, signal.SIG_IGN)
    try:
        return subprocess.run(args, stdout=log, stderr=subprocess.STDOUT, check=False,
                              env=environment, start_new_session=True)
    finally:
        signal.signal(signal.SIGHUP, previous)


def inspect_state(source, root=Path("/opt/racebib")):
    state_path = root / "state/release-state.json"
    if not state_path.is_file():
        raise RuntimeError("Release state missing; explicit inspection required")
    value = ReleaseState(state_path).value
    current = value["current"]
    if value["inflight"] is not None or not current or current["manifest"]["source_sha"] != source:
        raise RuntimeError("Target release state not verified")
    if not value["history"]:
        raise RuntimeError("Successful deployment history missing")
    latest = value["history"][-1]
    if latest["release"]["manifest_digest"] != current["manifest_digest"] or not re.fullmatch(r"[0-9a-f]{64}", latest["schema_hash"]):
        raise RuntimeError("Successful history does not match current release")
    return {"deployment": "succeeded", "host_state": "verified", "source_sha": source,
            "release_tag": current.get("release_tag") or "racebib-" + source,
            "manifest_digest": current["manifest_digest"], "api": current["manifest"]["api"],
            "web": current["manifest"]["web"], "schema_hash": latest["schema_hash"]}


def deploy(source, tag):
    parameters(source, tag)
    if os.geteuid() != 0:
        raise RuntimeError("Requires root via restricted sudo entry")
    os.umask(0o077)
    args = ["/bin/bash", "/opt/racebib/scripts/deploy.sh", source]
    if tag:
        args += ["--release-tag", tag]
    descriptor, path = tempfile.mkstemp(prefix="ssh-deployment-", suffix=".log", dir="/opt/racebib/state")
    print("Root-only deployment log:", Path(path).name, flush=True)
    environment = {name: value for name, value in os.environ.items()
                   if name not in {"BASH_ENV", "ENV", "PYTHONPATH", "PYTHONHOME", "GH_TOKEN", "GHCR_TOKEN",
                                   "GITHUB_TOKEN", "GHCR_USERNAME", "GITHUB_ACTOR"} and not name.startswith("LD_")}
    environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
    with os.fdopen(descriptor, "w", encoding="utf-8") as log:
        result = run_engine(args, log, environment)
    if result.returncode:
        raise RuntimeError("Engine failed; preserve maintenance state and inspect root-only log")
    receipt = inspect_state(source)
    if receipt["release_tag"] != (tag or "racebib-" + source):
        raise RuntimeError("Recorded release selector differs from the requested selector")
    print("RACEBIB_RESULT " + json.dumps(receipt))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("--release-tag", default="")
    args = parser.parse_args()
    try:
        deploy(args.source, args.release_tag)
    except Exception:
        print("Deployment unconfirmed; inspect host maintenance state and root-only logs before retrying.", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
