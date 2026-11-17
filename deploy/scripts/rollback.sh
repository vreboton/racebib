#!/usr/bin/env bash
set -Eeuo pipefail
exec /bin/bash /opt/racebib/scripts/common.sh rollback "$@"
