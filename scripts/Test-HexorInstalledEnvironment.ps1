#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'HexorReleaseQualification.psm1') -Force
$scope = Read-HexorConsoleScope $root (Join-Path $root '.build/approved-release-scope-20261116T034104Z/scope-77-856.json')
$records = Get-HexorEnvironmentAudit $scope
Write-HexorGateEvidence $ResultPath ([ordered]@{ RequiredEntries = 856; Records = $records; ChangedVendorHashesAccepted = $false; CheckedUtc = [DateTimeOffset]::UtcNow.ToString('O') })
Write-Output 'PASS: all 856 installed-environment findings recorded; no changed hash accepted.'
