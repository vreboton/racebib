"""INACTIVE historical SSM entry point, retained for audit; active transport is SSH."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

from ssm_deploy import parameters


def inspect_state(source=None, root=Path("/opt/racebib")):
    value = json.loads((root / "state/release-state.json").read_text())
    current = value.get("current")
    inflight = value.get("inflight")
    print(json.dumps({"current_sha": current["manifest"]["source_sha"] if current else None,
                      "maintenance": inflight is not None,
                      "stage": inflight.get("stage") if inflight else None}))
    if source and (inflight is not None or not current or current["manifest"]["source_sha"] != source):
        raise RuntimeError("Successful target state was not recorded")


def main():
    source = os.environ["SSM_SourceSha"]
    tag = os.environ.get("SSM_ReleaseTag", "")
    parameters(source, tag)
    if os.geteuid() != 0:
        raise RuntimeError("SSM operation requires root")
    args = ["/bin/bash", "/opt/racebib/scripts/deploy.sh", source]
    if tag:
        args += ["--release-tag", tag]
    os.umask(0o077)
    # Keep detailed output on the host. Never read or transmit .env here.
    descriptor, log_path = tempfile.mkstemp(prefix="ssm-deployment-", suffix=".log", dir="/opt/racebib/state")
    print("Root-only deployment log:", Path(log_path).name, flush=True)
    with os.fdopen(descriptor, "w", encoding="utf-8") as log:
        result = subprocess.run(args, stdout=log, stderr=subprocess.STDOUT, check=False)
    inspect_state(source if result.returncode == 0 else None)
    if result.returncode:
        raise RuntimeError("Deployment failed; inspect root-only log and use existing recovery procedure")
    print("RACEBIB_RESULT " + json.dumps({"deployment": "succeeded", "host_state": "verified", "source_sha": source}))
