#Requires -Version 7.4
# No production connections: all orchestration native calls and HTTPS are mocked.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$module = Import-Module (Join-Path $repo 'deploy/windows/RaceBib.Deployment.psm1') -Force -PassThru
$fixtureBase = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$fixture = Join-Path $fixtureBase ('racebib-controller-mocks-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
try {
    & $module {
        param($Fixture)
        $script:Checks = 0
        function Assert-True($Condition, $Message) {
            if (-not $Condition) { throw ('TEST FAILED: ' + $Message) }
            $script:Checks++
        }
        function Assert-Throws([scriptblock]$Body, $Message) {
            $threw = $false
            try { & $Body | Out-Null } catch { $threw = $true }
            Assert-True $threw $Message
        }
        $source = 'a' * 40
        Assert-RaceBibSelector $source ''
        foreach ($bad in @('main', ('a'*39), ($source + "`n"), ($source + ';id'))) {
            Assert-Throws { Assert-RaceBibSelector $bad '' } 'Reject non-exact SHA'
        }
        Assert-Throws { Assert-RaceBibSelector $source '--migrate' } 'Reject flags'
        Assert-Throws { Assert-RaceBibSelector $source ('racebib-' + ('b'*40) + '-recovery-1-1') } 'Reject mismatched recovery source'
        Assert-Throws { Assert-RaceBibLocalPath '\\not-a-real-host.invalid\share\key' } 'Reject UNC before any filesystem/network access'
        foreach ($path in @('C:\keys\%h', 'C:\keys\${USERPROFILE}\pin')) {
            Assert-Throws { Assert-RaceBibSshLiteralPath $path } 'Reject OpenSSH filename expansion tokens'
        }

        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $nullAcl = [Security.AccessControl.FileSecurity]::new()
        $nullAcl.SetSecurityDescriptorSddlForm('O:' + $sid.Value + 'D:PNO_ACCESS_CONTROL')
        Assert-Throws { Assert-RaceBibDacl $nullAcl } 'Reject unrestricted null DACL without applying it to a file'
        $directoryAcl = [Security.AccessControl.DirectorySecurity]::new()
        $directoryAcl.SetOwner($sid)
        $directoryAcl.SetAccessRuleProtection($true, $false)
        $directoryAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $Fixture -AclObject $directoryAcl
        function Protect-TestFile($Path) {
            $acl = [Security.AccessControl.FileSecurity]::new()
            $acl.SetOwner($sid)
            $acl.SetAccessRuleProtection($true, $false)
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
            Set-Acl -LiteralPath $Path -AclObject $acl
        }
        $key = Join-Path $Fixture 'synthetic-key'
        $hosts = Join-Path $Fixture 'known hosts'
        $configPath = Join-Path $Fixture 'controller.json'
        [IO.File]::WriteAllText($key, 'synthetic-key-never-used-for-authentication')
        [byte[]]$blob = @([byte]0,0,0,11) + [Text.Encoding]::ASCII.GetBytes('ssh-ed25519') + @([byte]0,0,0,32) + [byte[]]::new(32)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { $fingerprint = 'SHA256:' + [Convert]::ToBase64String($sha256.ComputeHash($blob)).TrimEnd('=') }
        finally { $sha256.Dispose() }
        [IO.File]::WriteAllText($hosts, 'test.example.invalid ssh-ed25519 ' + [Convert]::ToBase64String($blob))
        $config = @{ HostName='test.example.invalid'; Port=22; UserName='racebib-deploy'; PrivateKeyPath=$key;
            KnownHostsPath=$hosts; HostKeyFingerprint=$fingerprint;
            PythonPath=(Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32/cmd.exe') } # Existing trusted executable; preflight is mocked, never run.
        [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json))
        foreach ($path in @($key,$hosts,$configPath)) { Protect-TestFile $path }
        $validated = Get-RaceBibConfiguration $configPath
        Assert-True ($validated.HostKeyFingerprint -ceq $fingerprint) 'Validate ACLs and exact fingerprint'
        $arguments = Get-RaceBibSshArguments $validated $source ''
        foreach ($option in @('StrictHostKeyChecking=yes','IdentityAgent=none','IdentitiesOnly=yes','ConnectionAttempts=1','GlobalKnownHostsFile=NUL')) {
            Assert-True ($option -in $arguments) ('Strict option ' + $option)
        }
        Assert-True ($arguments[-1] -ceq ('deploy ' + $source)) 'Only fixed deploy command'
        Assert-True ($arguments[1] -ceq 'NUL') 'Windows null configuration path'
        & "$env:WINDIR/System32/icacls.exe" $key /grant '*S-1-1-0:R' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not set synthetic unsafe test ACL.' }
        Assert-Throws { Get-RaceBibConfiguration $configPath } 'Reject readable private key'
        & "$env:WINDIR/System32/icacls.exe" $key /remove:g '*S-1-1-0' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not remove synthetic test ACL.' }
        $config.HostKeyFingerprint = 'SHA256:wrong'
        [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json))
        Assert-Throws { Get-RaceBibConfiguration $configPath } 'Reject wrong host pin'
        $config.HostKeyFingerprint = $fingerprint
        $config.HostName = '-oProxyCommand=bad'
        [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json))
        Assert-Throws { Get-RaceBibConfiguration $configPath } 'Reject endpoint option injection'
        $config.HostName = 'test.example.invalid'
        $config.Port = '22;bad'
        [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json))
        Assert-Throws { Get-RaceBibConfiguration $configPath } 'Reject port injection'
        $config.Port = 22
        $unsafePem = Join-Path $Fixture 'administrator.pem'
        [IO.File]::WriteAllText($unsafePem, 'synthetic-not-a-real-key')
        Protect-TestFile $unsafePem
        $config.PrivateKeyPath = $unsafePem
        [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json))
        Assert-Throws { Get-RaceBibConfiguration $configPath } 'Reject administrator PEM path'
        $config.PrivateKeyPath = $key
        $childDirectory = Join-Path $Fixture 'protected-child'
        [void][IO.Directory]::CreateDirectory($childDirectory)
        $childKey = Join-Path $childDirectory 'synthetic-key'
        [IO.File]::WriteAllText($childKey, 'synthetic-key')
        Protect-TestFile $childKey
        & "$env:WINDIR/System32/icacls.exe" $Fixture /grant '*S-1-1-0:(DC)' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not create synthetic ancestor ACL.' }
        Assert-Throws { Assert-RaceBibPrivateFile $childKey } 'Reject ancestor directory replacement permission'
        & "$env:WINDIR/System32/icacls.exe" $Fixture /remove:g '*S-1-1-0' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not restore synthetic ancestor ACL.' }
        [void](Assert-RaceBibPrivateFile $childKey)
        $junction = Join-Path $Fixture 'synthetic-junction'
        [void](New-Item -ItemType Junction -Path $junction -Target $childDirectory)
        Assert-Throws { Assert-RaceBibPrivateFile (Join-Path $junction 'synthetic-key') } 'Reject junction before following the credential leaf'
        Remove-Item -LiteralPath $junction -Force
        $binary = Join-Path $Fixture 'synthetic-binary.exe'
        [IO.File]::WriteAllText($binary, 'synthetic-file-never-executed')
        Protect-TestFile $binary
        & "$env:WINDIR/System32/icacls.exe" $binary /grant '*S-1-1-0:W' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not create synthetic executable ACL.' }
        Assert-Throws { Assert-RaceBibExecutable $binary } 'Reject executable writable by another identity'

        # Local-only OpenSSH parser rehearsal: -G prints config, never opens SSH.
        $ssh = Join-Path $env:WINDIR 'System32/OpenSSH/ssh.exe'
        $parserArgs = @('-G','-o','CanonicalizeHostname=no') + $arguments[0..($arguments.Count-2)]
        $parsed = Invoke-RaceBibNative $ssh $parserArgs 10 -StripTokens
        Assert-True ($parsed.ExitCode -eq 0) 'Windows OpenSSH parses paths with spaces and strict options without a connection'
        Assert-True ($parsed.Stdout.Contains($hosts.Replace('\','/'))) 'Pinned known_hosts path with spaces remains intact'
        $priorToken = $env:GH_TOKEN
        try {
            $env:GH_TOKEN = 'synthetic-token-for-environment-test'
            $environmentCheck = Invoke-RaceBibNative (Get-Process -Id $PID).Path @('-NoProfile','-NonInteractive','-Command','[Console]::Write([string]::IsNullOrEmpty($env:GH_TOKEN))') 10 -StripTokens
            Assert-True ($environmentCheck.Stdout -ceq 'True') 'Native SSH-style process does not inherit GitHub token'
        } finally { $env:GH_TOKEN = $priorToken }
        $script:MockHttpStatus = 200
        function script:Invoke-WebRequest {
            param($Uri,$Method,$MaximumRedirection,$MaximumRetryCount,[switch]$SkipCertificateCheck,$TimeoutSec,$ErrorAction)
            Assert-True ($Uri -ceq 'https://racebib.vreboton.com/health') 'Fixed HTTPS target'
            Assert-True (-not $SkipCertificateCheck -and $MaximumRedirection -eq 0 -and $MaximumRetryCount -eq 0) 'TLS verification and no redirects/retries'
            return @{ StatusCode=$script:MockHttpStatus }
        }
        Test-RaceBibHttps
        foreach ($status in @(301,500)) {
            $script:MockHttpStatus = $status
            Assert-Throws { Test-RaceBibHttps } 'Reject non-200 HTTPS without any real request'
        }

        $script:FakeConfig = $config
        $script:FakeEvidence = @{ source_sha=$source; release_tag=('racebib-' + $source); manifest_digest=('sha256:' + ('b'*64));
            api=('ghcr.io/vreboton/racebib-api@sha256:' + ('c'*64)); web=('ghcr.io/vreboton/racebib-web@sha256:' + ('d'*64)) }
        $completeReceipt = $script:FakeEvidence.Clone()
        $completeReceipt.deployment = 'succeeded'
        $completeReceipt.host_state = 'verified'
        $completeReceipt.schema_hash = 'e'*64
        $receiptLine = 'RACEBIB_RESULT ' + ($completeReceipt | ConvertTo-Json -Compress)
        Assert-RaceBibReceipt $receiptLine $script:FakeEvidence
        $duplicateReceipt = 'RACEBIB_RESULT {"manifest_digest":"wrong",' + $receiptLine.Substring(16)
        foreach ($badReceipt in @('', ($receiptLine + "`n" + $receiptLine), 'RACEBIB_RESULT invalid', $duplicateReceipt,
                $receiptLine.Replace(('c'*64), ('f'*64)), $receiptLine.Replace(('e'*64), 'bad'),
                $receiptLine.Replace(('racebib-' + $source), ('racebib-' + ('f'*40))))) {
            Assert-Throws { Assert-RaceBibReceipt $badReceipt $script:FakeEvidence } 'Reject malformed, duplicate or mismatched host evidence'
        }
        $script:Calls = @()
        $script:Scenario = 'success'
        $script:ConfigReads = 0
        function script:Get-RaceBibConfiguration {
            param($ConfigPath)
            $script:ConfigReads++
            $value = $script:FakeConfig.Clone()
            if ($script:Scenario -eq 'config-changed' -and $script:ConfigReads -gt 1) { $value.Port = 2222 }
            if ($script:Scenario -eq 'pin-changed' -and $script:ConfigReads -gt 1) { throw 'Mock pin/ACL changed' }
            return $value
        }
        function script:Read-Host {
            param($Prompt)
            $script:Calls += 'authorization'
            if ($script:Scenario -eq 'denied') { return 'NO' }
            return ('DEPLOY ' + $script:FakeEvidence.source_sha + ' ' + $script:FakeEvidence.manifest_digest)
        }
        function script:Invoke-RaceBibNative {
            param($Executable,$Arguments,$TimeoutSeconds,[switch]$StripTokens)
            if ($Executable.EndsWith('ssh.exe')) {
                $script:Calls += 'ssh'
                Assert-True ([bool]$StripTokens) 'Strip tokens from SSH environment'
                if ($script:Scenario -eq 'disconnect') { throw 'Mock disconnect' }
                if ($script:Scenario -eq 'ssh-failed') { return @{ ExitCode=255; Stdout='' } }
                if ($script:Scenario -eq 'missing-receipt') { return @{ ExitCode=0; Stdout='no receipt' } }
                $receipt = $script:FakeEvidence.Clone()
                $receipt.deployment = 'succeeded'
                $receipt.host_state = 'verified'
                $receipt.schema_hash = 'e'*64
                if ($script:Scenario -eq 'wrong-state') { $receipt.manifest_digest = 'sha256:' + ('f'*64) }
                return @{ ExitCode=0; Stdout=('RACEBIB_RESULT ' + ($receipt | ConvertTo-Json -Compress)) }
            }
            $script:Calls += 'preflight'
            if ($script:Scenario -eq 'preflight-failed') { return @{ ExitCode=1; Stdout='' } }
            $evidence = $script:FakeEvidence.Clone()
            if ($script:Scenario -eq 'bad-preflight') { $evidence.api = 'latest' }
            return @{ ExitCode=0; Stdout=($evidence | ConvertTo-Json -Compress) }
        }
        function script:Test-RaceBibHttps {
            $script:Calls += 'https'
            if ($script:Scenario -eq 'https-failed') { throw 'Mock TLS failure' }
        }
        foreach ($scenario in @('success','denied','preflight-failed','bad-preflight','config-changed','pin-changed','ssh-failed','disconnect','missing-receipt','wrong-state','https-failed')) {
            $script:Scenario = $scenario
            $script:Calls = @()
            $script:ConfigReads = 0
            if ($scenario -eq 'success') {
                $result = Invoke-RaceBibLocalDeployment -SourceSha $source -ConfigPath 'mock-only'
                Assert-True ($result.HostState -eq 'verified' -and $result.Https -eq 'verified') 'Combined success requires all evidence'
                Assert-True (($script:Calls -join ',') -eq 'preflight,authorization,ssh,https') 'Verified ordering'
            } else {
                Assert-Throws { Invoke-RaceBibLocalDeployment -SourceSha $source -ConfigPath 'mock-only' } ('Fail closed: ' + $scenario)
            }
            $sshCalls = @($script:Calls | Where-Object { $_ -eq 'ssh' }).Count
            Assert-True ($sshCalls -le 1) 'Never retry SSH'
            if ($scenario -in @('denied','preflight-failed','bad-preflight','config-changed','pin-changed')) { Assert-True ($sshCalls -eq 0) 'No submission before verification and authorization' }
            if ($scenario -in @('ssh-failed','disconnect','missing-receipt','wrong-state')) {
                Assert-True ('https' -notin $script:Calls) 'HTTP cannot substitute for host-state evidence'
            }
        }
        $script:Scenario = 'success'
        $script:Calls = @()
        $script:FakeEvidence.release_tag = 'racebib-' + $source + '-recovery-123-1'
        [void](Invoke-RaceBibLocalDeployment -SourceSha $source -RecoveryTag $script:FakeEvidence.release_tag -ConfigPath 'mock-only')
        Assert-True (($script:Calls -join ',') -eq 'preflight,authorization,ssh,https') 'Validated recovery selector rehearsal'
        Write-Output ('PASS: ' + $script:Checks + ' Windows ACL, pinning, parser and mock orchestration assertions; no production connection.')
    } $fixture
} finally {
    Remove-Module $module -Force
    # Remove only the verified UUID-scoped fixture created by this test.
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempPrefix = [IO.Path]::GetFullPath($fixtureBase).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'racebib-controller-mocks-*') { throw 'Unsafe test fixture cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
