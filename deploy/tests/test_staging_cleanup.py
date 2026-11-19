"""Offline reproductions of partial creation and deleted-stack cleanup; no AWS process is permitted."""
import contextlib
import copy
import io
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
import unittest
from concurrent.futures import ThreadPoolExecutor
import threading
from unittest.mock import patch

import test_staging_rehearsal as fixtures
import infrastructure as infra


class StagingCleanupTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.StagingTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        self.record = self.fixture.submitted()
        self.directory = self.fixture.directory
        self.record.update(inventory={}, aws_cli=str(self.directory / 'never-execute-aws'))
        self.calls = []
        self.block = patch.object(infra.subprocess, 'run', side_effect=AssertionError('No real AWS process allowed'))
        self.block.start()
        self.addCleanup(self.block.stop)

    def deleted(self, record, service, *args, **kwargs):
        self.calls.append((service, args))
        if service == 'sts':
            return {'Account': record['account']}
        if service == 'cloudformation':
            return {'Stacks': [{'StackId': self.fixture.arn, 'StackStatus': 'DELETE_COMPLETE', 'Tags': self.fixture.tags()}]}
        if '--volume-ids' in args:
            return {'Volumes': []}
        if '--network-interface-ids' in args:
            return {'NetworkInterfaces': []}
        return {key: [] for key in ('Reservations', 'Volumes', 'Addresses', 'NatGateways', 'Snapshots', 'NetworkInterfaces', 'Vpcs')}

    def test_reported_empty_inventories_deleted_stack_and_absent_target_pass_without_mutation(self):
        before = copy.deepcopy(self.record)
        with patch.object(infra, 'aws', side_effect=self.deleted), patch.object(infra, 'save') as save:
            result = infra.cleanup(self.record)
            save.assert_not_called()
        self.assertEqual(self.record, before)
        self.assertEqual(result['tracked_and_tagged_billable_resources'], 'none')
        self.assertFalse(result['account_wide_billing_certification'])
        self.assertTrue({'describe-vpcs', 'describe-network-interfaces'} <= {a[0] for _, a in self.calls})
        self.assertFalse(any(a[0] in ('delete-stack', 'create-stack') for _, a in self.calls))

    def test_exit_zero_aws_json_replays_the_full_reported_cleanup_path(self):
        def process(command, **kwargs):
            service, arguments = command[5], command[6:-3]
            return SimpleNamespace(returncode=0, stdout=json.dumps(self.deleted(self.record, service, *arguments)), stderr='')
        with patch.object(infra.subprocess, 'run', side_effect=process):
            self.assertEqual(infra.cleanup(self.record)['tracked_and_tagged_billable_resources'], 'none')
            infra.destroy(self.directory, self.record)
        self.assertFalse(any(a[0] in ('delete-stack', 'create-stack') for _, a in self.calls))

    def test_failed_creation_wait_keeps_stack_identity_without_target_and_never_resubmits(self):
        record = self.fixture.record
        record.pop('stack_id')
        record.pop('creation_submitted')
        def failed_wait(record, service, *args, **kwargs):
            if args[:2] == ('wait', 'stack-create-complete'):
                raise infra.InfrastructureFailure('aws-cli', 'cloudformation', 'wait', 'stack-create-complete', 255, 'unknown')
            return self.fixture.client(record, service, *args, **kwargs)
        with patch.object(infra, 'aws', side_effect=failed_wait), self.assertRaises(infra.InfrastructureFailure):
            infra.create(self.directory, record, record['run_id'], self.fixture.execution_id)
        saved = json.loads((self.directory / 'run.json').read_text())
        self.assertTrue(saved['creation_submitted'])
        self.assertEqual(saved['stack_id'], self.fixture.arn)
        self.assertNotIn('target', saved)
        with patch.object(infra, 'aws', side_effect=self.deleted):
            infra.destroy(self.directory, saved)
            infra.cleanup(saved)
        with patch.object(infra, 'aws') as aws, self.assertRaises(RuntimeError):
            infra.create(self.directory, saved, saved['run_id'], self.fixture.execution_id)
        aws.assert_not_called()

    def test_cleanup_cli_does_not_rewrite_historical_run_or_generate_acceptance(self):
        infra.save(self.directory, self.record)
        path = self.directory / 'run.json'
        before = path.read_bytes(), path.stat().st_mtime_ns
        argv = ['infrastructure.py', 'cleanup', '--directory', str(self.directory), '--aws-cli', self.record['aws_cli']]
        with patch.object(infra, 'aws', side_effect=self.deleted), patch.object(sys, 'argv', argv), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(infra.cli(), 0)
        self.assertEqual((path.read_bytes(), path.stat().st_mtime_ns), before)
        self.assertFalse((self.directory / 'acceptance.json').exists())
        self.assertFalse((self.directory / 'cleanup.json').exists())

    def test_deleted_stack_destroy_is_idempotent_and_checks_identity_and_ownership(self):
        with patch.object(infra, 'aws', side_effect=self.deleted):
            infra.destroy(self.directory, self.record)
            infra.destroy(self.directory, self.record)
        self.assertFalse(any(a[0] == 'delete-stack' for _, a in self.calls))
        for wrong in ('tags', 'id'):
            def changed(record, service, *args, **kwargs):
                value = self.deleted(record, service, *args, **kwargs)
                if service == 'cloudformation':
                    if wrong == 'tags': value['Stacks'][0]['Tags'] = []
                    else: value['Stacks'][0]['StackId'] = self.fixture.arn + '-other'
                return value
            for operation in (infra.cleanup, lambda record: infra.destroy(self.directory, record)):
                with self.subTest(wrong=wrong), patch.object(infra, 'aws', side_effect=changed), self.assertRaises(RuntimeError):
                    operation(self.record)

    def test_lost_create_response_and_deleted_stack_reconcile_without_resubmission(self):
        self.record.pop('stack_id')
        with patch.object(infra, 'aws', side_effect=self.deleted):
            infra.destroy(self.directory, self.record)
            infra.cleanup(self.record)
        self.assertEqual(json.loads((self.directory / 'run.json').read_text())['stack_id'], self.fixture.arn)
        self.assertFalse(any(a[0] in ('create-stack', 'delete-stack') for _, a in self.calls))
        self.record.pop('stack_id')
        with patch.object(infra, 'aws', side_effect=lambda r, s, *a, **k: None if s == 'cloudformation' else self.deleted(r, s, *a, **k)):
            with self.assertRaisesRegex(RuntimeError, 'ambiguous'):
                infra.cleanup(self.record)

    def test_delete_in_progress_only_waits_and_never_resends_deletion(self):
        def deleting(record, service, *args, **kwargs):
            if args[0] == 'wait':
                self.calls.append((service, args))
                return {}
            value = self.deleted(record, service, *args, **kwargs)
            if service == 'cloudformation': value['Stacks'][0]['StackStatus'] = 'DELETE_IN_PROGRESS'
            return value
        with patch.object(infra, 'aws', side_effect=deleting):
            infra.destroy(self.directory, self.record)
        self.assertEqual(sum(a[:2] == ('wait', 'stack-delete-complete') for _, a in self.calls), 1)
        self.assertFalse(any(a[0] == 'delete-stack' for _, a in self.calls))

    def test_partial_attachment_inventory_and_tagged_vpc_or_eni_prevent_false_cleanup(self):
        for key, identifier in (('volumes', 'vol-partial'), ('interfaces', 'eni-partial')):
            self.record['target'] = None
            self.record['attachments'] = {'volumes': [], 'interfaces': [], key: [identifier]}
            def retained(record, service, *args, **kwargs):
                if identifier in args:
                    return {'Volumes': [{}], 'NetworkInterfaces': [{}]}
                return self.deleted(record, service, *args, **kwargs)
            with self.subTest(key=key), patch.object(infra, 'aws', side_effect=retained), self.assertRaises(RuntimeError):
                infra.cleanup(self.record)
        self.record.pop('attachments')
        for operation, key in (('describe-vpcs', 'Vpcs'), ('describe-network-interfaces', 'NetworkInterfaces')):
            def tagged(record, service, *args, **kwargs):
                return {key: [{}]} if args[0] == operation else self.deleted(record, service, *args, **kwargs)
            with self.subTest(operation=operation), patch.object(infra, 'aws', side_effect=tagged), self.assertRaises(RuntimeError):
                infra.cleanup(self.record)

    def test_null_target_with_no_attachments_and_empty_id_response_is_not_a_leak(self):
        self.record['target'] = None
        with patch.object(infra, 'aws', side_effect=self.deleted):
            infra.cleanup(self.record)
        self.record['target'] = {'interfaces': ['eni-gone'], 'volumes': ['vol-gone']}
        with patch.object(infra, 'aws', side_effect=self.deleted):
            infra.cleanup(self.record)

    def test_aws_diagnostics_are_actionable_but_never_contain_raw_arguments_or_credentials(self):
        secret = 'SENSITIVE-OUTPUT-MUST-NOT-APPEAR'
        cases = [SimpleNamespace(returncode=255, stdout=secret, stderr='An error occurred (AccessDenied) when calling the DescribeSnapshots operation: '+secret),
                 SimpleNamespace(returncode=1, stdout=secret, stderr=secret),
                 SimpleNamespace(returncode=0, stdout=secret, stderr=secret),
                 SimpleNamespace(returncode=0, stdout='[]', stderr=secret),
                 subprocess.TimeoutExpired('command-'+secret, 120, output=secret, stderr=secret),
                 OSError(secret), UnicodeError(secret)]
        for case in cases:
            with self.subTest(case=type(case).__name__), patch.object(infra.subprocess, 'run', side_effect=case if isinstance(case, Exception) else None,
                                                                     return_value=case):
                with self.assertRaises(infra.InfrastructureFailure) as caught:
                    infra.aws(self.record, 'ec2', 'describe-snapshots', '--owner-ids', secret)
                value = infra.diagnostic(caught.exception, 'cleanup')
                self.assertEqual(value['service'], 'ec2')
                self.assertEqual(value['operation'], 'describe-snapshots')
                self.assertNotIn(secret, json.dumps(value) + str(caught.exception))
        with patch.object(infra.subprocess, 'run', return_value=cases[0]):
            try: infra.aws(self.record, 'ec2', 'describe-snapshots')
            except Exception as error:
                self.assertEqual(infra.diagnostic(error, 'cleanup')['aws_error_code'], 'AccessDenied')

    def test_missing_stack_requires_exact_absence_not_permissions_or_credential_errors(self):
        absent = 'An error occurred (ValidationError) when calling the DescribeStacks operation: Stack with id '+self.fixture.arn+' does not exist'
        with patch.object(infra.subprocess, 'run', return_value=SimpleNamespace(returncode=255, stdout='', stderr=absent)):
            self.assertIsNone(infra.aws(self.record, 'cloudformation', 'describe-stacks', '--stack-name', self.fixture.arn, missing_ok=True))
        for text in (absent.replace('ValidationError', 'AccessDenied'), absent.replace(self.fixture.arn, 'other-stack'),
                     'credentials do not exist', 'does not exist'):
            with patch.object(infra.subprocess, 'run', return_value=SimpleNamespace(returncode=255, stdout='', stderr=text)), self.assertRaises(infra.InfrastructureFailure):
                infra.aws(self.record, 'cloudformation', 'describe-stacks', '--stack-name', self.fixture.arn, missing_ok=True)

    def test_main_failure_emits_safe_protocol_even_for_arbitrary_python_errors(self):
        for error in (KeyError('PRIVATE-KEY-TEXT'), AttributeError('SSH RAW OUTPUT'), PermissionError('SECRET ENV VALUE')):
            out, err = io.StringIO(), io.StringIO()
            with patch.object(infra, 'main', side_effect=error), patch.object(sys, 'argv', ['infrastructure.py', 'cleanup']), \
                    contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                self.assertEqual(infra.cli(), 1)
            value = json.loads(out.getvalue())
            self.assertEqual(value['protocol'], 'racebib-stage-error-v1')
            self.assertEqual(value['action'], 'cleanup')
            self.assertNotIn(str(error), out.getvalue() + err.getvalue())

    def test_exit_zero_but_malformed_inventory_identifies_operation_and_fails_closed(self):
        def malformed(record, service, *args, **kwargs):
            return {'Snapshots': None} if args[0] == 'describe-snapshots' else self.deleted(record, service, *args, **kwargs)
        with patch.object(infra, 'aws', side_effect=malformed), self.assertRaises(infra.InfrastructureFailure) as caught:
            infra.cleanup(self.record)
        value = infra.diagnostic(caught.exception, 'cleanup')
        self.assertEqual((value['category'], value['operation'], value['exit_code']), ('aws-response', 'describe-snapshots', '0'))

    def test_explicit_id_absence_requires_empty_collection_or_recognized_not_found(self):
        for source in ('target', 'attachments'):
            for field, key, identifier, flag, code in (
                    ('volumes', 'Volumes', 'vol-known', '--volume-ids', 'InvalidVolume.NotFound'),
                    ('interfaces', 'NetworkInterfaces', 'eni-known', '--network-interface-ids', 'InvalidNetworkInterfaceID.NotFound')):
                self.record.pop('target', None); self.record.pop('attachments', None)
                self.record[source] = {field: [identifier]}
                for response in ({key: []}, None):
                    def absent(record, service, *args, **kwargs):
                        return response if flag in args else self.deleted(record, service, *args, **kwargs)
                    with self.subTest(source=source, field=field, response=response), patch.object(infra, 'aws', side_effect=absent):
                        self.assertEqual(infra.cleanup(self.record)['tracked_and_tagged_billable_resources'], 'none')
                # Exercise the actual CLI NotFound classifier, not just its None sentinel.
                def process(command, **kwargs):
                    if flag in command:
                        return SimpleNamespace(returncode=255, stdout='', stderr='An error occurred ('+code+') when calling the Describe operation: missing')
                    return SimpleNamespace(returncode=0, stdout=json.dumps(self.deleted(self.record, command[5], *command[6:-3])), stderr='')
                with patch.object(infra.subprocess, 'run', side_effect=process):
                    infra.cleanup(self.record)

    def test_malformed_or_ambiguous_explicit_id_responses_never_certify_cleanup(self):
        for source in ('target', 'attachments'):
            for field, key, id_key, identifier, flag in (
                    ('volumes', 'Volumes', 'VolumeId', 'vol-known', '--volume-ids'),
                    ('interfaces', 'NetworkInterfaces', 'NetworkInterfaceId', 'eni-known', '--network-interface-ids')):
                self.record.pop('target', None); self.record.pop('attachments', None)
                self.record[source] = {field: [identifier]}
                invalid = ({}, {'wrong': []}, {key: None}, {key: {}}, {key: ''}, {key: False}, {key: 0}, [], '',
                           {key: [], 'NextToken': 'ambiguous-page'}, {key: [], 'unexpected': []},
                           {key: [{}]}, {key: ['wrong']}, {key: [{id_key: 'other'}]},
                           {key: [{id_key: identifier}, {id_key: identifier}]})
                for response in invalid:
                    def malformed(record, service, *args, **kwargs):
                        return response if flag in args else self.deleted(record, service, *args, **kwargs)
                    with self.subTest(source=source, field=field, response=response), patch.object(infra, 'aws', side_effect=malformed), \
                            self.assertRaises(infra.InfrastructureFailure):
                        infra.cleanup(self.record)

    def test_valid_present_explicit_id_resources_block_cleanup_for_both_inventory_sources(self):
        for source in ('target', 'attachments'):
            for field, key, id_key, identifier, flag in (
                    ('volumes', 'Volumes', 'VolumeId', 'vol-known', '--volume-ids'),
                    ('interfaces', 'NetworkInterfaces', 'NetworkInterfaceId', 'eni-known', '--network-interface-ids')):
                self.record.pop('target', None); self.record.pop('attachments', None)
                self.record[source] = {field: [identifier]}
                def present(record, service, *args, **kwargs):
                    return {key: [{id_key: identifier}]} if flag in args else self.deleted(record, service, *args, **kwargs)
                with self.subTest(source=source, field=field), patch.object(infra, 'aws', side_effect=present), self.assertRaises(RuntimeError):
                    infra.cleanup(self.record)

    def test_falsey_malformed_local_inventory_cannot_be_silently_discarded(self):
        for source in ('target', 'attachments'):
            for value in ([], '', False, 0, {'volumes': None}, {'interfaces': ''}, {'volumes': [None]}):
                self.record.pop('target', None); self.record.pop('attachments', None)
                self.record[source] = value
                with self.subTest(source=source, value=value), patch.object(infra, 'aws', side_effect=self.deleted), self.assertRaises(RuntimeError):
                    infra.cleanup(self.record)

    def test_create_requires_current_execution_claim_before_any_aws_call(self):
        self.record.pop('stack_id'); self.record.pop('creation_submitted')
        for execution_id in (None, 'invalid', 'd' * 32):
            with patch.object(infra, 'aws') as aws, self.assertRaises((RuntimeError, FileExistsError)):
                infra.create(self.directory, self.record, self.record['run_id'], execution_id)
            aws.assert_not_called()
        self.assertFalse((self.directory / 'creation-attempt.json').exists())

    def test_consumed_create_claim_blocks_concurrent_stale_records_before_aws_or_writes(self):
        self.record.pop('stack_id'); self.record.pop('creation_submitted')
        stale = copy.deepcopy(self.record)
        def interrupted(record, service, *args, **kwargs):
            if args[0] == 'create-stack': raise infra.InfrastructureFailure('aws-timeout', 'cloudformation', 'create-stack')
            return self.fixture.client(record, service, *args, **kwargs)
        with patch.object(infra, 'aws', side_effect=interrupted), self.assertRaises(infra.InfrastructureFailure):
            infra.create(self.directory, self.record, self.record['run_id'], self.fixture.execution_id)
        before = {p.name: (p.read_bytes(), p.stat().st_mtime_ns) for p in self.directory.iterdir() if p.is_file()}
        with patch.object(infra, 'aws') as aws, self.assertRaises(FileExistsError):
            infra.create(self.directory, stale, stale['run_id'], self.fixture.execution_id)
        aws.assert_not_called()
        self.assertEqual(before, {p.name: (p.read_bytes(), p.stat().st_mtime_ns) for p in self.directory.iterdir() if p.is_file()})

    def test_two_concurrent_python_create_attempts_consume_authorization_only_once(self):
        self.record.pop('stack_id'); self.record.pop('creation_submitted')
        gate = threading.Barrier(2)
        def create():
            record = copy.deepcopy(self.record)
            gate.wait(timeout=10)
            try:
                infra.create(self.directory, record, record['run_id'], self.fixture.execution_id)
                return 'accepted'
            except FileExistsError:
                return 'rejected'
        with patch.object(infra, 'aws', side_effect=self.fixture.client), ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda _: create(), range(2)))
        self.assertCountEqual(results, ['accepted', 'rejected'])
        self.assertEqual(sum(args[0] == 'create-stack' for _, args in self.fixture.calls), 1)


if __name__ == '__main__':
    unittest.main()
