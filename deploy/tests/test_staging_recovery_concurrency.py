"""Deterministic root/Linux tests: real flock, files, claims and Operations.deploy.

Only release-network verification and application/Docker boundaries are mocked.
Run in the dedicated network-disabled CI container; never select a host deployment.
"""
import copy
import json
import multiprocessing
import os
from pathlib import Path
import signal
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'staging'))
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
import host_control as stage
from operations import Operations, operation_lock
from release import SafetyError
from release_state import atomic_json

SOURCE='a'*40
NONCE='e'*32
PROBE_NONCE='f'*32
IDENTITY={'run_id':'b'*32,'instance_id':'i-'+'a'*17}
RELEASE={'manifest':{'source_sha':SOURCE},'manifest_digest':'sha256:'+'c'*64}


def seed(root, nonce=NONCE, reason='attested synthetic failure'):
    (root/'state').mkdir(parents=True,exist_ok=True,mode=0o700)
    control=root/'control'; control.mkdir(exist_ok=True,mode=0o700)
    value={'format':1,'current':RELEASE,'previous':None,'history':[{'release':RELEASE,'schema_hash':'d'*64}],
           'inflight':{'kind':'deployment','stage':'writers-stopped','target':RELEASE,'reason':reason}}
    atomic_json(root/'state/release-state.json',value)
    proof={'failure_id':stage.FAILURE_ID,**IDENTITY,'nonce':nonce,'source_sha':SOURCE,
           'release_tag':'racebib-'+SOURCE,'state_digest':stage.state_digest(value)}
    atomic_json(control/'proof.json',proof)
    receipt={'protocol':stage.BARRIER_PROTOCOL,'state_digest':proof['state_digest']}
    atomic_json(control/'probe.json',{'phase':'verified','proof':proof,'probe_nonce':PROBE_NONCE,'receipt':receipt})
    return value,proof


def read_state(root):
    return json.loads((root/'state/release-state.json').read_text())


class SimulatedApplication(Operations):
    """Use the actual recovery engine state machine, never Docker, HTTP or MySQL."""
    def __init__(self,root,mode='success',entered=None,resume=None):
        assert os.environ.get('HOME') == '/root' and 'DOCKER_HOST' not in os.environ and 'GH_TOKEN' not in os.environ
        super().__init__(root)
        self.mode=mode; self.entered=entered; self.resume=resume
        if mode=='late-commit':
            original=self.state.commit
            def late_error(*args):
                original(*args)
                raise OSError('Synthetic failure after successful state-file replacement')
            self.state.commit=late_error
    def deploy(self,*args,**kwargs):
        assert kwargs['recovery'] is True and kwargs['migrate'] is False
        with (self.root/'dispatches').open('a') as marker: marker.write('dispatch\n')
        return super().deploy(*args,**kwargs)
    def bind(self,verified): self.target=verified
    def db_configuration(self): return {}
    def db_baseline(self,config): pass
    def db_runtime(self,config): pass
    def no_oneoff(self): pass
    def application_configuration(self): pass
    def pull(self): pass
    def stop_writers(self): (self.root/'writers-stopped').touch()
    def backup(self):
        # Event ordering, not sleeps, determines the interleaving.
        if self.entered:
            self.entered.set()
            if not self.resume.wait(10): raise TimeoutError('Test harness deadline')
        if self.mode=='failure': raise SafetyError('Synthetic recovery backup failure')
        if self.mode=='signal': os.kill(os.getpid(),signal.SIGTERM)
        return str(self.root/'synthetic.sql.gz')
    def schema(self):
        if self.mode=='timeout': raise TimeoutError('Synthetic schema-command timeout')
        return 'd'*64
    def application_ready(self,schema): pass


def recover(root, nonce=NONCE,mode='success',entered=None,resume=None):
    with patch('host_control.state',side_effect=lambda:read_state(root)), \
         patch('release.verify_release',return_value=copy.deepcopy(RELEASE)), \
         patch('operations.Operations',side_effect=lambda p:SimulatedApplication(p,mode,entered,resume)):
        stage.atomic_recover(IDENTITY,SOURCE,'',nonce,PROBE_NONCE,root,root/'control/proof.json',root/'control/probe.json')


def worker(root,queue,nonce=NONCE,mode='success',entered=None,resume=None,start=None):
    try:
        if start and not start.wait(10): raise TimeoutError('Test request scheduling deadline')
        recover(Path(root),nonce,mode,entered,resume)
        queue.put('success')
    except BaseException as error:
        queue.put(type(error).__name__)


@unittest.skipUnless(os.name=='posix' and os.geteuid()==0,'Run root-only recovery tests in the isolated Linux CI container')
class RecoveryConcurrencyTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='racebib-recovery-test-')
        self.root=Path(self.temp.name)
        self.before,self.proof=seed(self.root)
        self.proof_path=self.root/'control/proof.json'
        self.claim=stage.proof_claim_path(self.proof_path,NONCE)
        self.ctx=multiprocessing.get_context('fork')
        self.children=[]
    def tearDown(self):
        for child in self.children:
            child.join(1)
            if child.is_alive(): child.terminate(); child.join(2)
        self.temp.cleanup()
    def launch(self,*args,**kwargs):
        child=self.ctx.Process(target=worker,args=(str(self.root),*args),kwargs=kwargs)
        child.start(); self.children.append(child); return child
    def assert_no_dispatch(self): self.assertFalse((self.root/'dispatches').exists())
    def test_two_authorized_recoveries_contend_then_replay_is_denied(self):
        entered,resume=self.ctx.Event(),self.ctx.Event(); first,second=self.ctx.Queue(),self.ctx.Queue()
        self.launch(first,entered=entered,resume=resume)
        self.assertTrue(entered.wait(10),'First recovery must hold the real engine lock')
        self.launch(second)
        self.assertEqual(second.get(timeout=10),'SafetyError')
        self.assertEqual((self.root/'dispatches').read_text(),'dispatch\n')
        self.assertIsNotNone(read_state(self.root)['inflight'])
        resume.set(); self.assertEqual(first.get(timeout=10),'success')
        with self.assertRaises(RuntimeError): recover(self.root)
        self.assertEqual((self.root/'dispatches').read_text(),'dispatch\n')
        self.assertEqual(json.loads(self.proof_path.read_text()),self.proof)
        self.assertEqual(len(read_state(self.root)['history']),2)
    def test_replacement_inode_after_validation_is_not_claimed(self):
        original=stage._claim_failure_proof
        def replace(*args,**kwargs):
            atomic_json(self.proof_path,self.proof)  # Same bytes, new inode.
            return original(*args,**kwargs)
        with patch('host_control._claim_failure_proof',side_effect=replace),self.assertRaises(RuntimeError): recover(self.root)
        self.assert_no_dispatch(); self.assertFalse(self.claim.exists())
        self.assertEqual(json.loads(self.proof_path.read_text()),self.proof)
    def test_replacement_during_exclusive_claim_is_never_deleted(self):
        original_open=os.open
        replacement=dict(self.proof,nonce='1'*32)
        def race(path,flags,mode=0o777,*,dir_fd=None):
            descriptor=original_open(path,flags,mode,dir_fd=dir_fd)
            if Path(path)==self.claim and flags & os.O_EXCL:
                atomic_json(self.proof_path,replacement)
            return descriptor
        with patch('host_control.os.open',side_effect=race),self.assertRaises(RuntimeError): recover(self.root)
        self.assert_no_dispatch(); self.assertTrue(self.claim.exists())
        self.assertEqual(json.loads(self.proof_path.read_text()),replacement)
        self.assertEqual(read_state(self.root),self.before)
    def test_state_mutation_during_claim_handoff_stops_before_dispatch(self):
        changed=copy.deepcopy(self.before); changed['inflight']['reason']='different maintenance event'
        def changed_release(*args,**kwargs):
            atomic_json(self.root/'state/release-state.json',changed)
            return copy.deepcopy(RELEASE)
        with patch('host_control.state',side_effect=lambda:read_state(self.root)), \
             patch('release.verify_release',side_effect=changed_release), \
             patch('operations.Operations') as factory,self.assertRaises(RuntimeError):
            stage.atomic_recover(IDENTITY,SOURCE,'',NONCE,PROBE_NONCE,self.root,self.proof_path,self.root/'control/probe.json')
        factory.assert_not_called(); self.assert_no_dispatch()
        self.assertEqual(read_state(self.root),changed); self.assertTrue(self.proof_path.exists())
        self.assertTrue(self.claim.exists())
    def test_inplace_proof_change_after_claim_is_detected_without_deletion(self):
        replacement=dict(self.proof,nonce='1'*32)
        inode=self.proof_path.stat().st_ino
        def change(*args,**kwargs):
            self.proof_path.write_text(json.dumps(replacement))
            return copy.deepcopy(RELEASE)
        with patch('host_control.state',side_effect=lambda:read_state(self.root)), \
             patch('release.verify_release',side_effect=change),self.assertRaises(RuntimeError):
            stage.atomic_recover(IDENTITY,SOURCE,'',NONCE,PROBE_NONCE,self.root,self.proof_path,self.root/'control/probe.json')
        self.assert_no_dispatch(); self.assertTrue(self.claim.exists())
        self.assertEqual(self.proof_path.stat().st_ino,inode)
        self.assertEqual(json.loads(self.proof_path.read_text()),replacement)
    def test_engine_environment_cannot_inherit_remote_docker_or_credentials(self):
        with patch.dict(os.environ,{'DOCKER_HOST':'must-not-be-used','GH_TOKEN':'synthetic-never-logged'}):
            recover(self.root)
            self.assertEqual(os.environ['DOCKER_HOST'],'must-not-be-used')
            self.assertEqual(os.environ['GH_TOKEN'],'synthetic-never-logged')
        self.assertIsNone(read_state(self.root)['inflight'])
    def test_old_scheduled_request_cannot_claim_a_new_barrier(self):
        start=self.ctx.Event(); queue=self.ctx.Queue()
        self.launch(queue,start=start)
        with operation_lock(self.root/'.deployment.lock'):
            newer,proof=seed(self.root,nonce='1'*32,reason='new maintenance barrier while old request is pending')
        start.set()
        self.assertEqual(queue.get(timeout=10),'RuntimeError')
        self.assert_no_dispatch(); self.assertFalse(self.claim.exists())
        self.assertEqual(json.loads(self.proof_path.read_text()),proof)
        self.assertEqual(read_state(self.root),newer)
    def test_failures_after_claim_preserve_barrier_and_spend_authorization(self):
        with self.assertRaises(SafetyError): recover(self.root,mode='failure')
        self.assertTrue(self.claim.exists()); self.assertTrue(self.proof_path.exists())
        self.assertIsNotNone(read_state(self.root)['inflight'])
        self.assertEqual(read_state(self.root)['history'],self.before['history'])
        result=json.loads(self.claim.with_suffix('.result.json').read_text())
        self.assertEqual(result['status'],'failed'); self.assertTrue(result['dispatched'])
        with self.assertRaises(RuntimeError): recover(self.root)
        self.assertEqual((self.root/'dispatches').read_text(),'dispatch\n')
    def test_lock_contention_is_fail_fast_without_consuming_proof(self):
        queue=self.ctx.Queue()
        with operation_lock(self.root/'.deployment.lock'):
            self.launch(queue)
            self.assertEqual(queue.get(timeout=10),'SafetyError')
        self.assert_no_dispatch(); self.assertFalse(self.claim.exists())
        self.assertEqual(json.loads(self.proof_path.read_text()),self.proof)
    def test_command_timeout_leaves_claim_spent_and_barrier_intact(self):
        with self.assertRaises(TimeoutError): recover(self.root,mode='timeout')
        self.assertIsNotNone(read_state(self.root)['inflight'])
        self.assertEqual(json.loads(self.claim.with_suffix('.result.json').read_text())['error_type'],'TimeoutError')
        with self.assertRaises(RuntimeError): recover(self.root)
    def test_process_interruption_retains_claim_and_barrier(self):
        queue=self.ctx.Queue(); self.launch(queue,mode='signal')
        self.assertEqual(queue.get(timeout=10),'RuntimeError')
        self.assertTrue(self.claim.exists()); self.assertIsNotNone(read_state(self.root)['inflight'])
        with self.assertRaises(RuntimeError): recover(self.root)
    def test_late_commit_error_reinstates_the_attested_barrier(self):
        with self.assertRaises(OSError): recover(self.root,mode='late-commit')
        self.assertEqual(read_state(self.root),self.before)
        self.assertTrue((self.root/'writers-stopped').exists()); self.assertTrue(self.claim.exists())
        with self.assertRaises(RuntimeError): recover(self.root)


if __name__=='__main__': unittest.main()
