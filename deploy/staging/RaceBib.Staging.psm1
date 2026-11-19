#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$script:Core=Import-Module (Join-Path $PSScriptRoot '../windows/RaceBib.Deployment.psm1') -Force -PassThru
$script:Here=$PSScriptRoot

function Protect-StageFile([string]$Path) {
    try {
        [void](& $script:Core { param($p) Assert-RaceBibPrivateFile $p } $Path)
        return
    } catch { } # A newly created inherited ACL still needs explicit protection.
    # Validate path ancestry/reparse points before changing any ACL.
    [void](& $script:Core { param($p) Get-RaceBibLocalFile $p } $Path)
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl=Get-Acl -LiteralPath $Path
    # Elevated Windows tokens can create files owned by Administrators rather than the operator.
    # Do not request WRITE_OWNER for files that already have the required owner (including OpenSSH keys).
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sid.Value) {
        $acl.SetOwner($sid)
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    $icacls=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32/icacls.exe'
    foreach ($aclArguments in @(@('/reset'),@('/inheritance:r','/grant:r',('*'+$sid.Value+':(F)')))) {
        & $icacls $Path @aclArguments 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw ("Could not protect run-file ACL ({0}, {1}); no weakening is allowed." -f [IO.Path]::GetFileName($Path),$aclArguments[0]) }
    }
    [void](& $script:Core { param($p) Assert-RaceBibPrivateFile $p } $Path)
}

function New-StageDirectory([string]$Path) {
    & $script:Core { param($p) Assert-RaceBibLocalPath $p } $Path
    if (Test-Path -LiteralPath $Path) { throw 'Choose a new staging run directory; do not reuse credentials/state.' }
    $repo=[IO.Path]::GetFullPath((Join-Path $script:Here '../..')).TrimEnd('\')+'\'
    if ([IO.Path]::GetFullPath($Path).StartsWith($repo,[StringComparison]::OrdinalIgnoreCase)) { throw 'Run state and credentials belong outside the checkout.' }
    [void][IO.Directory]::CreateDirectory($Path)
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($sid); $acl.SetAccessRuleProtection($true,$false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
    $probe=Join-Path $Path 'local-staging-marker'
    [IO.File]::WriteAllText($probe,'staging-only'); Protect-StageFile $probe
    [void](& $script:Core { param($p) Assert-RaceBibPrivateFile $p } $probe)
    $lock=Join-Path $Path 'execution.lock'
    [IO.File]::WriteAllText($lock,'racebib-stage-execution-lock-v1'); Protect-StageFile $lock
}

function Assert-StageUnusedRun($Run,[string]$Directory) {
    if ($Run -isnot [Collections.IDictionary] -or $Run.run_id -cnotmatch '\A[0-9a-f]{32}\z') { throw 'Invalid execution run identity.' }
    foreach ($field in @('creation_submitted','stack_id','execution_id','target','inventory','attachments')) {
        if ($Run.ContainsKey($field)) { throw 'Execute is single-use; this run already has execution state. No preflight or teardown permitted.' }
    }
    foreach ($name in @('execution.json','creation-attempt.json','acceptance.json','cleanup.json')) {
        if (Test-Path -LiteralPath (Join-Path $Directory $name)) { throw 'Execute authorization already consumed; use a fresh plan.' }
    }
}

function Enter-StageExecutionLock([string]$Directory,$Run) {
    Assert-StageUnusedRun $Run $Directory
    $path=Join-Path $Directory 'execution.lock'
    [void](& $script:Core { param($p) Assert-RaceBibPrivateFile $p } $path)
    try { $lock=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None) }
    catch { throw 'Another Execute owns this run, or its lock is unavailable; no preflight or teardown permitted.' }
    try {
        # Re-read under the cross-process lock; a prior invocation may have finished since the first read.
        $latest=Get-Content -Raw (Join-Path $Directory 'run.json')|ConvertFrom-Json -AsHashtable
        Assert-StageUnusedRun $latest $Directory
        if ($latest.run_id -cne $Run.run_id) { throw 'Run identity changed before execution.' }
        return $lock
    } catch { $lock.Dispose(); throw }
}

function New-StageExecutionClaim([string]$Directory,[string]$RunId) {
    $id=[guid]::NewGuid().ToString('N')
    $path=Join-Path $Directory 'execution.json'
    $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes((@{format=1;run_id=$RunId;execution_id=$id}|ConvertTo-Json -Compress))
        $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true)
    } finally { $stream.Dispose() }
    Protect-StageFile $path
    return $id
}

function Format-StageInfrastructureFailure([string]$Json,[string]$Action,[int]$ExitCode) {
    # Read only an exact typed envelope. Never echo arbitrary stdout/stderr/exception messages.
    try {
        if ($Json.Length -gt 4096) { throw 'Oversized diagnostic.' }
        $fields=@('protocol','action','category','service','operation','waiter','exit_code','aws_error_code','exception_type','phase','line')
        $value=& $script:Core { param($j,$f) ConvertFrom-RaceBibEvidenceJson $j $f } $Json $fields
        if ($value.protocol -cne 'racebib-stage-error-v1' -or $value.action -cne $Action -or
            $Action -cnotin @('plan','create','inspect','trust','destroy','cleanup') -or
            $value.category -cnotin @('controller','aws-cli','aws-arguments','aws-config','aws-authentication','aws-authorization',
                'aws-timeout','aws-start','aws-json','aws-encoding','aws-response') -or
            $value.service -cnotin @('none','ec2','sts','cloudformation') -or
            $value.operation -cnotin @('none','get-caller-identity','describe-images','create-stack','describe-stacks','get-template',
                'list-stack-resources','describe-stack-events','describe-vpcs','describe-subnets','describe-security-groups',
                'describe-route-tables','describe-internet-gateways','describe-volumes','describe-network-interfaces',
                'describe-instances','get-console-output','delete-stack','wait','describe-addresses','describe-nat-gateways','describe-snapshots') -or
            $value.waiter -cnotin @('none','stack-create-complete','stack-delete-complete') -or
            $value.aws_error_code -cnotin @('none','unknown','AccessDenied','AccessDeniedException','UnauthorizedOperation','ExpiredToken',
                'ExpiredTokenException','InvalidClientTokenId','RequestExpired','ValidationError','InvalidParameterValue','Throttling',
                'ThrottlingException','RequestLimitExceeded','InvalidInstanceID.NotFound','InvalidNetworkInterfaceID.NotFound','InvalidVolume.NotFound') -or
            $value.exception_type -cnotin @('unknown','InfrastructureFailure','RuntimeError','KeyError','TypeError','ValueError',
                'AttributeError','JSONDecodeError','OSError','FileNotFoundError','PermissionError','NotReady') -or
            $value.phase -cnotin @('main','aws','validate_record','account_guard','create','destroy','cleanup','save','stack_guard',
                'validate_resources','inspect','trust','ownership','unprotected','observed_stack') -or
            $value.line -cnotmatch '\A[0-9]{1,7}\z' -or $value.exit_code -cnotmatch '\A(?:none|-?[0-9]{1,10})\z') { throw 'Unrecognized diagnostic.' }
        if ($ExitCode -notin @(1,2) -or $value.line -cnotmatch '\A[1-9][0-9]{0,6}\z') { throw 'Invalid native failure status/location.' }
        if ($value.category -ceq 'controller') {
            if ($value.service -cne 'none' -or $value.operation -cne 'none' -or $value.waiter -cne 'none' -or
                $value.exit_code -cne 'none' -or $value.aws_error_code -cne 'none' -or
                $value.exception_type -ceq 'InfrastructureFailure') { throw 'Controller diagnostic contains AWS metadata.' }
            if ($value.exception_type -ceq 'NotReady') {
                if ($Action -cne 'trust' -or $ExitCode -ne 2 -or $value.phase -cne 'trust') { throw 'Invalid readiness diagnostic.' }
            } elseif ($ExitCode -ne 1) { throw 'Unexpected controller exit status.' }
        } else {
            if ($ExitCode -ne 1 -or $value.exception_type -cne 'InfrastructureFailure' -or $value.service -ceq 'none' -or
                $value.operation -ceq 'none') { throw 'Incomplete AWS failure diagnostic.' }
            if ($Action -ceq 'plan' -or ($value.operation -ceq 'create-stack' -and $Action -cne 'create') -or
                ($value.operation -ceq 'delete-stack' -and $Action -cne 'destroy')) { throw 'Mutation/action mismatch.' }
            $cloudFormation=@('create-stack','describe-stacks','get-template','list-stack-resources','describe-stack-events','delete-stack','wait')
            if (($value.service -ceq 'sts' -and $value.operation -cne 'get-caller-identity') -or
                ($value.service -ceq 'cloudformation' -and $value.operation -cnotin $cloudFormation) -or
                ($value.service -ceq 'ec2' -and ($value.operation -cin $cloudFormation -or $value.operation -ceq 'get-caller-identity'))) {
                throw 'Service and operation disagree.'
            }
            if ($value.operation -ceq 'wait') {
                if ($value.service -cne 'cloudformation' -or $value.waiter -ceq 'none') { throw 'Missing waiter identity.' }
                if (-not (($Action -ceq 'create' -and $value.waiter -ceq 'stack-create-complete') -or
                          ($Action -ceq 'destroy' -and $value.waiter -ceq 'stack-delete-complete'))) { throw 'Waiter/action mismatch.' }
            } elseif ($value.waiter -cne 'none') { throw 'Waiter attached to a non-wait operation.' }
            switch -CaseSensitive ($value.category) {
                'aws-cli' {
                    if ($value.exit_code -cnotmatch '\A-?[1-9][0-9]{0,9}\z' -or $value.exit_code -cin @('2','252','253') -or
                        $value.aws_error_code -cin @('none','AccessDenied','AccessDeniedException','UnauthorizedOperation',
                            'ExpiredToken','ExpiredTokenException','InvalidClientTokenId','RequestExpired') -or $value.phase -cne 'aws') { throw 'Contradictory CLI failure.' }
                }
                { $_ -cin @('aws-arguments','aws-config') } {
                    $codes=if ($value.category -ceq 'aws-config') { @('253') } else { @('2','252') }
                    if ($value.exit_code -cnotin $codes -or $value.aws_error_code -cne 'none' -or $value.phase -cne 'aws') { throw 'Contradictory local CLI failure.' }
                }
                { $_ -cin @('aws-authentication','aws-authorization') } {
                    $codes=if ($value.category -ceq 'aws-authentication') { @('ExpiredToken','ExpiredTokenException','InvalidClientTokenId','RequestExpired') }
                           else { @('AccessDenied','AccessDeniedException','UnauthorizedOperation') }
                    if ($value.exit_code -cnotmatch '\A-?[1-9][0-9]{0,9}\z' -or $value.exit_code -cin @('2','252','253') -or
                        $value.aws_error_code -cnotin $codes -or $value.phase -cne 'aws') { throw 'Contradictory authentication/authorization failure.' }
                }
                { $_ -cin @('aws-json','aws-response') } {
                    $expectedPhase=if ($value.category -ceq 'aws-response') { 'cleanup' } else { 'aws' }
                    if ($value.exit_code -cne '0' -or $value.aws_error_code -cne 'none' -or $value.phase -cne $expectedPhase) { throw 'Contradictory response failure.' }
                    if ($value.category -ceq 'aws-response' -and $Action -cne 'cleanup') { throw 'Response/action mismatch.' }
                }
                default {
                    if ($value.exit_code -cne 'none' -or $value.aws_error_code -cne 'none' -or $value.phase -cne 'aws') { throw 'Unobserved process exit contains result metadata.' }
                }
            }
        }
        return "action=$Action; native_exit=$ExitCode; phase=$($value.phase); line=$($value.line); category=$($value.category); service=$($value.service); operation=$($value.operation); waiter=$($value.waiter); aws_exit=$($value.exit_code); aws_code=$($value.aws_error_code); exception=$($value.exception_type)"
    } catch { return "action=$Action; native_exit=$ExitCode; sanitized diagnostic unavailable; raw output withheld" }
}

function Get-StageFailureSummary($Failure,[string]$Phase) {
    if ($Failure.Data.Contains('RaceBibStageSafeSummary')) { return $Failure.Data['RaceBibStageSafeSummary'] }
    return "phase=$Phase; controller failure; raw exception withheld"
}

function Complete-StageTeardown($Settings,[string]$Directory,[string]$RunId,[string]$ExecutionId) {
    $failures=[Collections.Generic.List[Exception]]::new()
    try { $latest=Get-Content -Raw (Join-Path $Directory 'run.json')|ConvertFrom-Json -AsHashtable }
    catch { throw 'Teardown outcome unconfirmed: run state unreadable; no AWS action attempted.' }
    if ($latest.ContainsKey('creation_submitted') -and $latest.creation_submitted) {
        if ($ExecutionId -cnotmatch '\A[0-9a-f]{32}\z' -or $latest.run_id -cne $RunId -or
            -not $latest.ContainsKey('execution_id') -or $latest.execution_id -cne $ExecutionId) {
            throw 'Teardown rejected: creation does not belong to the current accepted Execute invocation.'
        }
        try { [void](Invoke-StageInfra $Settings 'destroy' $Directory) }
        catch { $failures.Add([InvalidOperationException]::new((Get-StageFailureSummary $_.Exception 'destroy'))) }
        # Verification is read-only and must still run when guarded deletion/waiting fails.
        try {
            $clean=Invoke-StageInfra $Settings 'cleanup' $Directory
            $clean|ConvertTo-Json|Set-Content (Join-Path $Directory 'cleanup.json'); Protect-StageFile (Join-Path $Directory 'cleanup.json')
        } catch { $failures.Add([InvalidOperationException]::new((Get-StageFailureSummary $_.Exception 'cleanup'))) }
    }
    if ($failures.Count) { throw [AggregateException]::new('Staging teardown failed; deletion and verification outcomes retained.', $failures) }
}

function Invoke-StageInfra($Settings,[string]$Action,[string]$Directory,[string[]]$Extra=@()) {
    $arguments=@('-I','-B',(Join-Path $script:Here 'infrastructure.py'),$Action,'--directory',$Directory)+$Extra
    if ($Action -ne 'plan') {
        $awsPath=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'Amazon/AWSCLIV2/aws.exe'
        $awsPath=& $script:Core { param($p) Assert-RaceBibExecutable $p } $awsPath
        $arguments+=@('--aws-cli',$awsPath)
    }
    $result=& $script:Core { param($p,$a) Invoke-RaceBibNative -Executable $p -Arguments $a -TimeoutSeconds 1500 } $Settings.PythonPath $arguments
    if ($result.ExitCode -ne 0) {
        $summary=Format-StageInfrastructureFailure $result.Stdout $Action $result.ExitCode
        if ($Action -eq 'trust' -and $result.ExitCode -eq 2 -and -not $summary.Contains('sanitized diagnostic unavailable')) { return $null }
        $failure=[InvalidOperationException]::new("Staging infrastructure action failed: $summary")
        $failure.Data['RaceBibStageSafeSummary']=$summary
        throw $failure
    }
    if ($Action -ne 'cleanup') {
        foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File)) { Protect-StageFile $file.FullName }
    }
    return ($result.Stdout | ConvertFrom-Json -AsHashtable)
}

function Assert-StageTarget($Target,$Run) {
    if ($Target.run_id -cne $Run.run_id -or $Target.public_ip -eq '15.135.108.24' -or
        $Target.public_ip -eq 'racebib.vreboton.com' -or $Target.instance_id -cnotmatch '\Ai-[0-9a-f]{17}\z') { throw 'Staging identity mismatch or production target.' }
    $address=$null
    if (-not [Net.IPAddress]::TryParse($Target.public_ip,[ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'An explicit new staging IPv4 address is required.' }
}

function Invoke-StageSsh($Config,[string[]]$Arguments,[string]$InputJson='') {
    if ($Config.HostName -in @('15.135.108.24','racebib.vreboton.com')) { throw 'Production SSH target forbidden.' }
    $exe=& $script:Core { Assert-RaceBibExecutable (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32/OpenSSH/ssh.exe') }
    $info=[Diagnostics.ProcessStartInfo]::new(); $info.FileName=$exe; $info.UseShellExecute=$false
    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($name in @($info.Environment.Keys)) { if ($name -match 'TOKEN|SECRET|PASSWORD|^AWS_|^GH|^RACEBIB_STAGING_') { [void]$info.Environment.Remove($name) } }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    try {
        [void]$process.Start()
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if ($InputJson) { $process.StandardInput.Write($InputJson) }; $process.StandardInput.Close()
        if (-not $process.WaitForExit(1500000)) { $process.Kill($true); throw 'Staging SSH timed out; outcome unconfirmed, never auto-retry.' }
        return @{ExitCode=$process.ExitCode; Stdout=$stdout.GetAwaiter().GetResult(); Stderr=$stderr.GetAwaiter().GetResult()}
    } finally { $process.Dispose() }
}

function Assert-StageRejection($Result,[string]$Kind,[string]$HostName) {
    # Stderr is inspected in memory only. Never echo raw SSH output or credentials.
    $expected=switch ($Kind) {
        'forced-command' { '(?m)^Only pinned RaceBib deployment is permitted\r?$' }
        'forwarding' { '(?m)^channel 0: open failed: administratively prohibited: open failed\r?$' }
        'host-pin' { '(?s)REMOTE HOST IDENTIFICATION HAS CHANGED!.*Host key verification failed\.' }
        'identity' { '(?m)^racebib-deploy@'+[regex]::Escape($HostName)+': Permission denied \(publickey\)\.\r?$' }
        default { throw 'Unknown negative test category.' }
    }
    $code=if ($Kind -eq 'forced-command') { 1 } else { 255 }
    if ($Result.ExitCode -ne $code -or $Result.Stdout -or $Result.Stderr -notmatch $expected -or
        $Result.Stderr -match 'Connection timed out|Connection refused|Connection reset|Connection closed|Could not resolve|Bad configuration|unknown option|Bad owner|invalid format') {
        throw ('SSH negative test inconclusive or failed: '+$Kind+'. No recovery or security acceptance is permitted.')
    }
}

function Read-StageBarrierEnvelope($Result) {
    # This is an exact transport envelope, never evidence of the engine's rejection reason.
    $announcement=[regex]::Match($Result.Stdout,'\ARoot-only deployment log: (ssh-deployment-[a-z0-9_]{8}\.log)\r?\n\z')
    if ($Result.ExitCode -ne 1 -or $Result.Stderr -cnotmatch '\ADeployment unconfirmed; inspect host maintenance state and root-only logs before retrying\.\r?\n\z' -or
        -not $announcement.Success) {
        throw 'Maintenance probe SSH envelope is unexpected or inconclusive; recovery forbidden.'
    }
    return $announcement.Groups[1].Value
}

function Assert-StageBarrierReceipt($Receipt,$Proof,[string]$ProbeNonce,[string]$LogName) {
    $fields=@('protocol','deployment','reason','run_id','instance_id','source_sha','release_tag','failure_nonce','probe_nonce','state_digest','log_name','log_digest')
    if ($null -eq $Receipt -or $Receipt.Count -ne $fields.Count -or @($fields|Where-Object{-not $Receipt.ContainsKey($_)}).Count -ne 0 -or
        $Receipt.protocol -cne 'racebib-stage-barrier-v2' -or $Receipt.deployment -cne 'rejected' -or $Receipt.reason -cne 'maintenance-barrier' -or
        $Receipt.failure_nonce -cne $Proof.nonce -or $Receipt.probe_nonce -cne $ProbeNonce -or $Receipt.log_name -cne $LogName -or
        $Receipt.log_digest -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Authentic maintenance rejection receipt missing or malformed; recovery forbidden.' }
    foreach ($name in @('run_id','instance_id','source_sha','release_tag','state_digest')) {
        if ($Receipt[$name] -cne $Proof[$name]) { throw 'Maintenance rejection differs from the exact synthetic failure; recovery forbidden.' }
    }
}

function ConvertFrom-StageBarrierResponse([string]$Json,[string]$Action) {
    $fields=switch ($Action) {
        'prepare-barrier-probe' { @('protocol','phase','probe_nonce') }
        'verify-barrier-probe' { @('protocol','deployment','reason','run_id','instance_id','source_sha','release_tag','failure_nonce','probe_nonce','state_digest','log_name','log_digest') }
        default { throw 'Unknown barrier response schema.' }
    }
    return (& $script:Core { param($text,$names) ConvertFrom-RaceBibEvidenceJson $text $names } $Json $fields)
}

function Test-StageMaintenanceBarrier($Config,$Settings,$Run,$Target,$Proof) {
    $probeNonce=[guid]::NewGuid().ToString('N')
    $additional=@{failure_nonce=$Proof.nonce;probe_nonce=$probeNonce}
    $prepared=Invoke-StageControl $Config $Settings $Run $Target 'prepare-barrier-probe' $additional
    if ($prepared.Count -ne 3 -or $prepared.protocol -cne 'racebib-stage-barrier-v2' -or $prepared.phase -cne 'prepared' -or $prepared.probe_nonce -cne $probeNonce) {
        throw 'Fresh maintenance probe was not established; no deployment submitted.'
    }
    $logName=Invoke-StageDeploy $Config $Settings $Run $Target $null -ExpectFailure
    $additional.log_name=$logName
    $receipt=Invoke-StageControl $Config $Settings $Run $Target 'verify-barrier-probe' $additional
    Assert-StageBarrierReceipt $receipt $Proof $probeNonce $logName
    return $probeNonce
}

function Assert-StageFailureProof($Proof,$Run,$Target,$Settings,[string]$Nonce) {
    $tag=if ($Settings.RecoveryTag) { $Settings.RecoveryTag } else { 'racebib-'+$Settings.SourceSha }
    if ($null -eq $Proof -or $Proof.Count -ne 7 -or $Proof.failure_id -cne 'racebib-stage-backup-v1' -or
        $Proof.nonce -cne $Nonce -or $Proof.run_id -cne $Run.run_id -or $Proof.instance_id -cne $Target.instance_id -or
        $Proof.source_sha -cne $Settings.SourceSha -or $Proof.release_tag -cne $tag -or $Proof.state_digest -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Exact synthetic backup failure evidence missing or mismatched; recovery forbidden.'
    }
}

function Get-StageArguments($Config,$Settings,[string]$Command) {
    $arguments=@(& $script:Core { param($c,$s,$t) Get-RaceBibSshArguments $c $s $t } $Config $Settings.SourceSha $Settings.RecoveryTag)
    $arguments[-1]=$Command
    return $arguments
}

function Invoke-StageControl($Config,$Settings,$Run,$Target,[string]$Action,$Additional=@{}) {
    $payload=@{action=$Action; run_id=$Run.run_id; instance_id=$Target.instance_id; source_sha=$Settings.SourceSha; release_tag=$Settings.RecoveryTag}
    foreach ($name in $Additional.Keys) { $payload[$name]=$Additional[$name] }
    $admin=$Config.Clone(); $admin.UserName='racebib-rehearsal'; $admin.PrivateKeyPath=Join-Path $Settings.Directory 'bootstrap-key'
    [void](& $script:Core { param($p) Assert-RaceBibPrivateFile $p } $admin.PrivateKeyPath)
    # Revalidate AWS stack/new-instance identity before EVERY network submission.
    $live=Invoke-StageInfra $Settings 'inspect' $Settings.Directory
    Assert-StageTarget $live $Run
    if ($live.instance_id -cne $Target.instance_id -or $live.public_ip -cne $Config.HostName) { throw 'Staging target changed.' }
    $result=Invoke-StageSsh $admin (Get-StageArguments $admin $Settings 'stage-control') ($payload|ConvertTo-Json -Depth 12 -Compress)
    if ($result.ExitCode -ne 0) { throw "Staging control failed: $Action. No automatic retry; inspect state." }
    if ($Action -in @('prepare-barrier-probe','verify-barrier-probe')) {
        if ($result.Stderr) { throw 'Unexpected barrier-control diagnostic; recovery forbidden.' }
        return (ConvertFrom-StageBarrierResponse $result.Stdout $Action)
    }
    return ($result.Stdout|ConvertFrom-Json -AsHashtable)
}

function Invoke-StageDeploy($Config,$Settings,$Run,$Target,$Evidence,[switch]$ExpectFailure) {
    $live=Invoke-StageInfra $Settings 'inspect' $Settings.Directory; Assert-StageTarget $live $Run
    if ($live.instance_id -cne $Target.instance_id -or $live.public_ip -cne $Config.HostName) { throw 'Staging target changed.' }
    $command='deploy '+$Settings.SourceSha
    if ($Settings.RecoveryTag) { $command+=' --release-tag '+$Settings.RecoveryTag }
    $result=Invoke-StageSsh $Config (Get-StageArguments $Config $Settings $command)
    if ($ExpectFailure) {
        return (Read-StageBarrierEnvelope $result)
    }
    if ($result.ExitCode -ne 0) { throw 'Staging deployment failed or disconnected; do not automatically retry.' }
    & $script:Core { param($out,$e) Assert-RaceBibReceipt $out $e } $result.Stdout $Evidence
}

function Test-StageHttps($Config,$Settings,$Run,$Target) {
    $live=Invoke-StageInfra $Settings 'inspect' $Settings.Directory; Assert-StageTarget $live $Run
    if ($live.public_ip -cne $Config.HostName -or $live.instance_id -cne $Target.instance_id) { throw 'Staging HTTPS identity changed.' }
    if (-not ('RaceBibStagingTls' -as [type])) { Add-Type -Path (Join-Path $script:Here 'StagingTls.cs') }
    $ca=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Target.ca_pem))
    [void][RaceBibStagingTls]::Get(('https://'+$Config.HostName+'/health'),$ca)
    $html=[RaceBibStagingTls]::Get(('https://'+$Config.HostName+'/'),$ca)
    if ($html -notmatch '<div\s+id="app"') { throw 'Staging frontend shell was not served.' }
}

function Invoke-RaceBibStagingRehearsal {
    [CmdletBinding()]
    param([string]$Action,[string]$RunDirectory,[string]$SourceSha,[string]$RecoveryTag='',
          [string]$ExpectedAccount,[string]$AwsProfile,[string]$OperatorCidr,[string]$AmiId,[string]$MySqlImage,[string]$PythonPath,
          [int]$TtlMinutes=120,[decimal]$CostCeilingUsd=1,[string[]]$ProtectedResourceIds)
    if (-not $IsWindows) { throw 'The rehearsal entry point requires Windows PowerShell 7.4+.' }
    if ($Action -eq 'Plan') {
        & $script:Core { param($s,$t) Assert-RaceBibSelector $s $t } $SourceSha $RecoveryTag
        $PythonPath=& $script:Core { param($p) Assert-RaceBibExecutable $p } $PythonPath
        if ($MySqlImage -cnotmatch '\Amysql:8\.4@sha256:[0-9a-f]{64}\z') { throw 'Select an immutable tested MySQL image digest.' }
        New-StageDirectory $RunDirectory
        $keygen=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32/OpenSSH/ssh-keygen.exe'
        foreach ($name in @('bootstrap-key','deployment-key')) {
            $result=& $script:Core { param($exe,$a) Invoke-RaceBibNative $exe $a 30 -StripTokens } $keygen @('-t','ed25519','-N','','-f',(Join-Path $RunDirectory $name),'-C',('racebib-staging-'+$name))
            if ($result.ExitCode -ne 0) { throw 'Fresh staging key generation failed.' }
        }
        $input=@{account=$ExpectedAccount; profile=$AwsProfile; cidr=$OperatorCidr; ami=$AmiId; ttl=$TtlMinutes; protected_ids=@($ProtectedResourceIds);
            bootstrap_key=(Get-Content -Raw (Join-Path $RunDirectory 'bootstrap-key.pub')).Trim(); deploy_key=(Get-Content -Raw (Join-Path $RunDirectory 'deployment-key.pub')).Trim()}
        $inputPath=Join-Path $RunDirectory 'plan-input.json'; $input|ConvertTo-Json|Set-Content $inputPath
        $settings=@{SourceSha=$SourceSha;RecoveryTag=$RecoveryTag;PythonPath=$PythonPath;Directory=[IO.Path]::GetFullPath($RunDirectory);MySqlImage=$MySqlImage;CostCeilingUsd=$CostCeilingUsd}
        $settings|ConvertTo-Json|Set-Content (Join-Path $RunDirectory 'settings.json')
        $plan=Invoke-StageInfra $settings 'plan' $RunDirectory @('--input',$inputPath)
        Write-Host ('No AWS resources created. Estimated USD '+$plan.estimate_usd+' including contingency; not a billing cap. Run '+$plan.run_id)
        return $plan
    }
    foreach ($name in @('settings.json','run.json','template.json','bundle.json')) {
        [void](& $script:Core { param($p) Assert-RaceBibPrivateFile $p } (Join-Path $RunDirectory $name))
    }
    $settings=Get-Content -Raw (Join-Path $RunDirectory 'settings.json')|ConvertFrom-Json -AsHashtable
    $settings.PythonPath=& $script:Core { param($p) Assert-RaceBibExecutable $p } $settings.PythonPath
    $run=Get-Content -Raw (Join-Path $RunDirectory 'run.json')|ConvertFrom-Json -AsHashtable
    $settings.Directory=[IO.Path]::GetFullPath($RunDirectory)
    if ($Action -eq 'Destroy') { [void](Invoke-StageInfra $settings 'destroy' $RunDirectory); return (Invoke-StageInfra $settings 'cleanup' $RunDirectory) }
    if ($Action -eq 'VerifyCleanup') { return (Invoke-StageInfra $settings 'cleanup' $RunDirectory) }
    if ($Action -ne 'Execute') { throw 'Unknown staging action.' }
    $executionLock=Enter-StageExecutionLock $RunDirectory $run
    try {
    if ($run.estimate_usd -gt $settings.CostCeilingUsd) { throw 'Estimated cost exceeds the reviewed ceiling.' }
    # Fresh staging-only read tokens are never written to Terraform/CF/local state.
    if (-not $env:RACEBIB_STAGING_GH_TOKEN -or -not $env:RACEBIB_STAGING_GHCR_TOKEN -or -not $env:RACEBIB_STAGING_GHCR_USERNAME) { throw 'Supply staging-only read credentials in this process environment.' }
    $old=@{GH_TOKEN=$env:GH_TOKEN;GHCR_TOKEN=$env:GHCR_TOKEN;GHCR_USERNAME=$env:GHCR_USERNAME}
    try {
        $env:GH_TOKEN=$env:RACEBIB_STAGING_GH_TOKEN; $env:GHCR_TOKEN=$env:RACEBIB_STAGING_GHCR_TOKEN; $env:GHCR_USERNAME=$env:RACEBIB_STAGING_GHCR_USERNAME
        $preflight=Join-Path $script:Here '../scripts/local_verify.py'
        $args=@('-I','-B',$preflight,$settings.SourceSha)
        if ($settings.RecoveryTag) { $args+=@('--release-tag',$settings.RecoveryTag) }
        $verified=& $script:Core { param($p,$a) Invoke-RaceBibNative $p $a 900 } $settings.PythonPath $args
        if ($verified.ExitCode -ne 0) { throw 'Immutable provenance/image verification failed; no infrastructure submitted.' }
        $evidence=& $script:Core { param($text,$s,$t) $v=ConvertFrom-RaceBibEvidenceJson $text @('source_sha','release_tag','manifest_digest','api','web'); Assert-RaceBibEvidence $v $s $t; return $v } $verified.Stdout $settings.SourceSha $settings.RecoveryTag
    } finally { $env:GH_TOKEN=$old.GH_TOKEN; $env:GHCR_TOKEN=$old.GHCR_TOKEN; $env:GHCR_USERNAME=$old.GHCR_USERNAME }
    Write-Host ('Temporary isolated stack '+$run.stack_name+'; estimate USD '+$run.estimate_usd+'; ceiling USD '+$settings.CostCeilingUsd+' is not an AWS billing cap.')
    if ((Read-Host ('Type CREATE STAGING '+$run.run_id)) -cne ('CREATE STAGING '+$run.run_id)) { throw 'Staging creation not approved.' }
    # A durable one-shot claim is created only after fresh local authorization, while holding the lock.
    $executionId=New-StageExecutionClaim $RunDirectory $run.run_id
    $primaryFailure=$null; $teardownFailure=$null; $phase='create'
    try {
        [void](Invoke-StageInfra $settings 'create' $RunDirectory @('--approved-run',$run.run_id,'--execution-id',$executionId))
        $phase='bootstrap-trust'
        $target=$null
        for ($attempt=0;$attempt -lt 90 -and $null -eq $target;$attempt++) {
            $target=Invoke-StageInfra $settings 'trust' $RunDirectory
            if ($null -eq $target) { Start-Sleep -Seconds 10 }
        }
        if ($null -eq $target) { throw 'Staging bootstrap did not complete.' }
        Assert-StageTarget $target $run
        $hostParts=$target.host_key -split '\s+'
        $hash=[Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($hostParts[1]))
        $fingerprint='SHA256:'+([Convert]::ToBase64String($hash).TrimEnd('='))
        $hosts=Join-Path $RunDirectory 'known_hosts'
        ($target.public_ip+' ssh-ed25519 '+$hostParts[1])|Set-Content $hosts
        $config=@{HostName=$target.public_ip;Port=22;UserName='racebib-deploy';PrivateKeyPath=(Join-Path $RunDirectory 'deployment-key');KnownHostsPath=$hosts;HostKeyFingerprint=$fingerprint;PythonPath=$settings.PythonPath}
        $configPath=Join-Path $RunDirectory 'staging-controller.json'; $config|ConvertTo-Json|Set-Content $configPath
        foreach ($file in @(Get-ChildItem -LiteralPath $RunDirectory -File)) { Protect-StageFile $file.FullName }
        $config=& $script:Core { param($p) Get-RaceBibConfiguration $p } $configPath
        $bundle=Get-Content -Raw (Join-Path $RunDirectory 'bundle.json')|ConvertFrom-Json -AsHashtable
        $auth=@{github_token=$env:RACEBIB_STAGING_GH_TOKEN;ghcr_token=$env:RACEBIB_STAGING_GHCR_TOKEN;ghcr_username=$env:RACEBIB_STAGING_GHCR_USERNAME}
        $phase='host-install-and-initialize'
        [void](Invoke-StageControl $config $settings $run $target 'install' @{files=$bundle;github_auth=$auth;mysql_image=$settings.MySqlImage})
        [void](Invoke-StageControl $config $settings $run $target 'initialize')
        $phase='deployment-and-acceptance'
        Invoke-StageDeploy $config $settings $run $target $evidence
        Test-StageHttps $config $settings $run $target
        foreach ($command in @('whoami','sftp','scp -t /tmp/forbidden',('deploy '+$settings.SourceSha+' --migrate'),('deploy '+$settings.SourceSha+'; id'))) {
            $live=Invoke-StageInfra $settings 'inspect' $RunDirectory; Assert-StageTarget $live $run
            $negative=Invoke-StageSsh $config (Get-StageArguments $config $settings $command)
            Assert-StageRejection $negative 'forced-command' $config.HostName
        }
        # A syntactically valid but wrong pinned host key must fail on the wire.
        $wrongHosts=Join-Path $RunDirectory 'wrong-known-hosts'
        $wrongPub=(Get-Content -Raw (Join-Path $RunDirectory 'bootstrap-key.pub')).Trim() -split '\s+'
        ($target.public_ip+' ssh-ed25519 '+$wrongPub[1])|Set-Content $wrongHosts; Protect-StageFile $wrongHosts
        $wrong=$config.Clone(); $wrong.KnownHostsPath=$wrongHosts
        $live=Invoke-StageInfra $settings 'inspect' $RunDirectory; Assert-StageTarget $live $run
        $negative=Invoke-StageSsh $wrong (Get-StageArguments $wrong $settings ('deploy '+$settings.SourceSha))
        Assert-StageRejection $negative 'host-pin' $config.HostName
        $wrong=$config.Clone(); $wrong.PrivateKeyPath=Join-Path $RunDirectory 'bootstrap-key'
        $negative=Invoke-StageSsh $wrong (Get-StageArguments $wrong $settings ('deploy '+$settings.SourceSha))
        Assert-StageRejection $negative 'identity' $config.HostName
        $forward=@(Get-StageArguments $config $settings '')
        $forward=$forward[0..($forward.Count-3)]+@('-W','127.0.0.1:8080',$forward[-2])
        $negative=Invoke-StageSsh $config $forward
        Assert-StageRejection $negative 'forwarding' $config.HostName
        $subsystem=@(Get-StageArguments $config $settings 'sftp')
        $subsystem=$subsystem[0..($subsystem.Count-3)]+@('-s',$subsystem[-2],$subsystem[-1])
        $negative=Invoke-StageSsh $config $subsystem
        Assert-StageRejection $negative 'forced-command' $config.HostName
        $before=Invoke-StageControl $config $settings $run $target 'inspect'
        if ($before.maintenance -or $before.current_sha -cne $settings.SourceSha -or $before.manifest_digest -cne $evidence.manifest_digest) { throw 'Negative tests changed successful state.' }
        $failureNonce=[guid]::NewGuid().ToString('N')
        $barrier=Invoke-StageControl $config $settings $run $target 'arm-failure' @{failure_nonce=$failureNonce}
        Assert-StageFailureProof $barrier.synthetic_failure $run $target $settings $failureNonce
        if (-not $barrier.maintenance -or $barrier.current_sha -cne $settings.SourceSha -or $barrier.manifest_digest -cne $before.manifest_digest -or $barrier.history_count -ne $before.history_count) { throw 'Failed deployment lost state/maintenance barrier.' }
        $probeNonce=Test-StageMaintenanceBarrier $config $settings $run $target $barrier.synthetic_failure
        $blocked=Invoke-StageControl $config $settings $run $target 'inspect'
        if (-not $blocked.maintenance -or $blocked.current_sha -cne $before.current_sha -or $blocked.manifest_digest -cne $before.manifest_digest -or $blocked.history_count -ne $before.history_count) { throw 'Barrier refusal did not preserve host state; no automatic recovery.' }
        [void](Invoke-StageControl $config $settings $run $target 'recover' @{failure_nonce=$failureNonce;probe_nonce=$probeNonce})
        Invoke-StageDeploy $config $settings $run $target $evidence
        Test-StageHttps $config $settings $run $target
        $state=Invoke-StageControl $config $settings $run $target 'inspect'
        if ($state.maintenance -or -not $state.api_ready -or -not $state.frontend_ready -or $state.current_sha -cne $settings.SourceSha -or $state.manifest_digest -cne $evidence.manifest_digest -or $state.history_count -ne ($before.history_count+2)) { throw 'Recovered staging state mismatch.' }
        @{run_id=$run.run_id;release=$evidence;security_negatives='passed';failure_recovery='passed';api_frontend='passed'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $RunDirectory 'acceptance.json')
        Protect-StageFile (Join-Path $RunDirectory 'acceptance.json')
    } catch {
        $primaryFailure=[InvalidOperationException]::new((Get-StageFailureSummary $_.Exception $phase))
    } finally {
        try { Complete-StageTeardown $settings $RunDirectory $run.run_id $executionId }
        catch { $teardownFailure=$_.Exception }
    }
    $failures=[Collections.Generic.List[Exception]]::new()
    if ($null -ne $primaryFailure) { $failures.Add($primaryFailure) }
    if ($null -ne $teardownFailure) { $failures.Add($teardownFailure) }
    if ($failures.Count) { throw [AggregateException]::new('Staging rehearsal failed; primary failure precedes teardown outcomes. No automatic retry.', $failures) }
    return @{run_id=$run.run_id;rehearsal='passed';cleanup='verified'}
    } finally { $executionLock.Dispose() }
}

Export-ModuleMember -Function Invoke-RaceBibStagingRehearsal
