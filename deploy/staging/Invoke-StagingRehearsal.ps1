#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Plan','Execute','Destroy','VerifyCleanup')][string]$Action,
    [Parameter(Mandatory)][string]$RunDirectory,
    [string]$SourceSha, [string]$RecoveryTag='', [string]$ExpectedAccount, [string]$AwsProfile,
    [string]$OperatorCidr, [string]$AmiId, [string]$MySqlImage, [string]$PythonPath,
    [string[]]$ProtectedResourceIds,
    [ValidateRange(30,180)][int]$TtlMinutes=120,
    [ValidateRange(0.1,10)][decimal]$CostCeilingUsd=1
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'RaceBib.Staging.psm1') -Force
Invoke-RaceBibStagingRehearsal @PSBoundParameters
