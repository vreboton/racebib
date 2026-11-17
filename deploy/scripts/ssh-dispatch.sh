#!/usr/bin/env bash
# Root-owned forced-command entry point. No arbitrary SSH commands or uploads.
set -Eeuo pipefail
if [[ ${SSH_ORIGINAL_COMMAND:-} =~ ^deploy\ ([0-9a-f]{40})(\ --migrate)?$ ]]; then
  tag=${BASH_REMATCH[1]}
  option=${BASH_REMATCH[2]:-}
  if [[ -n $option ]]; then
    exec sudo -n /usr/local/sbin/racebib-deploy "$tag" --migrate
  fi
  exec sudo -n /usr/local/sbin/racebib-deploy "$tag"
fi
echo 'Only pinned RaceBib deployment is permitted' >&2
exit 1
