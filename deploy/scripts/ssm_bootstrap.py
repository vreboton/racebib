"""Invoked with Python isolated mode; imports only root-owned installed scripts."""
import sys

sys.path.insert(0, "/opt/racebib/scripts")
import ssm_entry

try:
    ssm_entry.main()
except Exception:
    print("Deployment failed; inspect host state and root-only logs. Never bypass maintenance state.", file=sys.stderr)
    sys.exit(1)
