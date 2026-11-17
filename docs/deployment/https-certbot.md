# Transition an existing RaceBib deployment to HTTPS

This is an administrator maintenance procedure, not an automatic deployment. The reported live domain is `racebib.vreboton.com`, with a certificate already issued by Certbot 4.0.0 using HTTP-01 webroot `/var/www/letsencrypt`. The application is still configured with the public-IP HTTP origin and `restricted-http`; a working browser padlock alone does not complete that transition. No live server changes or renewal were performed while implementing this support.

## Prerequisites and preservation

Review/merge and install the reviewed host artifacts only after authorization. Retain the current approved API/web manifest, image digests and optional immutable recovery tag. Do not initialize/adopt/upgrade MySQL again, rotate credentials, reset state, reprovision the administrator, replace `.env` with the example, run volume deletion, or rebuild an old release. This transition changes Nginx routing and two application settings. The MySQL image/configuration/volume and database baseline stay unchanged; account records and secrets stay intact. A normal verified redeployment appends history while retaining the previous distinct release.

Before maintenance, take the runbook's verified database/off-instance backup and preserve root-only copies of `.env`, `/opt/racebib/state`, and the current active Nginx site. Keep these outside Git and CI logs, with protected access; `.env` contains secrets. Preserve certificates and renewal configuration through the existing secure backup process; never copy private keys into the repository. Use an authorized maintenance window and keep the administrator/SSM session available.

Confirm DNS A records resolve to the intended EC2 Elastic IP. Only advertise AAAA if IPv6 routing and firewall policy actually work. Check `sudo certbot certificates` for the certificate name and paths. The reviewed template expects `/etc/letsencrypt/live/racebib.vreboton.com/{fullchain,privkey}.pem`; if Certbot chose a suffixed name, stop and reconcile the certificate lineage through review instead of editing the template to bypass the validator. Do not unnecessarily reissue an already valid certificate.

For HTTP-01 renewal, allow inbound TCP 80 from the Internet in the AWS security group and any host firewall/NACL, not only the original tester CIDRs. HTTP-01 validation requires port 80; CA validator addresses are not a stable allowlist. Allow TCP 443 for intended users and the deployment verifier. Apply equivalent IPv6 rules only if AAAA is published. Keep SSH restricted and MySQL/API/web host ports closed externally; web stays bound to loopback 8080. Permit DNS/HTTPS egress for ACME. If public 80 is prohibited, use a separately reviewed DNS-01 setup. See [Let's Encrypt's port-80 guidance](https://letsencrypt.org/docs/allow-port-80/) and [HTTP-01 requirements](https://letsencrypt.org/docs/challenge-types/).

## Install the exact reviewed site

Run on the host only after reviewing the diff. Copy this branch's reviewed `operations.py` to `/opt/racebib/scripts/operations.py` and HTTPS template to `/opt/racebib/nginx/racebib-https.conf.example`, both root-owned mode 0644. Keep all other installed scripts/configuration consistent with the approved host artifact. The validator compares the active file byte-for-byte with that template after domain replacement; a manually altered or Certbot-rewritten proxy site is rejected.

```bash
sudo install -d -o root -g root -m 0755 /var/www/letsencrypt/.well-known/acme-challenge
sudo python3 - <<'PY'
from pathlib import Path
domain = 'racebib.vreboton.com'
template = Path('/opt/racebib/nginx/racebib-https.conf.example').read_text()
target = Path('/etc/nginx/sites-available/racebib')
target.write_text(template.replace('__DOMAIN__', domain))
target.chmod(0o644)
PY
sudo ln -sfn /etc/nginx/sites-available/racebib /etc/nginx/sites-enabled/racebib
sudo nginx -t
sudo systemctl reload nginx
```

Review existing enabled sites first: resolve duplicate default listeners or duplicate RaceBib server names deliberately; retain unrelated sites. If `nginx -t` fails, restore the preserved site and investigate before reloading. The HTTP redirect belongs inside `location /`; moving it to server scope prevents the ACME location from serving files. The ACME directory holds public challenge responses only and must be readable by Nginx; Certbot writes as root. Never place secrets there. The template does not proxy challenges or permit cleartext application access.

From a trusted external test client, verify a synthetic challenge before renewal:

```bash
# On EC2; this public test file contains no credentials:
printf '%s\n' 'racebib-acme-test' | sudo tee /var/www/letsencrypt/.well-known/acme-challenge/racebib-probe >/dev/null
# From an external client, without -L or -k:
curl -i http://racebib.vreboton.com/.well-known/acme-challenge/racebib-probe
curl -i http://racebib.vreboton.com/.well-known/acme-challenge/missing-probe
curl -I http://racebib.vreboton.com/api/v1/auth/login
curl -fsS https://racebib.vreboton.com/health
# On EC2, after checking the response:
sudo rm -- /var/www/letsencrypt/.well-known/acme-challenge/racebib-probe
```

Expect 200 with the synthetic text and no Location header, 404 for the missing file, 308 to the same path on canonical HTTPS for the API request, and successful HTTPS health with trusted hostname verification. Do not send real credentials to HTTP during testing.

## Update application configuration and redeploy the current release

Use a privileged editor for `/opt/racebib/.env` and change **only**:

```text
APPLICATION_BASE_URL=https://racebib.vreboton.com
DEPLOYMENT_ACCESS_MODE=https
```

Retain root ownership/mode 0600. Do not print/source `.env`, generate new JWT/database/admin secrets or change SMTP settings as part of this step. These two variables are application configuration, not database configuration. The running containers use the old values until a verified redeployment recreates API/web. Invitations require the HTTPS base URL plus working SMTP; validate delivery after this transition without changing existing accounts.

Select the existing successful immutable release from state, keeping its exact recovery tag if any. The following Bash commands read only non-secret release metadata; they intentionally refuse an existing maintenance barrier:

```bash
mapfile -t current_release < <(sudo python3 - <<'PY'
import json
from pathlib import Path
state = json.loads(Path('/opt/racebib/state/release-state.json').read_text())
assert state['inflight'] is None and state['current'], 'Investigate maintenance state before transitioning'
current = state['current']
print(current['manifest']['source_sha'])
print(current.get('release_tag', ''))
PY
)
[[ ${current_release[0]:-} =~ ^[0-9a-f]{40}$ ]] || { echo 'No valid current release'; exit 1; }
args=("${current_release[0]}")
if [[ -n ${current_release[1]:-} ]]; then args+=(--release-tag "${current_release[1]}"); fi
sudo bash /opt/racebib/scripts/deploy.sh "${args[@]}"
```

Do not add `--migrate` for this configuration-only transition. Deployment still verifies immutable assets/provenance/digests, database baseline and exact schema, creates a backup, verifies the running API's database-backed readiness, and updates release history atomically. It never recreates MySQL. If a persistent barrier already exists, investigate its cause and follow the runbook's explicit recovery procedure; do not delete it or change environment values to evade checks. A failure after stopping writers leaves maintenance active as before. Do not repeatedly run ordinary deployment across that barrier.

Alternatively, dispatch `deploy-production-manual` on main with the same source SHA, optional exact recovery tag, `migrate=false`, and the production reviewer approval. Install the reviewed host files and update `.env` manually first; the workflow does not provision Nginx, certificates or host secrets. Verify normal application behavior, authentication, HTTPS invitation URLs and SMTP delivery after deployment. Keep the restricted-HTTP configuration backup for incident analysis; returning to disposable HTTP testing requires separately reviewed access restrictions and cannot serve as a normal HTTPS rollback. Image rollback does not revert host Nginx or `.env`.

## Certbot renewal without template rewrites

The existing lineage should use the webroot authenticator with `/var/www/letsencrypt`. Inspect its protected renewal configuration through administrator access; do not hand-edit it or replace a working certificate unnecessarily. Use Certbot's supported configuration procedure if its authenticator/webroot differs. Avoid `certbot --nginx` or an installer that edits the active site. A certificate issued with `certonly --webroot` needs a successful-renewal deploy hook so Nginx loads the renewed files.

Install this root-owned hook, mode 0755, at `/etc/letsencrypt/renewal-hooks/deploy/racebib-nginx`:

```sh
#!/bin/sh
set -eu
/usr/sbin/nginx -t
/usr/bin/systemctl reload nginx
```

Check executable paths on this Ubuntu installation and protect the hook directories from non-root writes. A failed config check must prevent reload and alert the operator. The hook changes neither the site nor application containers. Do not duplicate hooks that already perform this action.

```bash
sudo certbot renew --cert-name racebib.vreboton.com --dry-run --run-deploy-hooks
systemctl list-timers --all | grep -i certbot
```

Use the actual certificate name reported by `certbot certificates`. Confirm the installed timer/cron mechanism is enabled; package, snap and other installations may use different unit names, so do not blindly create a second schedule. Monitor renewal failures and certificate expiry. The dry run uses the staging CA; deploy hooks normally do not run in a dry run unless `--run-deploy-hooks` is specified. Refer to [Certbot renewal and hooks](https://eff-certbot.readthedocs.io/en/stable/using.html#renewing-certificates) and confirm flags with the installed 4.0.0 CLI. Do not close public port 80 after issuance while using HTTP-01.

## Local rehearsal

`python3 -B -m unittest discover -s deploy/tests -v` verifies strict HTTPS template selection and certificate-failure handling. `python3 -B deploy/tests/nginx_https_rehearsal.py` runs pinned official Nginx with a temporary self-signed test CA/certificate and synthetic loopback upstream. It verifies ACME 200/404 without redirection, canonical 308 redirects for other paths/methods, trusted TLS, preserved proxy paths/headers and security headers. It uses a unique container, network isolation, no host ports or production data, and removes only its own container. Docker and OpenSSL are required; do not run this rehearsal on the 1 GiB production host. Real public DNS, firewall reachability, the installed Certbot timer, Let's Encrypt renewal and EC2 readiness remain administrator staging checks.
