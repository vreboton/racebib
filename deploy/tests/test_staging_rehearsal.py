import base64
import copy
import contextlib
import gzip
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'staging'))
import infrastructure as infra
import host_control


class StagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.directory = Path(self.temp.name)
        self.record = infra.plan(self.directory, '111122223333', 'staging-only', '8.8.8.8/32', 'ami-'+'a'*17,
                                 'ssh-ed25519 AAAA bootstrap', 'ssh-ed25519 AAAB deploy', protected_ids=['i-'+'f'*17,'vpc-'+'f'*17])
        self.execution_id = 'c' * 32
        (self.directory / 'execution.json').write_text(json.dumps({'format': 1, 'run_id': self.record['run_id'], 'execution_id': self.execution_id}))
        self.arn = 'arn:aws:cloudformation:ap-southeast-2:111122223333:stack/'+self.record['stack_name']+'/stack-guid'
        self.host = {'InstanceId':'i-'+'a'*17, 'ImageId':self.record['ami'], 'VpcId':'vpc-stage', 'SubnetId':'subnet-stage',
                     'SecurityGroups':[{'GroupId':'sg-stage'}], 'PublicIpAddress':'8.8.4.4', 'State':{'Name':'running'},
                     'Tags':self.tags('Host'),
                     'BlockDeviceMappings':[{'Ebs':{'VolumeId':'vol-stage','DeleteOnTermination':True}}], 'NetworkInterfaces':[{'NetworkInterfaceId':'eni-stage'}]}
        self.calls = []

    def tags(self, logical=None):
        value = {'StageRun':self.record['run_id'],'Environment':'racebib-staging','ManagedBy':'racebib-rehearsal','Temporary':'true'}
        if logical: value.update({'aws:cloudformation:stack-id':self.arn,'aws:cloudformation:logical-id':logical})
        return [{'Key':k,'Value':v} for k,v in value.items()]

    def ids(self):
        return {'Network':'vpc-stage','Subnet':'subnet-stage','Firewall':'sg-stage','Host':self.host['InstanceId'],
                'Gateway':'igw-stage','Routes':'rtb-stage','Attachment':'attachment-stage','DefaultRoute':'route-stage','SubnetRoutes':'rtbassoc-stage'}

    def tearDown(self):
        self.temp.cleanup()

    def client(self, record, service, *args, **kwargs):
        self.calls.append((service,args))
        operation = args[0]
        if operation == 'get-caller-identity': return {'Account':record['account']}
        if operation == 'describe-images': return {'Images':[{'State':'available','Architecture':'x86_64','Name':'ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-amd64-server-test'}]}
        if operation == 'create-stack': return {'StackId':self.arn}
        if operation == 'wait': return {}
        if operation == 'describe-stacks': return {'Stacks':[{'StackId':self.arn,'StackStatus':'CREATE_COMPLETE','Tags':self.tags()}]}
        if operation == 'get-template': return {'TemplateBody':record['template']}
        if operation == 'list-stack-resources':
            return {'StackResourceSummaries':[{'LogicalResourceId':name,'PhysicalResourceId':self.ids()[name], 'ResourceStatus':'CREATE_COMPLETE', 'ResourceType':value['Type']} for name,value in record['template']['Resources'].items()]}
        if operation == 'describe-stack-events':
            return {'StackEvents':[{'LogicalResourceId':name,'PhysicalResourceId':physical,'ResourceStatus':'CREATE_COMPLETE'} for name,physical in self.ids().items()]}
        if operation == 'describe-vpcs': return {'Vpcs':[{'VpcId':'vpc-stage','OwnerId':record['account'],'CidrBlock':'10.245.0.0/24','Tags':self.tags('Network')}]}
        if operation == 'describe-subnets': return {'Subnets':[{'SubnetId':'subnet-stage','VpcId':'vpc-stage','OwnerId':record['account'],'Tags':self.tags('Subnet')}]}
        if operation == 'describe-security-groups': return {'SecurityGroups':[{'GroupId':'sg-stage','VpcId':'vpc-stage','OwnerId':record['account'],'Tags':self.tags('Firewall')}]}
        if operation == 'describe-route-tables': return {'RouteTables':[{'RouteTableId':'rtb-stage','VpcId':'vpc-stage','OwnerId':record['account'],'Tags':self.tags('Routes'),
             'Routes':[{'DestinationCidrBlock':'0.0.0.0/0','GatewayId':'igw-stage'}], 'Associations':[{'RouteTableAssociationId':'rtbassoc-stage','SubnetId':'subnet-stage'}]}]}
        if operation == 'describe-internet-gateways': return {'InternetGateways':[{'InternetGatewayId':'igw-stage','OwnerId':record['account'],'Attachments':[{'VpcId':'vpc-stage'}],'Tags':self.tags('Gateway')}]}
        if operation == 'describe-volumes': return {'Volumes':[{'VolumeId':'vol-stage','Encrypted':True,'Attachments':[{'InstanceId':self.host['InstanceId']}],'Tags':self.tags()}]}
        if operation == 'describe-network-interfaces': return {'NetworkInterfaces':[{'NetworkInterfaceId':'eni-stage','OwnerId':record['account'],'VpcId':'vpc-stage','SubnetId':'subnet-stage','Attachment':{'InstanceId':self.host['InstanceId']}}]}
        if operation == 'describe-instances': return {'Reservations':[{'Instances':[self.host]}]}
        if operation == 'get-console-output':
            value = {'instance_id':self.host['InstanceId'],'public_ip':self.host['PublicIpAddress'],'run_id':record['run_id'],
                     'domain':'stage-'+record['run_id']+'.racebib.invalid','host_key':'ssh-ed25519 AAAA', 'ca_pem':'Y2E='}
            return {'Output':base64.b64encode(('RACEBIB_STAGING_TRUST '+json.dumps(value)).encode()).decode()}
        if operation == 'delete-stack': return {}
        raise AssertionError('Unexpected AWS action in local mock: '+operation)

    def submitted(self):
        self.record.update(stack_id=self.arn, creation_submitted=True)
        return self.record

    def test_aws_uses_reviewed_executable_without_github_credentials_or_retries(self):
        self.record['aws_cli'] = '/trusted/aws'
        with patch.dict(os.environ, {'RACEBIB_STAGING_GH_TOKEN':'never-forward', 'GH_TOKEN':'never-forward'}), \
                patch.object(infra.subprocess, 'run') as command:
            command.return_value.returncode = 0
            command.return_value.stdout = '{}'
            infra.aws(self.record, 'sts', 'get-caller-identity')
            args, options = command.call_args
            self.assertEqual(args[0][0], '/trusted/aws')
            self.assertEqual(options['env']['AWS_MAX_ATTEMPTS'], '1')
            self.assertNotIn('RACEBIB_STAGING_GH_TOKEN', options['env'])
            self.assertNotIn('GH_TOKEN', options['env'])

    def test_offline_plan_is_new_isolated_costed_and_contains_no_secrets(self):
        infra.validate_record(self.record)
        self.assertLess(self.record['estimate_usd'], 1)
        resources = self.record['template']['Resources']
        self.assertEqual(len(resources), 9)
        host = resources['Host']['Properties']
        self.assertEqual(host['CreditSpecification']['CPUCredits'], 'standard')
        self.assertNotIn('IamInstanceProfile', host)
        self.assertNotIn('KeyName', host)
        self.assertTrue(host['BlockDeviceMappings'][0]['Ebs']['DeleteOnTermination'])
        compressed = base64.b64decode(host['UserData'])
        self.assertLess(len(compressed), 16384)
        bootstrap = gzip.decompress(compressed).decode()
        self.assertIn('shutdown -h +120', bootstrap)
        self.assertNotIn('@@', bootstrap)
        self.assertNotIn('github_token', bootstrap) # Encoded code is present; credential values are not.
        self.assertEqual(resources['Firewall']['Properties']['SecurityGroupIngress'][0]['CidrIp'], '8.8.8.8/32')

    def test_rejects_broad_ingress_production_ip_unknown_resources_and_template_mutation(self):
        for cidr in ['0.0.0.0/0','8.8.8.0/24','15.135.108.24/32','127.0.0.1/32']:
            value = copy.deepcopy(self.record); value['operator_cidr'] = cidr
            with self.assertRaises(RuntimeError): infra.validate_record(value)
        for mutate in [lambda v: v['template']['Resources']['Host']['Properties'].update(SubnetId='subnet-production'),
                       lambda v: v['template']['Resources']['Firewall']['Properties']['SecurityGroupIngress'][0].update(CidrIp='0.0.0.0/0'),
                       lambda v: v['template']['Resources']['Host'].update(DeletionPolicy='Retain')]:
            value = copy.deepcopy(self.record); mutate(value); value['template_digest']=infra.digest(value['template'])
            with self.assertRaises(RuntimeError): infra.validate_record(value)

    def test_creation_requires_approval_and_fresh_template_and_never_resubmits(self):
        with patch('infrastructure.aws',side_effect=self.client):
            with self.assertRaises(RuntimeError): infra.create(self.directory,self.record,'wrong',self.execution_id)
            self.assertEqual(self.calls,[])
            result = infra.create(self.directory,self.record,self.record['run_id'],self.execution_id)
            self.assertEqual(result['instance_id'],self.host['InstanceId'])
            with self.assertRaises(RuntimeError): infra.create(self.directory,self.record,self.record['run_id'],self.execution_id)
        self.assertEqual(sum(args[0]=='create-stack' for _,args in self.calls),1)

    def test_lost_creation_response_keeps_intent_and_prevents_duplicate_submission(self):
        def fail(record,service,*args,**kwargs):
            if args[0]=='create-stack':
                self.calls.append((service,args)); raise RuntimeError('Synthetic lost response')
            return self.client(record,service,*args,**kwargs)
        with patch('infrastructure.aws',side_effect=fail):
            for _ in range(2):
                with self.assertRaises(RuntimeError): infra.create(self.directory,self.record,self.record['run_id'],self.execution_id)
        self.assertTrue(json.loads((self.directory/'run.json').read_text())['creation_submitted'])
        self.assertEqual(sum(args[0]=='create-stack' for _,args in self.calls),1)

    def test_live_guards_reject_production_wrong_vpc_tags_account_and_template_before_mutation(self):
        for field,value in [('PublicIpAddress','15.135.108.24'),('VpcId','vpc-production')]:
            with self.subTest(field=field):
                old=self.host[field]; self.host[field]=value
                with patch('infrastructure.aws',side_effect=self.client),self.assertRaises(RuntimeError): infra.inspect(self.submitted())
                self.host[field]=old
        for operation,replacement in [('get-caller-identity',{'Account':'999999999999'}),('get-template',{'TemplateBody':{}}),
                                      ('describe-stacks',{'Stacks':[{'StackId':self.arn,'Tags':[]}]} )]:
            def altered(record,service,*args,**kwargs):
                return replacement if args[0]==operation else self.client(record,service,*args,**kwargs)
            with patch('infrastructure.aws',side_effect=altered),self.assertRaises(RuntimeError): infra.destroy(self.directory,self.submitted())
        self.assertFalse(any(args[0]=='delete-stack' for _,args in self.calls))

    def test_console_trust_is_bound_to_stack_instance_nonce_and_ip(self):
        with patch('infrastructure.aws',side_effect=self.client):
            trust = infra.trust(self.submitted())
            self.assertEqual(trust['public_ip'],self.host['PublicIpAddress'])
        def altered(record,service,*args,**kwargs):
            if args[0]=='get-console-output': return {'Output':'RACEBIB_STAGING_TRUST '+json.dumps({'instance_id':'i-'+'f'*17,'run_id':record['run_id'],'public_ip':self.host['PublicIpAddress']})}
            return self.client(record,service,*args,**kwargs)
        with patch('infrastructure.aws',side_effect=altered),self.assertRaises(RuntimeError): infra.trust(self.submitted())

    def test_teardown_only_calls_delete_on_exact_stack_after_identity_checks(self):
        with patch('infrastructure.aws',side_effect=self.client): infra.destroy(self.directory,self.submitted())
        deleted=[args for _,args in self.calls if args[0]=='delete-stack']
        self.assertEqual(deleted,[('delete-stack','--stack-name',self.arn)])

    def test_cleanup_checks_tracked_volumes_even_when_tags_removed(self):
        self.record['target']={'volumes':['vol-stage'],'interfaces':[]}
        def client(record,service,*args,**kwargs):
            if args[0]=='get-caller-identity': return {'Account':record['account']}
            if args[0]=='describe-volumes' and '--volume-ids' in args: return {'Volumes':[{'VolumeId':'vol-stage'}]}
            return {key:[] for key in ['Reservations','Volumes','Addresses','NatGateways','Snapshots','NetworkInterfaces','Vpcs']}
        with patch('infrastructure.aws',side_effect=client),self.assertRaises(RuntimeError): infra.cleanup(self.record)

    def test_host_control_rejects_missing_marker_or_instance_mismatch_before_commands(self):
        marker=self.directory/'identity.json'
        value={'run_id':self.record['run_id'],'instance_id':self.host['InstanceId'],'public_ip':self.host['PublicIpAddress'],
               'domain':'stage-'+self.record['run_id']+'.racebib.invalid'}
        marker.write_text(json.dumps(value)); marker.chmod(0o600)
        root_marker = Mock(wraps=marker)
        root_marker.stat.return_value = SimpleNamespace(st_uid=0, st_mode=0o100600)
        with patch.object(os,'geteuid',return_value=0,create=True),patch('host_control.Path',return_value=root_marker), \
             patch('host_control.imds',side_effect=lambda path: value['instance_id'] if path=='instance-id' else value['public_ip']), \
             patch('host_control.run') as run:
            payload={'run_id':value['run_id'],'instance_id':value['instance_id']}
            self.assertEqual(host_control.guard(payload)['run_id'],value['run_id'])
            with self.assertRaises(RuntimeError): host_control.guard(dict(payload,instance_id='i-'+'f'*17))
            run.assert_not_called()

    def test_iam_template_has_no_iam_dns_production_mutation_or_stack_update_grant(self):
        policy = json.loads((Path(infra.__file__).parent/'provisioner-policy.example.json').read_text(encoding='utf-8-sig'))
        for statement in policy['Statement']:
            actions = statement['Action'] if isinstance(statement['Action'],list) else [statement['Action']]
            self.assertFalse(any(action.startswith(('iam:','route53:','ssm:')) or action=='cloudformation:UpdateStack' for action in actions))
            for action in actions:
                if action.startswith('ec2:') and any(word in action for word in ('Delete','Terminate','Modify','Attach','Detach','Authorize','Revoke','Associate','Disassociate')):
                    tags = statement['Condition']['StringEquals']
                    self.assertEqual(tags.get('ec2:ResourceTag/Environment'),'racebib-staging')
        self.assertTrue(any(s['Resource'].endswith(':vpc/*') and 'ec2:CreateSubnet' in s['Action'] and
                            s['Condition']['StringEquals'].get('ec2:ResourceTag/Environment')=='racebib-staging' for s in policy['Statement'] if isinstance(s['Resource'],str)))
        rendered=json.dumps(policy,separators=(',',':')).replace('ACCOUNT_ID','111122223333').replace('AMI_ID','ami-'+'a'*17)
        self.assertLessEqual(len(rendered),6144, 'Rendered policy must fit the IAM managed-policy limit')

    def test_tag_permission_is_creation_only_and_scoped_to_owned_staging(self):
        policy = json.loads((Path(infra.__file__).parent/'provisioner-policy.example.json').read_text())
        tag = next(s for s in policy['Statement'] if s.get('Sid')=='TagOnlyNewStagingStacks')
        self.assertEqual(tag['Action'],'cloudformation:TagResource')
        self.assertEqual(tag['Resource'],'arn:aws:cloudformation:ap-southeast-2:ACCOUNT_ID:stack/racebib-staging-*/*')
        conditions = tag['Condition']['StringEquals']
        self.assertEqual(conditions['cloudformation:CreateAction'],'CreateStack')
        self.assertEqual(conditions['aws:RequestTag/Environment'],'racebib-staging')
        self.assertEqual(conditions['aws:RequestTag/ManagedBy'],'racebib-rehearsal')
        self.assertIn('aws:RequestTag/StageRun',tag['Condition']['StringLike'])
        with patch('infrastructure.aws',side_effect=self.client):
            infra.create(self.directory,self.record,self.record['run_id'],self.execution_id)
        args = next(args for _,args in self.calls if args[0]=='create-stack')
        for key in ['Environment','StageRun','ManagedBy','Temporary']:
            self.assertTrue(any(a.startswith('Key='+key+',Value=') for a in args))

    def test_teardown_adversarial_inventory_never_deletes(self):
        mutations = {
            'extra': ('list-stack-resources',lambda v:v['StackResourceSummaries'].append(dict(v['StackResourceSummaries'][0],LogicalResourceId='Unexpected'))),
            'duplicate': ('list-stack-resources',lambda v:v['StackResourceSummaries'].append(v['StackResourceSummaries'][0])),
            'missing': ('list-stack-resources',lambda v:v['StackResourceSummaries'].pop()),
            'wrong-type': ('list-stack-resources',lambda v:v['StackResourceSummaries'][0].update(ResourceType='AWS::EC2::Instance')),
            'import': ('describe-stack-events',lambda v:v['StackEvents'][0].update(ResourceStatus='IMPORT_COMPLETE')),
            'unknown-origin': ('describe-stack-events',lambda v:v['StackEvents'].clear()),
            'replacement': ('describe-stack-events',lambda v:v['StackEvents'].append(dict(v['StackEvents'][0],PhysicalResourceId='vpc-other'))),
            'production-host': ('describe-instances',lambda v:v['Reservations'][0]['Instances'][0].update(PublicIpAddress=infra.PRODUCTION_IP)),
            'protected-host': ('describe-instances',lambda v:v['Reservations'][0]['Instances'][0].update(InstanceId=self.record['protected_ids'][0])),
            'protected-vpc': ('describe-vpcs',lambda v:v['Vpcs'][0].update(VpcId=self.record['protected_ids'][1])),
            'wrong-owner': ('describe-vpcs',lambda v:v['Vpcs'][0].update(OwnerId='999999999999')),
            'wrong-tags': ('describe-subnets',lambda v:v['Subnets'][0].update(Tags=[])),
            'foreign-route': ('describe-route-tables',lambda v:v['RouteTables'][0]['Routes'][0].update(GatewayId='igw-production')),
            'foreign-attachment': ('describe-internet-gateways',lambda v:v['InternetGateways'][0]['Attachments'][0].update(VpcId='vpc-production')),
            'foreign-volume': ('describe-volumes',lambda v:v['Volumes'][0]['Attachments'][0].update(InstanceId='i-production')),
            'foreign-interface': ('describe-network-interfaces',lambda v:v['NetworkInterfaces'][0].update(VpcId='vpc-production')),
        }
        for name,(operation,mutate) in mutations.items():
            with self.subTest(name=name):
                record=copy.deepcopy(self.submitted()); self.calls=[]
                def altered(r,service,*args,**kwargs):
                    result=copy.deepcopy(self.client(r,service,*args,**kwargs))
                    if args[0]==operation: mutate(result)
                    return result
                with patch('infrastructure.aws',side_effect=altered), self.assertRaises((RuntimeError,KeyError)):
                    infra.destroy(self.directory,record)
                self.assertFalse(any(args[0]=='delete-stack' for _,args in self.calls))

    def test_observed_replacement_is_rejected_even_with_new_create_evidence(self):
        record=self.submitted()
        with patch('infrastructure.aws',side_effect=self.client): infra.inspect(record)
        self.host['InstanceId']='i-'+'b'*17
        self.calls=[]
        with patch('infrastructure.aws',side_effect=self.client),self.assertRaises(RuntimeError): infra.destroy(self.directory,record)
        self.assertFalse(any(args[0]=='delete-stack' for _,args in self.calls))

    def test_all_protected_resource_classes_and_deleted_stack_idempotence(self):
        for prefix in ['i','vpc','subnet','sg','igw','rtb','vol','eni']:
            with self.subTest(prefix=prefix):
                record=copy.deepcopy(self.record)
                protected=prefix+'-'+'f'*17
                if protected not in record['protected_ids']: record['protected_ids'].append(protected)
                with self.assertRaises(RuntimeError): infra.unprotected(record,protected)
        def deleted(record,service,*args,**kwargs):
            value=self.client(record,service,*args,**kwargs)
            if args[0]=='describe-stacks': value['Stacks'][0]['StackStatus']='DELETE_COMPLETE'
            return value
        with patch('infrastructure.aws',side_effect=deleted):
            infra.destroy(self.directory,self.submitted()); infra.destroy(self.directory,self.record)
        self.assertFalse(any(args[0]=='delete-stack' for _,args in self.calls))

    def test_partial_stack_requires_proven_ownership_and_quiescence(self):
        def partial(record,service,*args,**kwargs):
            value=self.client(record,service,*args,**kwargs)
            if args[0]=='describe-stacks': value['Stacks'][0]['StackStatus']='CREATE_FAILED'
            if args[0]=='list-stack-resources': value['StackResourceSummaries']=value['StackResourceSummaries'][:1]
            return value
        with patch('infrastructure.aws',side_effect=partial): infra.destroy(self.directory,self.submitted())
        self.assertTrue(any(args[0]=='delete-stack' for _,args in self.calls))
        self.calls=[]
        def unowned(record,service,*args,**kwargs):
            value=partial(record,service,*args,**kwargs)
            if args[0]=='describe-vpcs': value['Vpcs'][0]['Tags']=[]
            return value
        with patch('infrastructure.aws',side_effect=unowned),self.assertRaises(RuntimeError): infra.destroy(self.directory,self.submitted())
        self.assertFalse(any(args[0]=='delete-stack' for _,args in self.calls))
        # DELETE_IN_PROGRESS now permits only an identity-checked read-only waiter, never another delete.
        for status in ['CREATE_IN_PROGRESS','UPDATE_COMPLETE','IMPORT_COMPLETE']:
            self.calls=[]
            def busy(record,service,*args,**kwargs):
                value=partial(record,service,*args,**kwargs)
                if args[0]=='describe-stacks': value['Stacks'][0]['StackStatus']=status
                return value
            with patch('infrastructure.aws',side_effect=busy),self.assertRaises(RuntimeError): infra.destroy(self.directory,self.submitted())
            self.assertFalse(any(args[0]=='delete-stack' for _,args in self.calls))


@unittest.skipUnless(os.name == 'posix' and os.geteuid() == 0, 'Host proof claims run in the isolated root Linux CI container')
class FailureProofTests(unittest.TestCase):
    def test_only_exact_backup_injection_produces_single_use_state_bound_proof(self):
        sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
        from release_state import SafetyError
        verified={'manifest':{'source_sha':'a'*40},'manifest_digest':'sha256:'+'b'*64}
        identity={'run_id':'c'*32,'instance_id':'i-'+'d'*17}
        class Ops:
            def __init__(self,scenario):
                self.scenario=scenario; self.stops=0
                self.state=SimpleNamespace(value={'current':verified,'history':[{'release':verified}],'inflight':None})
            def backup(self): raise AssertionError('Real backup must never run in a local mock')
            def stop_writers(self):
                self.stops+=1
                if self.scenario=='stop' or (self.scenario=='cleanup' and self.stops==2): raise SafetyError('Unexpected stop-writers failure')
            def deploy(self,target):
                self.state.value['inflight']={'stage':'prepared'}
                try:
                    self.stop_writers(); self.state.value['inflight']['stage']='writers-stopped'
                    if self.scenario in ('backup','migration','deployment'): raise SafetyError('Unexpected '+self.scenario+' failure')
                    self.backup()
                except BaseException:
                    try: self.stop_writers()
                    except BaseException: pass
                    raise
        for scenario in ['stop','backup','migration','deployment','cleanup','success']:
            with self.subTest(scenario=scenario),tempfile.TemporaryDirectory() as temp:
                path=Path(temp)/'proof.json'; ops=Ops(scenario)
                if scenario!='success':
                    with self.assertRaises(Exception): host_control.inject_backup_failure(ops,verified,identity,'e'*32,path)
                    self.assertFalse(path.exists()); self.assertIsNotNone(ops.state.value['inflight'])
                    continue
                proof=host_control.inject_backup_failure(ops,verified,identity,'e'*32,path)
                self.assertEqual(proof['failure_id'],host_control.FAILURE_ID)
                with patch('host_control.state',return_value=ops.state.value):
                    with self.assertRaises(RuntimeError): host_control._claim_failure_proof(identity,'a'*40,'e'*32,path,release_tag='racebib-'+('a'*40)+'-recovery-1-1')
                    for bad_nonce in ['', 'f'*32]:
                        with self.assertRaises(RuntimeError): host_control._claim_failure_proof(identity,'a'*40,bad_nonce,path)
                    ops.state.value['inflight']['stage']='unexpected-stage'
                    with self.assertRaises(RuntimeError): host_control._claim_failure_proof(identity,'a'*40,'e'*32,path)
                    ops.state.value['inflight']['stage']='writers-stopped'
                    retained = path.read_bytes()
                    host_control._claim_failure_proof(identity,'a'*40,'e'*32,path)
                    with self.assertRaises(RuntimeError): host_control._claim_failure_proof(identity,'a'*40,'e'*32,path)
                self.assertEqual(path.read_bytes(),retained)
                self.assertTrue(host_control.proof_claim_path(path,'e'*32).exists())


if __name__=='__main__': unittest.main()
