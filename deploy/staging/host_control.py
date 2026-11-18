#!/usr/bin/python3 -I
"""Staging-only forced provisioning/recovery capability; never install on production."""
import base64
import copy
import contextlib
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import signal
import stat
import subprocess
import sys
import traceback
import urllib.request


def require(value, message):
    if not value:
        raise RuntimeError(message)


def imds(path):
    request = urllib.request.Request('http://169.254.169.254/latest/api/token', method='PUT', headers={'X-aws-ec2-metadata-token-ttl-seconds': '60'})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(request, timeout=3) as result:
        token = result.read().decode()
    with opener.open(urllib.request.Request('http://169.254.169.254/latest/meta-data/' + path, headers={'X-aws-ec2-metadata-token': token}), timeout=3) as result:
        return result.read().decode()


def guard(payload):
    require(os.geteuid() == 0, 'Root staging entry required')
    marker = Path('/etc/racebib-staging/identity.json')
    require(marker.is_file() and marker.stat().st_uid == 0 and not marker.stat().st_mode & 0o077, 'No private staging identity')
    value = json.loads(marker.read_text())
    require(re.fullmatch('[0-9a-f]{32}', value['run_id']) and value['run_id'] == payload['run_id'], 'Staging nonce mismatch')
    require(value['instance_id'] == payload['instance_id'] == imds('instance-id'), 'Instance identity mismatch')
    require(value['public_ip'] == imds('public-ipv4') and value['public_ip'] != '15.135.108.24' and
            ipaddress.ip_address(value['public_ip']).is_global, 'Production/non-public target forbidden')
    require(value['domain'] == 'stage-' + value['run_id'] + '.racebib.invalid', 'Staging origin mismatch')
    return value


def run(*args, stdin=None, allow_failure=False):
    environment = dict(os.environ, PATH='/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin')
    for name in ['BASH_ENV', 'ENV', 'PYTHONPATH', 'PYTHONHOME', 'GH_TOKEN', 'GHCR_TOKEN', 'GITHUB_TOKEN']:
        environment.pop(name, None)
    result = subprocess.run(list(args), input=stdin, capture_output=True, timeout=1500, env=environment)
    require(allow_failure or result.returncode == 0, 'Staging command failed; inspect root-only state/logs')
    return result


def install(payload, identity):
    root = Path('/opt/racebib')
    require(not root.exists(), 'Never overwrite an initialized environment; create a fresh rehearsal')
    files = payload['files']
    actual = hashlib.sha256(json.dumps(files, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    require(actual == identity['bundle_digest'], 'Reviewed bundle digest mismatch')
    require(re.fullmatch(r'mysql:8\.4@sha256:[0-9a-f]{64}', payload['mysql_image']), 'Immutable MySQL 8.4 image required')
    for name, data in files.items():
        allowed = name in ('compose.database.yml', 'compose.production.yml') or re.fullmatch(r'deploy/scripts/[A-Za-z0-9_-]+\.(py|sh)', name) or re.fullmatch(r'deploy/nginx/[A-Za-z0-9_.-]+\.conf(?:\.example)?', name)
        require(allowed and len(data) < 300000, 'Unexpected source file')
        target = root / (name.removeprefix('deploy/'))
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(base64.b64decode(data, validate=True))
        target.chmod(0o755 if name.endswith('.sh') else 0o644)
    root.chmod(0o755)
    for name in ['state', 'backups']:
        (root / name).mkdir(mode=0o700)
    lines = {'MYSQL_DATABASE': 'racebib_stage', 'MYSQL_USER': 'racebib_stage', 'MYSQL_PASSWORD': secrets.token_hex(32),
             'MYSQL_ROOT_PASSWORD': secrets.token_hex(32), 'JWT_SIGNING_KEY': secrets.token_hex(32),
             'MYSQL_IMAGE': payload['mysql_image'], 'APPLICATION_BASE_URL': 'https://' + identity['domain'],
             'DEPLOYMENT_ACCESS_MODE': 'https', 'JWT_ISSUER': 'RaceBib.Staging', 'JWT_AUDIENCE': 'RaceBib.Staging.Clients'}
    (root / '.env').write_text(''.join(key + '=' + value + '\n' for key, value in lines.items()))
    (root / '.env').chmod(0o600)
    auth = payload['github_auth']
    require(set(auth) == {'github_token', 'ghcr_token', 'ghcr_username'} and all(isinstance(v, str) and v for v in auth.values()), 'Staging read credentials required')
    (root / 'github-auth.json').write_text(json.dumps(auth))
    (root / 'github-auth.json').chmod(0o600)
    run('docker', 'login', 'ghcr.io', '--username', auth['ghcr_username'], '--password-stdin', stdin=auth['ghcr_token'].encode())
    for source, destination in [('ssh-dispatch.sh', '/usr/local/sbin/racebib-ssh'), ('ssh-deploy.sh', '/usr/local/sbin/racebib-ssh-deploy')]:
        run('install', '-o', 'root', '-g', 'root', '-m', '0755', str(root / 'scripts' / source), destination)
    template = (root / 'nginx/racebib-https.conf.example').read_text().replace('__DOMAIN__', identity['domain'])
    Path('/etc/nginx/sites-enabled/default').unlink(missing_ok=True)
    Path('/etc/nginx/sites-enabled/racebib').write_text(template)
    run('nginx', '-t')
    run('systemctl', 'reload', 'nginx')
    # Marker is already present and each control operation is bound to this IMDS identity.
    (Path('/etc/racebib-staging') / 'installed').write_text(actual)


def state():
    path = Path('/opt/racebib/state/release-state.json')
    return json.loads(path.read_text())


FAILURE_ID = 'racebib-stage-backup-v1'
PROOF_PATH = Path('/etc/racebib-staging/synthetic-failure.json')
PROBE_PATH = Path('/etc/racebib-staging/barrier-probe.json')
LOG_DIRECTORY = Path('/opt/racebib/state')
BARRIER_PROTOCOL = 'racebib-stage-barrier-v2'
LOG_NAME = re.compile(r'ssh-deployment-[a-z0-9_]{8}\.log')
BARRIER_ERROR = 'release.SafetyError: Persistent maintenance barrier exists. Use recover.sh after investigation; never delete state to bypass it'


def state_digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def inject_backup_failure(ops, verified, identity, nonce, proof_path=PROOF_PATH):
    """Only this exact exception instance can authorize the later staging recovery."""
    from release_state import atomic_json
    require(re.fullmatch('[0-9a-f]{32}', nonce or ''), 'A fresh failure challenge is required')
    require(ops.state.value['inflight'] is None and ops.state.value['current'], 'Successful idle baseline required')
    require(not proof_path.exists(), 'Previous failure evidence must not be reused')
    before = copy.deepcopy(ops.state.value)
    class SyntheticBackupFailure(Exception):
        pass
    sentinel = SyntheticBackupFailure(FAILURE_ID)
    called = 0
    original = ops.backup
    original_stop = ops.stop_writers
    stop_errors = []
    def stop_writers():
        try:
            return original_stop()
        except BaseException as error:
            stop_errors.append(type(error).__name__)
            raise
    def fail_backup():
        nonlocal called
        called += 1
        require(ops.state.value['inflight']['stage'] == 'writers-stopped', 'Unexpected injection boundary')
        raise sentinel
    ops.backup = fail_backup
    ops.stop_writers = stop_writers
    try:
        try:
            ops.deploy(verified)
        except SyntheticBackupFailure as error:
            require(error is sentinel and called == 1, 'Exact injected failure not confirmed')
        else:
            raise RuntimeError('Expected synthetic failure did not occur')
    finally:
        ops.backup = original
        ops.stop_writers = original_stop
    after = ops.state.value
    require(not stop_errors and after['current'] == before['current'] and after['history'] == before['history'] and
            after['inflight']['stage'] == 'writers-stopped', 'Failure-state safeguards did not hold')
    proof = {'failure_id': FAILURE_ID, 'nonce': nonce, 'run_id': identity['run_id'],
             'instance_id': identity['instance_id'], 'source_sha': verified['manifest']['source_sha'],
             'release_tag': verified.get('release_tag') or 'racebib-' + verified['manifest']['source_sha'],
             'state_digest': state_digest(after)}
    atomic_json(proof_path, proof)
    return proof


def proof_claim_path(proof_path, nonce):
    require(re.fullmatch('[0-9a-f]{32}', nonce or ''), 'Valid recovery nonce required')
    return proof_path.parent / ('recovery-claim-'+nonce+'.json')


def _file_identity(info):
    return [info.st_dev, info.st_ino, info.st_uid, info.st_mode, info.st_nlink,
            info.st_size, info.st_mtime_ns, info.st_ctime_ns]


def _proof_snapshot(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor, 'rb') as stream:
        before = os.fstat(stream.fileno())
        require(stat.S_ISREG(before.st_mode) and before.st_uid == 0 and
                stat.S_IMODE(before.st_mode) == 0o600 and before.st_nlink == 1, 'Private root-owned proof required')
        raw = stream.read(65537)
        after = os.fstat(stream.fileno())
        require(len(raw) <= 65536 and _file_identity(before) == _file_identity(after) ==
                _file_identity(os.stat(path, follow_symlinks=False)), 'Proof changed during descriptor read')
    return json.loads(raw), _file_identity(after)+[hashlib.sha256(raw).hexdigest()]


def read_failure_proof(identity, source, nonce, proof_path=PROOF_PATH, release_tag=''):
    claim = proof_claim_path(proof_path, nonce)
    require(not os.path.lexists(claim), 'Recovery authorization already claimed; never retry it')
    proof, _ = _proof_snapshot(proof_path)
    require(proof == {'failure_id': FAILURE_ID, 'nonce': nonce, 'run_id': identity['run_id'],
                      'instance_id': identity['instance_id'], 'source_sha': source,
                      'release_tag': release_tag or 'racebib-' + source,
                      'state_digest': state_digest(state())}, 'Failure proof or barrier changed; manual investigation required')
    return proof


def _claim_failure_proof(identity, source, nonce, proof_path=PROOF_PATH, release_tag='', expected_snapshot=None):
    """Caller owns the engine lock. Never rename, delete or overwrite the proof."""
    proof = read_failure_proof(identity, source, nonce, proof_path, release_tag)
    current, snapshot = _proof_snapshot(proof_path)
    require(current == proof and (expected_snapshot is None or snapshot == expected_snapshot), 'Validated proof was replaced')
    claim_path = proof_claim_path(proof_path, nonce)
    claim = {'protocol':'racebib-stage-recovery-claim-v1', 'status':'claimed', 'proof':proof,
             'proof_identity':snapshot, 'retained_state':state(), 'log_name':'staging-recovery-'+nonce+'.log'}
    # An empty/partial claim after a crash is spent too. O_EXCL is the one-use authority.
    descriptor = os.open(claim_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'w', encoding='utf-8') as stream:
        json.dump(claim, stream, sort_keys=True)
        stream.flush(); os.fsync(stream.fileno())
    directory = os.open(claim_path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)
    current, observed = _proof_snapshot(proof_path)
    require(current == proof and observed == snapshot and state_digest(state()) == proof['state_digest'],
            'Proof or state changed during claim; authorization spent, recovery forbidden')
    return claim_path, claim


@contextlib.contextmanager
def _recovery_signals():
    def interrupted(signum, frame):
        raise RuntimeError('Staging recovery interrupted; inspect the spent claim and retained maintenance state')
    previous = {sig:signal.signal(sig, interrupted) for sig in (signal.SIGHUP, signal.SIGTERM, signal.SIGINT)}
    try:
        yield
    finally:
        for sig, handler in previous.items(): signal.signal(sig, handler)


@contextlib.contextmanager
def _recovery_environment():
    previous = os.environ.copy()
    # Force the same local root Docker/auth context; no inherited remote Docker,
    # Python, shell, registry credentials or TLS/proxy overrides may retarget it.
    os.environ.clear()
    os.environ.update(PATH='/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin',
                      HOME='/root', LANG='C.UTF-8', LC_ALL='C.UTF-8')
    try:
        yield
    finally:
        os.environ.clear(); os.environ.update(previous)


def atomic_recover(identity, source, tag, failure_nonce, probe_nonce, root=Path('/opt/racebib'),
                   proof_path=PROOF_PATH, probe_path=PROBE_PATH):
    """One existing engine lock, from attestation validation through recovery completion."""
    from operations import Operations, operation_lock
    from release import verify_release
    from release_state import atomic_json
    require(os.name == 'posix' and os.geteuid() == 0 and root.is_dir() and
            root.stat().st_uid == 0 and not root.stat().st_mode & 0o022, 'Trusted root-owned Linux deployment root required')
    os.umask(0o077)
    # Same non-blocking lock as every production engine operation. No wait, retry or nested subprocess lock.
    with operation_lock(root / '.deployment.lock'), _recovery_signals(), _recovery_environment():
        require_verified_probe(identity, source, tag, failure_nonce, probe_nonce, proof_path, probe_path)
        proof, snapshot = _proof_snapshot(proof_path)
        before = copy.deepcopy(state())
        require(before['inflight'] is not None and state_digest(before) == proof['state_digest'], 'Attested barrier changed')
        claim_path, claim = _claim_failure_proof(identity, source, failure_nonce, proof_path, tag, snapshot)
        result_path = claim_path.with_suffix('.result.json')
        ops = None
        dispatched = False
        try:
            descriptor = os.open(root/'state'/claim['log_name'], os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(descriptor, 'w', encoding='utf-8') as log, contextlib.redirect_stdout(log), contextlib.redirect_stderr(log):
                try:
                    verified = verify_release(source, release_tag=tag or None)
                    require(verified['manifest']['source_sha'] == source and verified['manifest_digest'] ==
                            before['inflight']['target']['manifest_digest'] and
                            (verified.get('release_tag') or 'racebib-'+source) == proof['release_tag'],
                            'Freshly verified release differs from the attested recovery target')
                    current, observed = _proof_snapshot(proof_path)
                    require(current == proof and observed == snapshot and state_digest(state()) == proof['state_digest'],
                            'Claimed proof or barrier changed before recovery dispatch')
                    ops = Operations(root)
                    require(ops.state.value == before, 'Engine state differs from attested state')
                    dispatched = True
                    # Same implementation/arguments as recover.sh without its second main()/lock acquisition.
                    ops.deploy(verified, migrate=False, recovery=True,
                               reason='Verified exact synthetic backup failure and maintenance rejection in isolated staging')
                except BaseException:
                    traceback.print_exc(file=log)
                    raise
            atomic_json(result_path, {'status':'succeeded', 'state_digest':state_digest(state())})
        except BaseException as error:
            if dispatched and ops is not None:
                try: ops.stop_writers()
                except BaseException: pass
                # A late commit/persistence error must not leave an idle success marker.
                try:
                    if state()['inflight'] is None:
                        ops.state.value = copy.deepcopy(before)
                        ops.state.save()
                except BaseException: pass  # Storage failure requires investigation; the immutable claim retains the baseline.
            try:
                atomic_json(result_path, {'status':'failed', 'error_type':type(error).__name__,
                                          'dispatched':dispatched, 'state_digest':state_digest(state())})
            except BaseException: pass
            raise


def read_probe(path=PROBE_PATH):
    require(path.is_file() and not path.is_symlink() and path.stat().st_uid == 0 and
            stat.S_IMODE(path.stat().st_mode) == 0o600, 'Private root-owned probe required')
    return json.loads(path.read_text())


def prepare_barrier_probe(identity, source, tag, failure_nonce, probe_nonce,
                          proof_path=PROOF_PATH, probe_path=PROBE_PATH, logs=LOG_DIRECTORY):
    from release_state import atomic_json
    proof = read_failure_proof(identity, source, failure_nonce, proof_path, tag)
    require(re.fullmatch('[0-9a-f]{32}', probe_nonce or '') and not probe_path.exists(), 'Fresh one-attempt barrier probe required')
    probe = {'phase':'preparing', 'proof':proof, 'probe_nonce':probe_nonce,
             'prior_logs':[p.name for p in logs.glob('ssh-deployment-*.log')]}
    atomic_json(probe_path, probe)
    # Compare filesystem timestamps to a durable filesystem marker, not a higher-
    # resolution userspace clock. Equal timestamps are valid on coarse filesystems;
    # the prior-name snapshot independently rejects every pre-existing log.
    probe.update(phase='prepared', not_before_ns=probe_path.stat().st_mtime_ns)
    atomic_json(probe_path, probe)
    return {'protocol':BARRIER_PROTOCOL, 'phase':'prepared', 'probe_nonce':probe_nonce}


def check_probe(identity, source, tag, failure_nonce, probe_nonce, phase,
                proof_path=PROOF_PATH, probe_path=PROBE_PATH):
    proof = read_failure_proof(identity, source, failure_nonce, proof_path, tag)
    probe = read_probe(probe_path)
    require(probe['phase'] == phase and probe['proof'] == proof and probe['probe_nonce'] == probe_nonce,
            'Probe phase, nonce or retained state changed; recovery forbidden')
    return probe


def verify_barrier_probe(identity, source, tag, failure_nonce, probe_nonce, log_name,
                         proof_path=PROOF_PATH, probe_path=PROBE_PATH, logs=LOG_DIRECTORY):
    """Read only the fresh log named by the real SSH envelope; never return log content."""
    from release_state import atomic_json
    probe = check_probe(identity, source, tag, failure_nonce, probe_nonce, 'prepared', proof_path, probe_path)
    # Burn this verification attempt BEFORE reading the log. Errors cannot be retried as successful probes.
    probe['phase'] = 'checking'
    atomic_json(probe_path, probe)
    require(isinstance(log_name, str) and LOG_NAME.fullmatch(log_name) and log_name not in probe['prior_logs'], 'Fresh fixed-format SSH log required')
    descriptor = os.open(logs / log_name, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor, 'rb') as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and info.st_uid == 0 and stat.S_IMODE(info.st_mode) == 0o600 and
                info.st_nlink == 1 and info.st_mtime_ns >= probe['not_before_ns'] and info.st_ctime_ns >= probe['not_before_ns'],
                'SSH log ownership/freshness mismatch')
        raw = stream.read(131073)
    require(len(raw) <= 131072, 'Unexpected SSH log size')
    text = raw.decode('utf-8')
    # The exact terminal exception alone is insufficient: require the real guard's ordered stack frames.
    frames = re.findall(r'^  File "([^"]+)", line [0-9]+, in ([A-Za-z_][A-Za-z_0-9]*)$', text, re.M)
    require(text.count('Traceback (most recent call last):') == 1 and text.endswith(BARRIER_ERROR+'\n') and
            'During handling of the above exception' not in text and 'The above exception was the direct cause' not in text and
            frames[-3:] == [('/opt/racebib/scripts/operations.py','deploy'),
                            ('/opt/racebib/scripts/release_state.py','require_idle'),
                            ('/opt/racebib/scripts/release.py','require')],
            'Log does not establish the actual maintenance guard rejection')
    # Re-read the synthetic proof/state after log inspection, before publishing evidence.
    proof = read_failure_proof(identity, source, failure_nonce, proof_path, tag)
    receipt = {'protocol':BARRIER_PROTOCOL, 'deployment':'rejected', 'reason':'maintenance-barrier',
               'run_id':identity['run_id'], 'instance_id':identity['instance_id'], 'source_sha':source,
               'release_tag':tag or 'racebib-'+source, 'failure_nonce':failure_nonce, 'probe_nonce':probe_nonce,
               'state_digest':proof['state_digest'], 'log_name':log_name, 'log_digest':hashlib.sha256(raw).hexdigest()}
    probe.update(phase='verified', receipt=receipt)
    atomic_json(probe_path, probe)
    return receipt


def require_verified_probe(identity, source, tag, failure_nonce, probe_nonce,
                           proof_path=PROOF_PATH, probe_path=PROBE_PATH):
    probe = check_probe(identity, source, tag, failure_nonce, probe_nonce, 'verified', proof_path, probe_path)
    require(probe['receipt']['protocol'] == BARRIER_PROTOCOL and probe['receipt']['state_digest'] == probe['proof']['state_digest'],
            'Verified maintenance rejection required before recovery')


def main():
    require(os.environ.get('SSH_ORIGINAL_COMMAND') == 'stage-control', 'Only the staging control protocol is permitted')
    os.umask(0o077)
    raw = sys.stdin.buffer.read(3 * 1024 * 1024 + 1)
    require(len(raw) <= 3 * 1024 * 1024, 'Payload exceeds limit')
    payload = json.loads(raw)
    identity = guard(payload)
    action = payload['action']
    if action == 'install':
        install(payload, identity)
        print(json.dumps({'installed': True, 'run_id': identity['run_id']}))
        return
    require(Path('/etc/racebib-staging/installed').read_text() == identity['bundle_digest'], 'Reviewed installation required')
    source, tag = payload.get('source_sha', ''), payload.get('release_tag', '')
    require(re.fullmatch('[0-9a-f]{40}', source) and (not tag or re.fullmatch('racebib-' + source + '-recovery-[1-9][0-9]*-[1-9][0-9]*', tag)), 'Invalid release selector')
    selector = [source] + (['--release-tag', tag] if tag else [])
    scripts = '/opt/racebib/scripts/'
    sys.path.insert(0, scripts)
    evidence = None
    if action in ('prepare-barrier-probe', 'verify-barrier-probe'):
        from operations import operation_lock
        with operation_lock(Path('/opt/racebib/.deployment.lock')):
            arguments = (identity, source, tag, payload.get('failure_nonce'), payload.get('probe_nonce'))
            result = (prepare_barrier_probe(*arguments) if action == 'prepare-barrier-probe' else
                      verify_barrier_probe(*arguments, payload.get('log_name')))
        print(json.dumps(result))
        return
    if action == 'initialize':
        require(not Path('/opt/racebib/state/database.json').exists(), 'Fresh database initialization only')
        run('/bin/bash', scripts + 'database-init.sh')
        # Explicit staging-only migration approval is part of the reviewed rehearsal.
        run('/bin/bash', scripts + 'recover.sh', *selector, '--resume-migrations', '--reason', 'Initialize isolated synthetic staging schema for approved rehearsal')
    elif action == 'arm-failure':
        # Use the real engine with its real lock, but inject only this call's backup boundary.
        # This stops writers, durably records a barrier, fails backup, and retains last success.
        sys.path.insert(0, scripts)
        from operations import Operations, operation_lock
        from release import verify_release
        with operation_lock(Path('/opt/racebib/.deployment.lock')):
            ops = Operations()
            evidence = inject_backup_failure(ops, verify_release(source, release_tag=tag or None), identity, payload.get('failure_nonce'))
    elif action == 'recover':
        atomic_recover(identity, source, tag, payload.get('failure_nonce'), payload.get('probe_nonce'))
    elif action != 'inspect':
        raise RuntimeError('Unknown staging action')
    value = state()
    current = value['current']
    ready = False
    frontend = False
    if value['inflight'] is None and current:
        sys.path.insert(0, scripts)
        from operations import Operations
        ops = Operations()
        ops.bind(current)
        readiness = json.loads(ops.dc('exec', '-T', 'api', 'curl', '-fsS', '--max-time', '30', 'http://127.0.0.1:8080/health/ready'))
        ready = readiness.get('status') == 'ready' and readiness.get('schemaHash') == value['history'][-1]['schema_hash']
        frontend = b'<div id="app"' in run('curl', '-fsS', '--max-time', '30', 'http://127.0.0.1:8080/').stdout
        require(ready and frontend, 'Internal API/frontend readiness failed')
    print(json.dumps({'run_id': identity['run_id'], 'maintenance': value['inflight'] is not None,
                      'current_sha': current['manifest']['source_sha'] if current else None,
                      'manifest_digest': current['manifest_digest'] if current else None, 'history_count': len(value['history']),
                      'api_ready': ready, 'frontend_ready': frontend, 'synthetic_failure': evidence}))


if __name__ == '__main__':
    try:
        main()
    except Exception:
        print('Staging control failed; inspect only this run and preserve maintenance state.', file=sys.stderr)
        sys.exit(1)
