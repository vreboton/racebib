#!/bin/bash
# CloudFormation user data for a fresh staging VM only. Contains public keys, never secrets.
set -Eeuo pipefail
umask 077
RUN='@@RUN@@'
TOKEN=$(curl -fsS -X PUT -H 'X-aws-ec2-metadata-token-ttl-seconds: 300' http://169.254.169.254/latest/api/token)
IID=$(curl -fsS -H "X-aws-ec2-metadata-token: $TOKEN" http://169.254.169.254/latest/meta-data/instance-id)
IP=$(curl -fsS -H "X-aws-ec2-metadata-token: $TOKEN" http://169.254.169.254/latest/meta-data/public-ipv4)
[[ $IID =~ ^i-[0-9a-f]{17}$ && $IP != '15.135.108.24' && $RUN =~ ^[0-9a-f]{32}$ ]]
DOMAIN="stage-$RUN.racebib.invalid"
install -d -m 0700 /etc/racebib-staging
python3 - "$RUN" "$IID" "$IP" "$DOMAIN" '@@BUNDLE_DIGEST@@' <<'PY'
import json,sys
from pathlib import Path
Path('/etc/racebib-staging/identity.json').write_text(json.dumps(dict(zip(['run_id','instance_id','public_ip','domain','bundle_digest'],sys.argv[1:]))))
PY
# Best-effort cost fuse, not a substitute for verified CloudFormation teardown.
shutdown -h +@@TTL@@
# Avoid HTTP package egress: SG allows only outbound TCP 443 (and VPC resolver).
sed -i 's|http://|https://|g' /etc/apt/sources.list.d/ubuntu.sources
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq ca-certificates curl gnupg nginx openssl python3
install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod 0644 /etc/apt/keyrings/docker.asc
echo 'deb [arch=amd64 signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable' > /etc/apt/sources.list.d/docker.list
apt-get update -qq
apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker nginx ssh
# A small memory cushion; no application builds occur here.
fallocate -l 1G /swapfile
chmod 0600 /swapfile
mkswap /swapfile >/dev/null
swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
for account in racebib-rehearsal racebib-deploy; do
  useradd --create-home --shell /bin/bash "$account"
  passwd -l "$account"
  chown root:root "/home/$account"
  chmod 0755 "/home/$account"
  find "/home/$account" -maxdepth 1 -type f -exec chown root:root {} +
done
install -d -m 0755 /etc/ssh/authorized_keys
printf '%s\n' 'from="@@CIDR@@",restrict,command="/usr/bin/sudo -n /usr/local/sbin/racebib-stage-control" @@BOOTSTRAP_KEY@@' > /etc/ssh/authorized_keys/racebib-rehearsal
printf '%s\n' 'from="@@CIDR@@",restrict,command="/usr/local/sbin/racebib-ssh" @@DEPLOY_KEY@@' > /etc/ssh/authorized_keys/racebib-deploy
chmod 0644 /etc/ssh/authorized_keys/*
cat > /etc/ssh/sshd_config.d/00-racebib-staging.conf <<'CONF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitUserEnvironment no
Match User racebib-deploy,racebib-rehearsal
    AuthorizedKeysFile /etc/ssh/authorized_keys/%u
    AuthenticationMethods publickey
    DisableForwarding yes
    PermitTTY no
    PermitTunnel no
Match User racebib-rehearsal
    ForceCommand /usr/bin/sudo -n /usr/local/sbin/racebib-stage-control
Match User racebib-deploy
    ForceCommand /usr/local/sbin/racebib-ssh
Match all
CONF
echo '@@CONTROL@@' | base64 -d > /usr/local/sbin/racebib-stage-control
chmod 0755 /usr/local/sbin/racebib-stage-control
cat > /etc/sudoers.d/racebib-staging <<'CONF'
Defaults:racebib-rehearsal env_keep += "SSH_ORIGINAL_COMMAND"
racebib-rehearsal ALL=(root) NOPASSWD: NOSETENV: /usr/local/sbin/racebib-stage-control
racebib-deploy ALL=(root) NOPASSWD: NOSETENV: /usr/local/sbin/racebib-ssh-deploy
CONF
chmod 0440 /etc/sudoers.d/racebib-staging
visudo -cf /etc/sudoers.d/racebib-staging
sshd -t
systemctl reload ssh
# A per-run private CA. Its private key never leaves this disposable instance.
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=RaceBib staging $RUN" -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' -keyout /etc/racebib-staging/ca.key -out /etc/racebib-staging/ca.crt >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -subj "/CN=$DOMAIN" -keyout /etc/racebib-staging/server.key -out /etc/racebib-staging/server.csr >/dev/null 2>&1
printf 'subjectAltName=DNS:%s,IP:%s\nextendedKeyUsage=serverAuth\nbasicConstraints=CA:FALSE\n' "$DOMAIN" "$IP" > /etc/racebib-staging/server.ext
openssl x509 -req -in /etc/racebib-staging/server.csr -CA /etc/racebib-staging/ca.crt -CAkey /etc/racebib-staging/ca.key -CAcreateserial -days 1 -extfile /etc/racebib-staging/server.ext -out /etc/racebib-staging/server.crt >/dev/null 2>&1
install -m 0644 /etc/racebib-staging/ca.crt /usr/local/share/ca-certificates/racebib-staging.crt
update-ca-certificates >/dev/null
install -d -m 0755 "/etc/letsencrypt/live/$DOMAIN" /var/www/letsencrypt
install -m 0644 /etc/racebib-staging/server.crt "/etc/letsencrypt/live/$DOMAIN/fullchain.pem"
install -m 0600 /etc/racebib-staging/server.key "/etc/letsencrypt/live/$DOMAIN/privkey.pem"
printf '127.0.0.1 %s\n' "$DOMAIN" >> /etc/hosts
python3 - <<'PY' > /dev/console
import base64,json
from pathlib import Path
value=json.loads(Path('/etc/racebib-staging/identity.json').read_text())
value['host_key']=Path('/etc/ssh/ssh_host_ed25519_key.pub').read_text().strip()
value['ca_pem']=base64.b64encode(Path('/etc/racebib-staging/ca.crt').read_bytes()).decode()
print('RACEBIB_STAGING_TRUST '+json.dumps(value),flush=True)
PY
