#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Preparation','Package','Publication')][string]$Phase = 'Preparation',
    [string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$ResultDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'HexorReleaseQualification.psm1') -Force
if (Test-Path -LiteralPath $ResultDirectory) { throw 'Preserve gate evidence: ResultDirectory must be new.' }
New-Item -ItemType Directory -Path $ResultDirectory | Out-Null
$result = [ordered]@{ Passed = $false; Phase = $Phase; Version = '1.0.14'; StartedUtc = [DateTimeOffset]::UtcNow.ToString('O') }
try {
    $scope = Read-HexorConsoleScope $root (Join-Path $root '.build/approved-release-scope-20261116T034104Z/scope-77-856.json')
    $baseline = $null; $target = $null
    if ($Phase -ne 'Preparation') {
        $baseline = Read-HexorQualifiedPackage $root (Join-Path $root 'output/assessment-1.0.13/publish-20261030-01/public-release') '1.0.13'
        $target = Read-HexorQualifiedPackage $root $ReleaseDirectory '1.0.14'
    }
    $result.Integrity = Test-HexorBlockingScope $scope $baseline $target
    $audit = Get-HexorEnvironmentAudit $scope
    Write-HexorGateEvidence (Join-Path $ResultDirectory 'environment-audit.json') ([ordered]@{ RequiredEntries = 856; Records = $audit; ChangedHashesAccepted = $false })
    $result.EnvironmentFindingsRecorded = $audit.Count
    if ($Phase -ne 'Preparation') {
        $expectedChecksum = $target.Manifest.Sha256.ToLowerInvariant() + '  Hexor-1.0.14-win-x64.zip'
        if ([IO.File]::ReadAllText((Join-Path $ReleaseDirectory 'SHA256SUMS.txt')).Trim() -cne $expectedChecksum -or
            [IO.File]::ReadAllText((Join-Path $ReleaseDirectory 'RELEASE-NOTES.md')).TrimEnd() -cne $target.Manifest.ReleaseNotes.TrimEnd()) { throw 'Release notes or checksum asset differs from signed manifest.' }
        foreach ($name in @('Hexor-AION-Control-Center.exe','Hexor.Updater.exe')) {
            if ((Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory "payload/$name")).Hash -cne $target.Hashes[$name]) { throw 'Payload differs from signed ZIP.' }
        }
    }
    if ($Phase -eq 'Publication') { Assert-HexorReleaseReceipt $root $ReleaseDirectory }
    $result.Passed = $true
    Write-Output "PASS: Console $Phase gate, 77 blocking checks and 856 required environment audit records."
} catch { $result.Failure = $_.Exception.Message; throw }
finally {
    $result.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Write-HexorGateEvidence (Join-Path $ResultDirectory 'result.json') $result
}
