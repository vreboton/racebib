#!/bin/bash
# Only this constrained wrapper, never the unrestricted engine, belongs in CI sudoers.
set -Eeuo pipefail
exec /usr/bin/python3 -I -B /opt/racebib/scripts/ssh_entry.py "$@"
