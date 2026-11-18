"""Actual SSH entry-point integration rehearsal, in a disposable root Linux container only.

Only ssh_entry.run_engine is mocked. Files, UID checks, log announcements, error
handling, receipts and the real Operations/ReleaseState maintenance guard run unchanged.
No network, Docker socket, AWS, production state or application deployment is used.
"""
import argparse
import contextlib
import copy
import io
import json
import os
from pathlib import Path
import shutil
import sys
from types import SimpleNamespace
import traceback
from unittest.mock import patch


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--emit-fixtures', action='store_true')
    args = parser.parse_args()
    root = Path('/opt/racebib')
    control = Path('/etc/racebib-staging')
    if not (Path('/.dockerenv').is_file() and os.geteuid() == 0 and
            os.environ.get('RACEBIB_BARRIER_FIXTURE') == '1' and not root.exists() and not control.exists()):
        raise RuntimeError('Use only the documented fresh disposable root container; existing paths are forbidden')
    os.umask(0o077)
    repo = Path(__file__).resolve().parents[2]
    shutil.copytree(repo/'deploy/scripts',root/'scripts')
    (root/'state').mkdir(); control.mkdir()
    sys.path.insert(0,str(root/'scripts'))
    sys.path.insert(0,str(repo/'deploy/staging'))
    import ssh_entry
    from operations import Operations
    from operations import operation_lock
    from release import SafetyError
    from release_state import atomic_json
    import host_control as stage
    source = 'a'*40
    identity = {'run_id':'b'*32, 'instance_id':'i-'+'a'*17}
    failure_nonce, probe_nonce = 'e'*32, 'f'*32
    release = {'manifest':{'source_sha':source,'api':'ghcr.io/vreboton/racebib-api@sha256:'+'c'*64,
                           'web':'ghcr.io/vreboton/racebib-web@sha256:'+'d'*64},'manifest_digest':'sha256:'+'c'*64}
    initial = {'format':1,'current':release,'previous':None,'history':[{'release':release,'schema_hash':'e'*64}],
               'inflight':{'kind':'deployment','stage':'writers-stopped','target':release}}
    fixtures = []
    scenarios = ['maintenance','unrelated','generic','spoofed-terminal','success','stale-log','symlink','wrong-mode','changed-state','old-log','same-message-wrong-origin','engine-exception']
    for scenario in scenarios:
        stage.PROBE_PATH.unlink(missing_ok=True)
        # Reset only this fixture's spent claim inside the fresh disposable container.
        stage.proof_claim_path(stage.PROOF_PATH,failure_nonce).unlink(missing_ok=True)
        atomic_json(root/'state/release-state.json',copy.deepcopy(initial))
        proof = {'failure_id':stage.FAILURE_ID,'nonce':failure_nonce,**identity,'source_sha':source,
                 'release_tag':'racebib-'+source,'state_digest':stage.state_digest(initial)}
        atomic_json(stage.PROOF_PATH,proof)  # Synthetic pre-existing failure-state fixture; generation is unit-tested separately.
        prior = list((root/'state').glob('ssh-deployment-*.log'))
        stage.prepare_barrier_probe(identity,source,'',failure_nonce,probe_nonce)
        def engine(command, log, environment):
            assert command == ['/bin/bash','/opt/racebib/scripts/deploy.sh',source]
            if scenario == 'engine-exception': raise TimeoutError('Synthetic engine timeout before any guard evidence')
            if scenario == 'success':
                value=copy.deepcopy(initial); value['inflight']=None
                atomic_json(root/'state/release-state.json',value)
                return SimpleNamespace(returncode=0)
            if scenario == 'generic':
                log.write('Deployment unconfirmed; inspect host maintenance state and root-only logs before retrying.\n')
                return SimpleNamespace(returncode=1)
            if scenario == 'spoofed-terminal':
                log.write('Traceback (most recent call last):\n  File "/tmp/not-the-guard.py", line 1, in main\n'+stage.BARRIER_ERROR+'\n')
                return SimpleNamespace(returncode=1)
            try:
                if scenario == 'unrelated': raise SafetyError('Unrelated provenance/deployment failure')
                if scenario == 'same-message-wrong-origin': raise SafetyError(stage.BARRIER_ERROR.removeprefix('release.SafetyError: '))
                Operations().deploy(release)  # Real require_idle must throw before Docker, images or backup can be touched.
            except SafetyError:
                traceback.print_exc(file=log)
                return SimpleNamespace(returncode=1)
            raise AssertionError('Maintenance barrier was bypassed')
        out, err = io.StringIO(), io.StringIO()
        sys.argv = ['ssh_entry.py',source]
        with patch.object(ssh_entry,'run_engine',side_effect=engine),contextlib.redirect_stdout(out),contextlib.redirect_stderr(err):
            try:
                ssh_entry.main(); code=0
            except SystemExit as error:
                code=error.code
        result={'ExitCode':code,'Stdout':out.getvalue(),'Stderr':err.getvalue()}
        log_name = result['Stdout'].splitlines()[0].removeprefix('Root-only deployment log: ')
        log_path = root/'state'/log_name
        assert stage.LOG_NAME.fullmatch(log_name) and log_path.is_file()
        if scenario == 'stale-log': os.utime(log_path,ns=(1,1))
        if scenario == 'symlink':
            target = log_path.with_suffix('.private'); log_path.rename(target); log_path.symlink_to(target)
        if scenario == 'wrong-mode': log_path.chmod(0o644)
        if scenario == 'changed-state':
            value=copy.deepcopy(initial); value['inflight']['stage']='unexpected-stage'; atomic_json(root/'state/release-state.json',value)
        if scenario == 'old-log': log_name = prior[0].name
        receipt = None
        try:
            receipt=stage.verify_barrier_probe(identity,source,'',failure_nonce,probe_nonce,log_name)
        except (RuntimeError,OSError):
            if scenario == 'maintenance': raise
        assert (receipt is not None) == (scenario == 'maintenance'), scenario
        if receipt:
            stage.require_verified_probe(identity,source,'',failure_nonce,probe_nonce)
            try: stage.prepare_barrier_probe(identity,source,'',failure_nonce,probe_nonce)
            except RuntimeError: pass
            else: raise AssertionError('A second barrier probe was prepared for the same failure')
            # Verified evidence cannot be generated again, even before single-use failure proof consumption.
            try: stage.verify_barrier_probe(identity,source,'',failure_nonce,probe_nonce,log_name)
            except RuntimeError: pass
            else: raise AssertionError('Probe replay accepted')
            with operation_lock(root/'.deployment.lock'):
                stage._claim_failure_proof(identity,source,failure_nonce)
            try: stage.require_verified_probe(identity,source,'',failure_nonce,probe_nonce)
            except RuntimeError: pass
            else: raise AssertionError('Consumed synthetic proof could authorize another recovery')
        else:
            try: stage.require_verified_probe(identity,source,'',failure_nonce,probe_nonce)
            except RuntimeError: pass
            else: raise AssertionError('Unverified failure could authorize recovery')
        # Stable, synthetic cross-platform envelope fixtures for the Windows classifier.
        announced = result['Stdout'].splitlines()[0].removeprefix('Root-only deployment log: ')
        result['Stdout']=result['Stdout'].replace(announced,'ssh-deployment-fixture0.log')
        if receipt: receipt['log_name']='ssh-deployment-fixture0.log'
        fixtures.append({'scenario':scenario,'result':result,'proof':proof,'probe_nonce':probe_nonce,'receipt':receipt})
    if args.emit_fixtures:
        print(json.dumps(fixtures,indent=2))
    else:
        expected=json.loads((repo/'deploy/tests/fixtures/staging-barrier-entry.json').read_text(encoding='utf-8-sig'))
        for actual,saved in zip(fixtures,expected,strict=True):
            # Log digest includes Python traceback line numbers, not a stable wire-protocol field.
            if actual['receipt']: actual['receipt']['log_digest']=saved['receipt']['log_digest']
            assert actual == saved, 'Refresh fixtures from the actual entry point after protocol changes'
        print(f'PASS: {len(fixtures)} actual SSH-entry/real maintenance-guard cases; only engine execution mocked; no network')


if __name__ == '__main__':
    main()
