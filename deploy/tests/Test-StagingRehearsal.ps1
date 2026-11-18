#Requires -Version 7.4
# Every AWS, provisioning SSH and HTTPS operation is mocked. TLS chains are generated locally.
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$module=Import-Module (Join-Path $root 'deploy/staging/RaceBib.Staging.psm1') -Force -PassThru
$fixture=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) ('racebib-staging-test-'+[guid]::NewGuid().ToString('N'))
try {
    & $module {
        param($Fixture)
        $script:Checks=0
        function Assert-True($value,$message) { if (-not $value) { throw ('TEST FAILED: '+$message) }; $script:Checks++ }
        function Assert-Throws([scriptblock]$Body,$message) { $failed=$false; try { & $Body | Out-Null } catch { $failed=$true }; Assert-True $failed $message }
        New-StageDirectory $Fixture
        $source='a'*40; $runId='b'*32
        $script:Evidence=@{source_sha=$source;release_tag=('racebib-'+$source);manifest_digest=('sha256:'+('c'*64));api=('ghcr.io/vreboton/racebib-api@sha256:'+('d'*64));web=('ghcr.io/vreboton/racebib-web@sha256:'+('e'*64))}
        $script:Target=@{run_id=$runId;instance_id=('i-'+('a'*17));public_ip='8.8.4.4';domain=('stage-'+$runId+'.racebib.invalid');host_key='';ca_pem='Y2E='}
        [byte[]]$blob=@([byte]0,0,0,11)+[Text.Encoding]::ASCII.GetBytes('ssh-ed25519')+@([byte]0,0,0,32)+[byte[]]::new(32)
        $script:Target.host_key='ssh-ed25519 '+[Convert]::ToBase64String($blob)
        $script:EntryFixtures=Get-Content -Raw (Join-Path $script:Here '../tests/fixtures/staging-barrier-entry.json')|ConvertFrom-Json -AsHashtable
        $settings=@{SourceSha=$source;RecoveryTag='';PythonPath=(Join-Path $env:WINDIR 'System32/cmd.exe');Directory=$Fixture;MySqlImage=('mysql:8.4@sha256:'+('f'*64));CostCeilingUsd=1}
        $settings|ConvertTo-Json|Set-Content (Join-Path $Fixture 'settings.json')
        '{}'|Set-Content (Join-Path $Fixture 'template.json'); '{}'|Set-Content (Join-Path $Fixture 'bundle.json')
        foreach ($name in @('bootstrap-key','deployment-key')) {
            'synthetic-key-never-used'|Set-Content (Join-Path $Fixture $name)
            $script:Target.host_key|Set-Content (Join-Path $Fixture ($name+'.pub'))
        }
        & $script:Core {
            function script:Invoke-RaceBibNative {
                param($Executable,$Arguments,$TimeoutSeconds,[switch]$StripTokens)
                return @{ExitCode=0;Stdout=($global:RaceBibStagingMockEvidence|ConvertTo-Json -Compress)}
            }
        }
        $global:RaceBibStagingMockEvidence=$script:Evidence
        $script:Events=@(); $script:Barrier=$false; $script:Scenario='success'
        function script:Read-Host { param($Prompt) return ('CREATE STAGING '+('b'*32)) }
        function script:Invoke-StageInfra {
            param($Settings,$Action,$Directory,$Extra=@())
            $script:Events+='infra-'+$Action
            if ($Action -eq 'create') {
                $record=Get-Content -Raw (Join-Path $Directory 'run.json')|ConvertFrom-Json -AsHashtable
                $record.creation_submitted=$true
                $record|ConvertTo-Json|Set-Content (Join-Path $Directory 'run.json'); Protect-StageFile (Join-Path $Directory 'run.json')
            }
            if ($Action -in @('inspect','trust','create')) {
                $target=$script:Target.Clone()
                if ($script:Scenario -eq 'production-target') { $target.public_ip='15.135.108.24' }
                return $target
            }
            return @{tracked_and_tagged_billable_resources='none'}
        }
        function script:Invoke-StageSsh {
            param($Config,$Arguments,$InputJson='')
            $command=$Arguments[-1]
            $script:Events+='ssh-'+$command
            if ($command -eq 'stage-control') {
                $payload=$InputJson|ConvertFrom-Json -AsHashtable
                $script:Events+='control-'+$payload.action
                if ($payload.action -eq 'prepare-barrier-probe') {
                    $script:ProbeNonce=$payload.probe_nonce
                    $protocol=if ($script:Scenario -eq 'legacy-recovery-host') {'racebib-stage-barrier-v1'} else {'racebib-stage-barrier-v2'}
                    return @{ExitCode=0;Stderr='';Stdout=(@{protocol=$protocol;phase='prepared';probe_nonce=$payload.probe_nonce}|ConvertTo-Json -Compress)}
                }
                if ($payload.action -eq 'verify-barrier-probe') {
                    if ($script:Scenario -in @('barrier-unrelated','barrier-generic')) { return @{ExitCode=1;Stdout='';Stderr='Staging control failed'} }
                    $script:ProbeVerified=$true
                    $receipt=@{protocol='racebib-stage-barrier-v2';deployment='rejected';reason='maintenance-barrier';run_id=('b'*32);instance_id=$script:Target.instance_id;source_sha=('a'*40);release_tag=('racebib-'+('a'*40));failure_nonce=$script:FailureNonce;probe_nonce=$script:ProbeNonce;state_digest=('f'*64);log_name=$payload.log_name;log_digest=('c'*64)}
                    if ($script:Scenario -eq 'barrier-malformed-receipt') { $receipt.reason='generic-error' }
                    return @{ExitCode=0;Stderr='';Stdout=($receipt|ConvertTo-Json -Compress)}
                }
                $proof=$null
                if ($payload.action -eq 'arm-failure') {
                    $script:Barrier=$true; $script:FailureNonce=$payload.failure_nonce
                    if ($script:Scenario -eq 'unexpected-failure') { throw 'Synthetic unrelated stop-writers failure' }
                    if ($script:Scenario -ne 'missing-proof') { $proof=@{failure_id='racebib-stage-backup-v1';nonce=$script:FailureNonce;run_id=('b'*32);instance_id=$script:Target.instance_id;source_sha=('a'*40);release_tag=('racebib-'+('a'*40));state_digest=('f'*64)} }
                    if ($script:Scenario -eq 'wrong-proof') { $proof.nonce='0'*32 }
                }
                if ($payload.action -eq 'recover') {
                    Assert-True ($payload.failure_nonce -ceq $script:FailureNonce -and $payload.probe_nonce -ceq $script:ProbeNonce -and $script:ProbeVerified) 'Recovery requires exact synthetic challenge and verified barrier probe'
                    if ($script:Scenario -in @('recovery-failed','recovery-busy')) { return @{ExitCode=1;Stdout='';Stderr='Staging control failed; inspect the retained barrier and claim.'} }
                    $script:Barrier=$false; $script:History++
                }
                if ($payload.action -eq 'initialize') { $script:History=1 }
                return @{ExitCode=0;Stderr='';Stdout=(@{run_id=('b'*32);maintenance=$script:Barrier;current_sha=('a'*40);manifest_digest=$script:Evidence.manifest_digest;history_count=$script:History;api_ready=(-not $script:Barrier);frontend_ready=(-not $script:Barrier);synthetic_failure=$proof}|ConvertTo-Json -Compress)}
            }
            if ($script:Scenario -like 'negative-*') {
                $errorText=switch ($script:Scenario) { 'negative-auth' {'Permission denied (publickey).'} 'negative-client' {'Bad configuration option'} 'negative-timeout' {'Connection timed out'} default {'Connection reset by peer'} }
                return @{ExitCode=255;Stdout='';Stderr=$errorText}
            }
            if ($Config.KnownHostsPath.EndsWith('wrong-known-hosts')) { return @{ExitCode=255;Stdout='';Stderr="WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!`nHost key verification failed."} }
            if ($Config.PrivateKeyPath.EndsWith('bootstrap-key')) { return @{ExitCode=255;Stdout='';Stderr=('racebib-deploy@'+$Config.HostName+': Permission denied (publickey).')} }
            if ('-W' -in $Arguments) { return @{ExitCode=255;Stdout='';Stderr='channel 0: open failed: administratively prohibited: open failed'} }
            if ($command -ne ('deploy '+('a'*40))) { return @{ExitCode=1;Stdout='';Stderr='Only pinned RaceBib deployment is permitted'} }
            if ($script:Barrier) {
                $envelope=$script:EntryFixtures[0].result.Clone()
                switch ($script:Scenario) {
                    'barrier-disconnect' { $envelope.ExitCode=255; $envelope.Stderr='Connection reset' }
                    'barrier-auth' { $envelope.ExitCode=255; $envelope.Stderr='Permission denied (publickey).' }
                    'barrier-timeout' { throw 'Synthetic SSH timeout' }
                    'barrier-success' { return ($script:EntryFixtures|Where-Object scenario -EQ 'success').result.Clone() }
                    'barrier-generic-only' { $envelope.Stdout='' }
                    'barrier-extra-output' { $envelope.Stdout+='unexpected output' }
                    'barrier-extra-stderr' { $envelope.Stderr+='unexpected error' }
                }
                return $envelope
            }
            if ($script:Scenario -eq 'disconnect') { return @{ExitCode=255;Stdout='';Stderr='Connection reset'} }
            $script:History++
            $receipt=$script:Evidence.Clone(); $receipt.deployment='succeeded'; $receipt.host_state='verified'; $receipt.schema_hash='f'*64
            return @{ExitCode=0;Stderr='';Stdout=('RACEBIB_RESULT '+($receipt|ConvertTo-Json -Compress))}
        }
        function script:Test-StageHttps {
            param($Config,$Settings,$Run,$Target)
            $script:Events+='https'
            Assert-True ($Config.HostName -ne '15.135.108.24') 'No production HTTPS target'
            if ($script:Scenario -eq 'tls-failure') { throw 'Synthetic TLS rejection' }
        }
        $prior=@{a=$env:RACEBIB_STAGING_GH_TOKEN;b=$env:RACEBIB_STAGING_GHCR_TOKEN;c=$env:RACEBIB_STAGING_GHCR_USERNAME}
        try {
            $env:RACEBIB_STAGING_GH_TOKEN='synthetic-github-read';$env:RACEBIB_STAGING_GHCR_TOKEN='synthetic-ghcr-read';$env:RACEBIB_STAGING_GHCR_USERNAME='synthetic-user'
            foreach ($scenario in @('success','production-target','disconnect','tls-failure','barrier-disconnect','unexpected-failure','missing-proof','wrong-proof','negative-auth','negative-client','negative-timeout','negative-connection','barrier-auth','barrier-timeout','barrier-unrelated','barrier-generic','barrier-generic-only','barrier-extra-output','barrier-extra-stderr','barrier-success','barrier-malformed-receipt','recovery-failed','recovery-busy','legacy-recovery-host')) {
                $script:Scenario=$scenario; $script:Events=@();$script:Barrier=$false;$script:History=0;$script:ProbeVerified=$false
                @{run_id=$runId;stack_name=('racebib-staging-'+$runId);estimate_usd=0.32}|ConvertTo-Json|Set-Content (Join-Path $Fixture 'run.json')
                foreach ($file in @(Get-ChildItem $Fixture -File)) { Protect-StageFile $file.FullName }
                $failed=$false
                try { $result=Invoke-RaceBibStagingRehearsal -Action Execute -RunDirectory $Fixture } catch { $failed=$true; if ($scenario -eq 'success') { throw } }
                Assert-True ($failed -eq ($scenario -ne 'success')) ('Expected scenario outcome: '+$scenario)
                Assert-True (@($script:Events|Where-Object{$_ -eq 'infra-create'}).Count -eq 1) 'Creation never retried'
                Assert-True (@($script:Events|Where-Object{$_ -eq 'infra-destroy'}).Count -eq 1) 'Owned staging cleanup always attempted'
                if ($scenario -eq 'production-target') { Assert-True (@($script:Events|Where-Object{$_ -like 'ssh-*'}).Count -eq 0) 'Production target rejected before SSH' }
                if ($scenario -in @('recovery-failed','recovery-busy')) {
                    Assert-True (@($script:Events|Where-Object{$_ -eq 'control-recover'}).Count -eq 1 -and $script:Barrier) 'No retry or barrier clearing after recovery failure/contention'
                    $tail=@($script:Events|Select-Object -Skip ([Array]::IndexOf($script:Events,'control-recover')+1))
                    Assert-True (@($tail|Where-Object{$_ -like 'ssh-deploy *'}).Count -eq 0) 'No deployment after failed recovery'
                } elseif ($scenario -ne 'success') { Assert-True ('control-recover' -notin $script:Events) 'No automatic recovery after unrelated, inconclusive or unproven failure' }
                if ($scenario -eq 'success') {
                    Assert-True ($result.rehearsal -eq 'passed' -and 'control-arm-failure' -in $script:Events -and 'control-recover' -in $script:Events) 'Explicit synthetic barrier and recovery exercised'
                }
                foreach ($file in @(Get-ChildItem $Fixture -File)) {
                    Assert-True (-not (Get-Content -Raw $file.FullName).Contains('synthetic-github-read')) 'Read token not persisted in local state'
                }
            }
        } finally { $env:RACEBIB_STAGING_GH_TOKEN=$prior.a;$env:RACEBIB_STAGING_GHCR_TOKEN=$prior.b;$env:RACEBIB_STAGING_GHCR_USERNAME=$prior.c }
        foreach ($pair in @(@('forced-command','Only pinned RaceBib deployment is permitted',1),@('forwarding','channel 0: open failed: administratively prohibited: open failed',255),@('host-pin',"REMOTE HOST IDENTIFICATION HAS CHANGED!`nHost key verification failed.",255),@('identity','racebib-deploy@8.8.4.4: Permission denied (publickey).',255))) {
            $ok=@{ExitCode=$pair[2];Stdout='';Stderr=$pair[1]}
            Assert-StageRejection $ok $pair[0] '8.8.4.4'; $script:Checks++
            foreach ($bad in @('Connection timed out','Connection refused','Connection reset','Permission denied (publickey).','unknown option -- invalid','')) {
                Assert-Throws { Assert-StageRejection @{ExitCode=255;Stdout='';Stderr=$bad} $pair[0] '8.8.4.4' } 'Transport/client/authentication error is not expected security rejection'
            }
            Assert-Throws { Assert-StageRejection @{ExitCode=0;Stdout='';Stderr=$pair[1]} $pair[0] '8.8.4.4' } 'Unexpected success never proves rejection'
        }
        # These envelopes were emitted by actual ssh_entry.main(), with only run_engine mocked in a disposable container.
        foreach ($case in $script:EntryFixtures) {
            if ($case.scenario -eq 'maintenance') {
                $logName=Read-StageBarrierEnvelope $case.result
                Assert-True ($logName -ceq 'ssh-deployment-fixture0.log') 'Accept exact real-entry log announcement as envelope only'
                Assert-StageBarrierReceipt $case.receipt $case.proof $case.probe_nonce $logName; $script:Checks++
            } else {
                Assert-Throws { $logName=Read-StageBarrierEnvelope $case.result; Assert-StageBarrierReceipt $case.receipt $case.proof $case.probe_nonce $logName } ('No rejection proof for actual entry case: '+$case.scenario)
            }
        }
        $valid=$script:EntryFixtures[0]
        $receiptJson=$valid.receipt|ConvertTo-Json -Compress
        $parsed=ConvertFrom-StageBarrierResponse $receiptJson 'verify-barrier-probe'
        Assert-StageBarrierReceipt $parsed $valid.proof $valid.probe_nonce 'ssh-deployment-fixture0.log'; $script:Checks++
        foreach ($badJson in @('', '[]', ($receiptJson+'{}'), ('announcement '+$receiptJson),
                ('{"reason":"generic-error",'+$receiptJson.Substring(1)),
                $receiptJson.Replace(('"state_digest":"'+$valid.proof.state_digest+'"'),'"state_digest":123'))) {
            Assert-Throws { ConvertFrom-StageBarrierResponse $badJson 'verify-barrier-probe' } 'Reject malformed, duplicate, non-string or extra response output'
        }
        foreach ($bad in @('Connection timed out','Permission denied (publickey).','Connection refused','unknown option -- invalid','generic exception')) {
            Assert-Throws { Read-StageBarrierEnvelope @{ExitCode=255;Stdout=$valid.result.Stdout;Stderr=$bad} } 'Barrier probe never accepts transport/authentication/client errors'
        }
        foreach ($field in @('reason','failure_nonce','probe_nonce','state_digest','source_sha','release_tag','run_id','instance_id','log_name','log_digest','deployment','protocol')) {
            $wrong=$valid.receipt.Clone(); $wrong[$field]='wrong'
            Assert-Throws { Assert-StageBarrierReceipt $wrong $valid.proof $valid.probe_nonce 'ssh-deployment-fixture0.log' } ('Reject malformed or mismatched barrier receipt: '+$field)
        }
        # Exercise the actual production CLI wrapper's parameter wiring with scoped mocks.
        & {
            param($Entry)
            function Import-Module { param($Name,[switch]$Force) if ([IO.Path]::GetFileName($Name) -cne 'RaceBib.Deployment.psm1' -or -not $Force) { throw 'Unexpected wrapper import' } }
            function Invoke-RaceBibLocalDeployment { param($SourceSha,$RecoveryTag,$ConfigPath) return @{SourceSha=$SourceSha;RecoveryTag=$RecoveryTag;ConfigPath=$ConfigPath} }
            $result=& $Entry -SourceSha ('a'*40) -RecoveryTag ('racebib-'+('a'*40)+'-recovery-123-1') -ConfigPath 'mock-only-never-loaded'
            if ($result.SourceSha -cne ('a'*40) -or $result.ConfigPath -cne 'mock-only-never-loaded' -or $result.RecoveryTag -cne ('racebib-'+('a'*40)+'-recovery-123-1')) { throw 'Production wrapper selector/config wiring changed' }
        } (Join-Path $script:Here '../windows/Invoke-RaceBibDeployment.ps1')
        $script:Checks++
        # Cryptographic TLS chain tests, no sockets or OS certificate-store writes.
        Add-Type -Path (Join-Path $script:Here 'StagingTls.cs')
        $key=[Security.Cryptography.RSA]::Create(2048)
        $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=SyntheticStagingRoot',$key,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
        $ca=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddDays(1))
        $leafKey=[Security.Cryptography.RSA]::Create(2048)
        $leafRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=SyntheticStagingLeaf',$leafKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $eku=[Security.Cryptography.OidCollection]::new();[void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku,$false))
        $leaf=$leafRequest.Create($ca,[DateTimeOffset]::UtcNow.AddSeconds(-20),[DateTimeOffset]::UtcNow.AddHours(1),[byte[]](1,2,3,4))
        Assert-True ([RaceBibStagingTls]::Validate($leaf,$ca,[Net.Security.SslPolicyErrors]::RemoteCertificateChainErrors)) 'Per-run private CA validates'
        Assert-True (-not [RaceBibStagingTls]::Validate($leaf,$ca,[Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch)) 'Hostname mismatch rejected'
        $wrongKey=[Security.Cryptography.RSA]::Create(2048)
        $wrongRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=WrongRoot',$wrongKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $wrongCa=$wrongRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddDays(1))
        Assert-True (-not [RaceBibStagingTls]::Validate($leaf,$wrongCa,[Net.Security.SslPolicyErrors]::RemoteCertificateChainErrors)) 'Unrelated/public trust does not replace the run CA'
        foreach ($item in @($ca,$leaf,$wrongCa,$key,$leafKey,$wrongKey)) { $item.Dispose() }
        Write-Output ('PASS: '+$script:Checks+' staging isolation/automation/TLS assertions; no AWS or production connection.')
    } $fixture
} finally {
    Remove-Variable RaceBibStagingMockEvidence -Scope Global -ErrorAction SilentlyContinue
    Remove-Module $module -Force
    $full=[IO.Path]::GetFullPath($fixture)
    $prefix=[Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile).TrimEnd('\')+'\'
    if (-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notlike 'racebib-staging-test-*') { throw 'Unsafe fixture cleanup target' }
    if (Test-Path $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
