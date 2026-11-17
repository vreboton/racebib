#!/usr/bin/env bash
# All operational entry points share Python validation, locking and durable state.
set -Eeuo pipefail
umask 077
exec python3 -B /opt/racebib/scripts/operations.py "$@"
