#!/usr/bin/env bash
set -Eeuo pipefail
exec python3 -B /opt/racebib/scripts/restore_rehearsal.py "$@"
