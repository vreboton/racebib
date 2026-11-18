"""Read-only local preflight: reuse release provenance and verify both live OCI digests."""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from release import verify_release, verify_registry_images
from transport_common import parameters


def verify(source, tag):
    parameters(source, tag)
    release = verify_release(source, release_tag=tag or None)
    verify_registry_images(release["manifest"])  # Mandatory even for canonical releases.
    manifest = release["manifest"]
    return {"source_sha": source, "release_tag": tag or "racebib-" + source,
            "manifest_digest": release["manifest_digest"], "api": manifest["api"], "web": manifest["web"]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("--release-tag", default="")
    args = parser.parse_args()
    try:
        print(json.dumps(verify(args.source, args.release_tag)))
    except Exception:
        print("Release provenance or registry verification failed; no deployment was submitted.", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
