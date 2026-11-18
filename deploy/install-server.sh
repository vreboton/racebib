#!/usr/bin/env bash
# Run manually from a reviewed artifact directory; installs files, never starts containers.
set -Eeuo pipefail
[[ $(id -u) == 0 ]] || { echo 'Run as root' >&2; exit 1; }
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
install -d -o root -g root -m 0755 /opt/racebib /opt/racebib/scripts /opt/racebib/nginx
install -d -o root -g root -m 0700 /opt/racebib/state /opt/racebib/backups
install -o root -g root -m 0644 compose.database.yml compose.production.yml /opt/racebib/
install -o root -g root -m 0644 deploy/scripts/*.py /opt/racebib/scripts/
install -o root -g root -m 0755 deploy/scripts/*.sh /opt/racebib/scripts/
install -o root -g root -m 0644 deploy/nginx/*.conf /opt/racebib/nginx/
install -o root -g root -m 0644 deploy/nginx/racebib-https.conf.example /opt/racebib/nginx/
install -o root -g root -m 0755 deploy/scripts/ssh-dispatch.sh /usr/local/sbin/racebib-ssh
install -o root -g root -m 0755 deploy/scripts/deploy.sh /usr/local/sbin/racebib-deploy
install -o root -g root -m 0755 deploy/scripts/ssh-deploy.sh /usr/local/sbin/racebib-ssh-deploy
echo 'Files installed with explicit executable/ownership permissions. Existing .env, data, state and host Nginx policies were preserved.'
