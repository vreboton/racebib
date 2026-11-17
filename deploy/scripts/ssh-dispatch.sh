#!/usr/bin/env bash
# Root-owned forced-command entry point. No arbitrary SSH commands or uploads.
set -Eeuo pipefail
if [[ ${SSH_ORIGINAL_COMMAND:-} =~ ^deploy\ ([0-9a-f]{40})(\ --migrate)?(\ --release-tag\ (racebib-[0-9a-f]{40}-recovery-[1-9][0-9]*-[1-9][0-9]*))?$ ]]; then
  tag=${BASH_REMATCH[1]}
  migrate=${BASH_REMATCH[2]:-}
  release_tag=${BASH_REMATCH[4]:-}
  args=("$tag")
  if [[ -n $migrate ]]; then args+=(--migrate); fi
  if [[ -n $release_tag ]]; then
    [[ $release_tag == racebib-"$tag"-recovery-* ]] || { echo 'Release tag/source mismatch' >&2; exit 1; }
    args+=(--release-tag "$release_tag")
  fi
  exec sudo -n /usr/local/sbin/racebib-deploy "${args[@]}"
fi
echo 'Only pinned RaceBib deployment is permitted' >&2
exit 1
