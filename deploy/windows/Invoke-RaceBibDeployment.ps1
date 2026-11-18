#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceSha,
    [string]$RecoveryTag = '',
    [string]$ConfigPath = $env:RACEBIB_DEPLOY_CONFIG
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'RaceBib.Deployment.psm1') -Force
Invoke-RaceBibLocalDeployment -SourceSha $SourceSha -RecoveryTag $RecoveryTag -ConfigPath $ConfigPath
