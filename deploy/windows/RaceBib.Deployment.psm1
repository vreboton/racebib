#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Assert-RaceBibSelector {
    param([string]$SourceSha, [string]$RecoveryTag)
    if ($SourceSha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'A full lowercase immutable source SHA is required.' }
    if ($RecoveryTag -and $RecoveryTag -cnotmatch ('\Aracebib-' + $SourceSha + '-recovery-[1-9][0-9]*-[1-9][0-9]*\z')) {
        throw 'Recovery tag must match the exact source SHA and verified recovery format.'
    }
}

function Get-RaceBibLocalFile {
    param([string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Protected files require absolute paths.' }
    Assert-RaceBibLocalPath $Path
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $allowed = @($sid, 'S-1-5-18', 'S-1-5-32-544')
    $normalized = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetPathRoot($normalized)
    $components = @($normalized.Substring($rootPath.Length).Split([char[]]@('\','/'), [StringSplitOptions]::RemoveEmptyEntries))
    $cursorPath = $rootPath
    # Validate from the drive root down BEFORE following the next component.
    # Checking links only after opening the leaf can already follow a UNC junction.
    for ($index = -1; $index -lt $components.Count; $index++) {
        if ($index -ge 0) { $cursorPath = Join-Path $cursorPath $components[$index] }
        $cursor = Get-Item -LiteralPath $cursorPath -Force
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse paths are not permitted.' }
        if ($cursor.PSIsContainer) {
            Assert-RaceBibDirectoryAcl $cursor.FullName $allowed ($index -eq ($components.Count - 2))
        }
    }
    $file = $cursor
    if ($file.PSIsContainer) { throw 'Expected a protected regular file.' }
    return $file
}

function Assert-RaceBibPrivateFile {
    param([string]$Path)
    $file = Get-RaceBibLocalFile $Path
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $allowed = @($sid, 'S-1-5-18', 'S-1-5-32-544')
    $full = $file.FullName
    $repoPrefix = $script:RepoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($full.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Configuration and credentials must remain outside the repository.' }
    $acl = Get-Acl -LiteralPath $full
    Assert-RaceBibDacl $acl
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sid -or -not $acl.AreAccessRulesProtected) {
        throw 'Protected files must be owned by the current operator with inheritance disabled.'
    }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference.Value -notin $allowed) {
            throw 'Protected file ACL grants access to another identity.'
        }
    }
    return $full
}

function Assert-RaceBibExecutable {
    param([string]$Path)
    $file = Get-RaceBibLocalFile $Path
    $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $acl = Get-Acl -LiteralPath $file.FullName
    Assert-RaceBibDacl $acl
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'Executable has an untrusted owner.' }
    $writes = [Security.AccessControl.FileSystemRights]'WriteData,AppendData,WriteExtendedAttributes,WriteAttributes,Delete,ChangePermissions,TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $rule.IdentityReference.Value -notin $allowed -and ($rule.FileSystemRights -band $writes) -ne 0) {
            throw 'Executable can be modified by another identity.'
        }
    }
    return $file.FullName
}

function Assert-RaceBibDirectoryAcl {
    param([string]$Path, [string[]]$Allowed, [bool]$ImmediateParent)
    $trustedOwners = $allowed + @('S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') # TrustedInstaller
    $directoryAcl = Get-Acl -LiteralPath $Path
    Assert-RaceBibDacl $directoryAcl
    if ($directoryAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trustedOwners) {
        throw 'Credential path has an untrusted directory owner.'
    }
    $dangerous = [Security.AccessControl.FileSystemRights]'Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    if ($ImmediateParent) {
        $dangerous = $dangerous -bor [Security.AccessControl.FileSystemRights]'WriteData,AppendData,WriteExtendedAttributes,WriteAttributes'
    }
    foreach ($rule in $directoryAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if (($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $rule.IdentityReference.Value -notin $trustedOwners -and ($rule.FileSystemRights -band $dangerous) -ne 0) {
            throw 'Credential path can be replaced or modified by another identity.'
        }
    }
}

function Assert-RaceBibDacl {
    param([Security.AccessControl.FileSystemSecurity]$Acl)
    $raw = [Security.AccessControl.RawSecurityDescriptor]::new($Acl.GetSecurityDescriptorBinaryForm(), 0)
    if ($null -eq $raw.DiscretionaryAcl) { throw 'A null DACL grants unrestricted access and is not permitted.' }
}

function Assert-RaceBibLocalPath {
    param([string]$Path)
    if ($Path -notmatch '\A[A-Za-z]:[\\/]') { throw 'Only absolute local drive paths are permitted; no UNC paths.' }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Path))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { throw 'Use a fixed local drive, not a network/removable drive.' }
}

function Assert-RaceBibSshLiteralPath {
    param([string]$Path)
    if ($Path.Contains('%') -or $Path.Contains('${')) { throw 'OpenSSH filename expansion tokens are not permitted in credential paths.' }
}

function Get-RaceBibConfiguration {
    param([string]$ConfigPath)
    $protectedConfig = Assert-RaceBibPrivateFile $ConfigPath
    try { $config = Get-Content -LiteralPath $protectedConfig -Raw | ConvertFrom-Json -AsHashtable }
    catch { throw 'Invalid protected controller configuration JSON.' }
    $expected = @('HostName','Port','UserName','PrivateKeyPath','KnownHostsPath','HostKeyFingerprint','PythonPath')
    if (@(Compare-Object ($config.Keys | Sort-Object) ($expected | Sort-Object)).Count -ne 0) { throw 'Unexpected or missing configuration fields.' }
    if ($config.HostName -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9.-]{0,252}\z' -or $config.UserName -cne 'racebib-deploy' -or
        ($config.Port -isnot [long] -and $config.Port -isnot [int])) { throw 'Invalid fixed SSH endpoint configuration.' }
    if ($config.Port -lt 1 -or $config.Port -gt 65535) { throw 'Invalid SSH port.' }
    $config.PrivateKeyPath = Assert-RaceBibPrivateFile $config.PrivateKeyPath
    Assert-RaceBibSshLiteralPath $config.PrivateKeyPath
    if ([IO.Path]::GetExtension($config.PrivateKeyPath) -ieq '.pem') { throw 'Use a separate deployment OpenSSH key, never an administrator PEM.' }
    $config.KnownHostsPath = Assert-RaceBibPrivateFile $config.KnownHostsPath
    Assert-RaceBibSshLiteralPath $config.KnownHostsPath
    $config.PythonPath = Assert-RaceBibExecutable $config.PythonPath
    $lines = @(Get-Content -LiteralPath $config.KnownHostsPath | Where-Object { $_.Trim() -ne '' })
    if ($lines.Count -ne 1) { throw 'Pin exactly one Ed25519 host key.' }
    $parts = $lines[0].Trim() -split '\s+'
    $hostPattern = if ($config.Port -eq 22) { $config.HostName } else { '[' + $config.HostName + ']:' + $config.Port }
    if ($parts.Count -lt 3 -or $parts.Count -gt 4 -or $parts[0] -cne $hostPattern -or $parts[1] -cne 'ssh-ed25519') {
        throw 'known_hosts must contain only the exact endpoint and its pinned Ed25519 key.'
    }
    try { $blob = [Convert]::FromBase64String($parts[2]) } catch { throw 'Invalid pinned host key.' }
    if ($blob.Length -ne 51 -or [Convert]::ToHexString($blob[0..3]) -ne '0000000B' -or
        [Text.Encoding]::ASCII.GetString($blob[4..14]) -cne 'ssh-ed25519' -or [Convert]::ToHexString($blob[15..18]) -ne '00000020') {
        throw 'Invalid Ed25519 wire-format host key.'
    }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha256.ComputeHash($blob) } finally { $sha256.Dispose() }
    $fingerprint = 'SHA256:' + [Convert]::ToBase64String($hash).TrimEnd('=')
    if ($config.HostKeyFingerprint -cne $fingerprint) { throw 'Pinned host key fingerprint differs from the approved configuration.' }
    return $config
}

function Invoke-RaceBibNative {
    param([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds = 3900, [switch]$StripTokens)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    if ($StripTokens) {
        foreach ($name in @('GH_TOKEN','GHCR_TOKEN','GITHUB_TOKEN')) { [void]$info.Environment.Remove($name) }
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Process timed out; remote outcome may be unconfirmed.'
        }
        return @{ ExitCode = $process.ExitCode; Stdout = $stdout.GetAwaiter().GetResult() }
        # Raw stderr is deliberately never printed or returned.
    } catch { throw 'Native operation failed or timed out; inspect state before any retry.' }
    finally { $process.Dispose() }
}

function Assert-RaceBibEvidence {
    param([hashtable]$Evidence, [string]$SourceSha, [string]$RecoveryTag)
    $tag = if ($RecoveryTag) { $RecoveryTag } else { 'racebib-' + $SourceSha }
    if ($Evidence.source_sha -cne $SourceSha -or $Evidence.release_tag -cne $tag -or
        $Evidence.manifest_digest -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or
        $Evidence.api -cnotmatch '\Aghcr\.io/vreboton/racebib-api@sha256:[0-9a-f]{64}\z' -or
        $Evidence.web -cnotmatch '\Aghcr\.io/vreboton/racebib-web@sha256:[0-9a-f]{64}\z') { throw 'Verified release metadata is incomplete or mismatched.' }
}

function Get-RaceBibSshArguments {
    param([hashtable]$Config, [string]$SourceSha, [string]$RecoveryTag)
    Assert-RaceBibSelector $SourceSha $RecoveryTag
    Assert-RaceBibSshLiteralPath $Config.PrivateKeyPath
    Assert-RaceBibSshLiteralPath $Config.KnownHostsPath
    $command = 'deploy ' + $SourceSha
    if ($RecoveryTag) { $command += ' --release-tag ' + $RecoveryTag }
    $hostsOption = 'UserKnownHostsFile="' + $Config.KnownHostsPath.Replace('\', '/') + '"'
    return @('-F','NUL','-T','-p',[string]$Config.Port,'-i',$Config.PrivateKeyPath,
        '-o','BatchMode=yes','-o','IdentitiesOnly=yes','-o','IdentityAgent=none',
        '-o','StrictHostKeyChecking=yes','-o',$hostsOption,'-o','GlobalKnownHostsFile=NUL',
        '-o','HostKeyAlgorithms=ssh-ed25519','-o','VerifyHostKeyDNS=no','-o','UpdateHostKeys=no',
        '-o','ClearAllForwardings=yes','-o','ForwardAgent=no','-o','RequestTTY=no',
        '-o','PasswordAuthentication=no','-o','KbdInteractiveAuthentication=no',
        '-o','ControlMaster=no','-o','ControlPath=none','-o','ControlPersist=no',
        '-o','ConnectionAttempts=1','-o','ConnectTimeout=15',
        '-o','ServerAliveInterval=15','-o','ServerAliveCountMax=3',
        ($Config.UserName + '@' + $Config.HostName),$command)
}

function Assert-RaceBibReceipt {
    param([string]$Stdout, [hashtable]$Evidence)
    $receipts = @($Stdout -split '\r?\n' | Where-Object { $_.StartsWith('RACEBIB_RESULT ') })
    if ($receipts.Count -ne 1) { throw 'Host release-state receipt missing or ambiguous; remote outcome unconfirmed.' }
    $receipt = ConvertFrom-RaceBibEvidenceJson $receipts[0].Substring(15) @('deployment','host_state','source_sha','release_tag','manifest_digest','api','web','schema_hash')
    if ($receipt.deployment -cne 'succeeded' -or $receipt.host_state -cne 'verified' -or $receipt.schema_hash -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Host did not verify a successful idle deployment and schema.'
    }
    foreach ($name in @('source_sha','release_tag','manifest_digest','api','web')) {
        if ($receipt[$name] -cne $Evidence[$name]) { throw 'Host release state differs from independently verified release metadata.' }
    }
}

function ConvertFrom-RaceBibEvidenceJson {
    param([string]$Json, [string[]]$Fields)
    $document = $null
    try {
        $document = [Text.Json.JsonDocument]::Parse($Json)
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Expected object.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $result = @{}
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if (-not $seen.Add($property.Name) -or $property.Value.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Duplicate/non-string evidence.' }
            $result[$property.Name] = $property.Value.GetString()
        }
        if (-not $seen.SetEquals($Fields)) { throw 'Unexpected/missing fields.' }
        return $result
    } catch { throw 'Invalid, ambiguous or incomplete release evidence JSON.' }
    finally { if ($null -ne $document) { $document.Dispose() } }
}

function Confirm-RaceBibLocalAuthorization {
    param([hashtable]$Evidence, [hashtable]$Config)
    Write-Host ('Verified release: ' + $Evidence.source_sha + ' / ' + $Evidence.release_tag)
    Write-Host ('Manifest: ' + $Evidence.manifest_digest)
    Write-Host ('Target: ' + $Config.UserName + '@' + $Config.HostName + ':' + $Config.Port)
    Write-Host 'This is local operator authorization. GitHub production approval does not authorize this deployment.'
    $expected = 'DEPLOY ' + $Evidence.source_sha + ' ' + $Evidence.manifest_digest
    $answer = Read-Host ('Type exactly: ' + $expected)
    if ($answer -cne $expected) { throw 'Local deployment was not authorized; nothing submitted.' }
}

function Test-RaceBibHttps {
    # Fixed trusted endpoint, normal Windows CA/hostname validation, no redirects or retries.
    $response = Invoke-WebRequest -Uri 'https://racebib.vreboton.com/health' -Method Get -MaximumRedirection 0 -MaximumRetryCount 0 -SkipCertificateCheck:$false -TimeoutSec 30 -ErrorAction Stop
    if ([int]$response.StatusCode -ne 200) { throw 'External HTTPS check failed.' }
}

function Invoke-RaceBibLocalDeployment {
    [CmdletBinding()]
    param([string]$SourceSha, [string]$RecoveryTag = '', [string]$ConfigPath)
    if (-not $IsWindows) { throw 'Use Windows with PowerShell 7 and Windows OpenSSH.' }
    Assert-RaceBibSelector $SourceSha $RecoveryTag
    $config = Get-RaceBibConfiguration $ConfigPath
    $pythonArgs = @('-I','-B',(Join-Path $script:RepoRoot 'deploy/scripts/local_verify.py'),$SourceSha)
    if ($RecoveryTag) { $pythonArgs += @('--release-tag',$RecoveryTag) }
    $verified = Invoke-RaceBibNative -Executable $config.PythonPath -Arguments $pythonArgs -TimeoutSeconds 900
    if ($verified.ExitCode -ne 0) { throw 'Release provenance/registry verification failed; nothing submitted.' }
    $evidence = ConvertFrom-RaceBibEvidenceJson $verified.Stdout @('source_sha','release_tag','manifest_digest','api','web')
    Assert-RaceBibEvidence $evidence $SourceSha $RecoveryTag
    $approvedConfig = $config.Clone()
    Confirm-RaceBibLocalAuthorization $evidence $config
    $config = Get-RaceBibConfiguration $ConfigPath
    foreach ($field in $approvedConfig.Keys) {
        if (-not [object]::Equals($approvedConfig[$field], $config[$field])) { throw 'Configuration changed after authorization; nothing submitted.' }
    }
    $sshPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32/OpenSSH/ssh.exe'
    $sshPath = Assert-RaceBibExecutable $sshPath
    Write-Host 'Submitting one restricted SSH command. A disconnect or shutdown may leave the outcome unconfirmed.'
    $remote = Invoke-RaceBibNative -Executable $sshPath -Arguments (Get-RaceBibSshArguments $config $SourceSha $RecoveryTag) -StripTokens
    if ($remote.ExitCode -ne 0) { throw 'SSH failed/disconnected; remote outcome unconfirmed. Inspect the host before any retry.' }
    Assert-RaceBibReceipt $remote.Stdout $evidence
    Write-Host 'Host deployment and exact release state verified. Checking external HTTPS separately.'
    try { Test-RaceBibHttps } catch {
        throw 'Host deployment/state verified, external HTTPS unconfirmed. Do not redeploy or roll back automatically; investigate from the trusted PC.'
    }
    return [pscustomobject]@{ SourceSha=$SourceSha; ManifestDigest=$evidence.manifest_digest; HostState='verified'; Https='verified'; Authorization='local-operator' }
}

Export-ModuleMember -Function Invoke-RaceBibLocalDeployment
