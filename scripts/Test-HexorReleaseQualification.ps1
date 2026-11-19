#requires -Version 7.0
# Isolated failure tests. Never modifies the live approved scope or customer files.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$module = Import-Module (Join-Path $PSScriptRoot 'HexorReleaseQualification.psm1') -Force -PassThru
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('HexorQualificationTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$script:passed = 0
function Check([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:passed++; Write-Output "PASS: $Message"
}
function Reject([scriptblock]$Action,[string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Check $rejected $Message
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path).Hash }
try {
    $policy = Join-Path $root '.build/approved-release-scope-20261116T034104Z/scope-77-856.json'
    $real = Read-HexorConsoleScope $root $policy
    Check ($real.Entries.Count -eq 933) 'Exact authorized scope preserves all 933 historical records'
    $tampered = Join-Path $sandbox 'scope.json'
    ([IO.File]::ReadAllText($policy) + ' ') | Set-Content -LiteralPath $tampered
    Reject { Read-HexorConsoleScope $root $tampered } 'Any approved scope byte change is rejected'
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($index in 1..73) {
        $file = Join-Path $sandbox "artifact-$index.bin"; [IO.File]::WriteAllText($file,"immutable-$index")
        $entries.Add([pscustomobject]@{ Path=$file; ApprovedHistoricalSHA256=(Hash $file); ProposedQualification='RequiredExactHistoricalArtifactHash' })
    }
    $install = Join-Path $sandbox 'install'; $backup = Join-Path $install '.hexor-update'
    New-Item -ItemType Directory -Path $backup | Out-Null
    $baselineHashes = @{}; $targetHashes = @{}; $historicalBytes = @{}
    foreach ($name in @('Hexor-AION-Control-Center.exe','Hexor.Updater.exe')) {
        $current = Join-Path $install $name; [IO.File]::WriteAllText($current,"v13-$name")
        $baselineHashes[$name] = Hash $current
        $entries.Add([pscustomobject]@{ Path=$current; ApprovedHistoricalSHA256=(Hash $current); ProposedQualification='RequiredVersionAwareConsoleArtifact' })
        $historicalBytes[$current] = [IO.File]::ReadAllBytes($current)
        $old = Join-Path $backup $name; [IO.File]::WriteAllText($old,"v12-$name")
        $entries.Add([pscustomobject]@{ Path=$old; ApprovedHistoricalSHA256=(Hash $old); ProposedQualification='RequiredVersionAwareConsoleArtifact' })
        $historicalBytes[$old] = [IO.File]::ReadAllBytes($old)
        $target = Join-Path $sandbox "v14-$name"; [IO.File]::WriteAllText($target,"v14-$name")
        $targetHashes[$name] = Hash $target
    }
    $fake = [pscustomobject]@{ Entries=$entries.ToArray() }
    Check ((Test-HexorBlockingScope $fake).BlockingChecks -eq 77) 'All 77 fixture artifact checks pass with exact historical bytes'
    $fixed = $entries[0].Path; $fixedBytes = [IO.File]::ReadAllBytes($fixed)
    [IO.File]::WriteAllText($fixed,'tampered trust/artifact')
    Reject { Test-HexorBlockingScope $fake } 'Changed release artifact or pinned-trust hash blocks qualification'
    [IO.File]::WriteAllBytes($fixed,$fixedBytes)
    Remove-Item -LiteralPath $fixed
    Reject { Test-HexorBlockingScope $fake } 'Missing mandatory artifact blocks qualification'
    [IO.File]::WriteAllBytes($fixed,$fixedBytes)
    Reject { Test-HexorBlockingScope ([pscustomobject]@{ Entries=$entries.ToArray()[1..76] }) } 'Dropping one mandatory artifact is rejected'
    foreach ($name in $baselineHashes.Keys) {
        [IO.File]::WriteAllText((Join-Path $install $name),"v14-$name")
        [IO.File]::WriteAllText((Join-Path $backup $name),"v13-$name")
    }
    $base = [pscustomobject]@{ Hashes=$baselineHashes }; $target = [pscustomobject]@{ Hashes=$targetHashes }
    Check ((Test-HexorBlockingScope $fake $base $target).InstalledState -eq 'AuthorizedUpdatedWithPreviousVersionBackups') 'Authorized v14 bytes and v13 backups pass version-aware update validation'
    Reject { Test-HexorBlockingScope $fake } 'Updated bytes cannot pass without authenticated target/baseline hash maps'
    [IO.File]::WriteAllText((Join-Path $install 'Hexor.Updater.exe'),'v13-Hexor.Updater.exe')
    Reject { Test-HexorBlockingScope $fake $base $target } 'Mixed app/helper release versions block qualification'
    [IO.File]::WriteAllText((Join-Path $install 'Hexor-AION-Control-Center.exe'),'v13-Hexor-AION-Control-Center.exe')
    Check ((Test-HexorBlockingScope $fake $base $target).InstalledState -eq 'RecoveredPreviousVersionWithTransactionBackups') 'Recovered v13 bytes and transaction backups pass rollback validation'
    [IO.File]::WriteAllText((Join-Path $backup 'Hexor.Updater.exe'),'wrong backup')
    Reject { Test-HexorBlockingScope $fake $base $target } 'Invalid rollback backup blocks qualification'
    foreach ($index in 1..856) { $entries.Add([pscustomobject]@{Path=(Join-Path $sandbox "missing-environment-$index");ApprovedHistoricalSHA256=('0'*64);ProposedQualification='RequiredInstalledEnvironmentReport'}) }
    $audit = Get-HexorEnvironmentAudit ([pscustomobject]@{Entries=$entries.ToArray()})
    Check ($audit.Count -eq 856 -and @($audit | Where-Object ChangedHashAccepted).Count -eq 0) 'All 856 missing/changed environment findings are retained without accepting hashes'
    Reject { Get-HexorEnvironmentAudit ([pscustomobject]@{Entries=$entries.ToArray()[0..931]}) } 'Omitting an environment audit entry is rejected'
    $evidence = Join-Path $sandbox 'evidence.json'; Write-HexorGateEvidence $evidence @{Passed=$true}
    Reject { Write-HexorGateEvidence $evidence @{Passed=$false} } 'Existing qualification evidence cannot be overwritten'
    Reject { Assert-HexorReleaseReceipt $root $sandbox } 'Missing complete release/signing receipt blocks publication'
    $sourceRecords = @(); $requiredPaths = @()
    foreach ($index in 1..94) {
        $path = Join-Path $sandbox "source-$index.cs"; [IO.File]::WriteAllText($path,"qualified source $index")
        $requiredPaths += $path
        $sourceRecords += [pscustomobject]@{Path=$path;SHA256=(Hash $path)}
    }
    & $module {param($records,$paths) Assert-HexorSourceReceipt $records $paths} $sourceRecords $requiredPaths
    Check $true 'All 94 mandatory source paths are independently covered and hash verified'
    $actualInputs = @(& $module {param($p) Get-HexorQualificationInputs $p} $root)
    Check ($actualInputs.Count -eq 94 -and @($actualInputs | Where-Object { $_ -isnot [string] }).Count -eq 0) 'Actual production discovery enumerates the unchanged set of 94 individual string paths'
    $oneMissing = $null
    try { & $module {param($records,$paths) Assert-HexorSourceReceipt $records $paths} $sourceRecords[1..93] $requiredPaths } catch { $oneMissing=$_.Exception.Message }
    Check ($null -ne $oneMissing -and $oneMissing.Contains($requiredPaths[0])) 'One missing source fails and identifies its exact normalized path'
    $manyMissing = $null
    try { & $module {param($records,$paths) Assert-HexorSourceReceipt $records $paths} $sourceRecords[2..93] $requiredPaths } catch { $manyMissing=$_.Exception.Message }
    Check ($null -ne $manyMissing -and $manyMissing.Contains($requiredPaths[0]) -and $manyMissing.Contains($requiredPaths[1])) 'Multiple missing sources fail and identify every missing path'
    Reject { & $module {param($paths) Assert-HexorSourceReceipt @() $paths} $requiredPaths } 'Empty source receipt rejected'
    Reject { & $module {param($records) Assert-HexorSourceReceipt $records @()} $sourceRecords } 'Empty mandatory source inventory rejected'
    Reject { & $module {param($paths) Assert-HexorSourceReceipt @([pscustomobject]@{SHA256=('0'*64)}) $paths} $requiredPaths } 'Malformed source records without a path rejected'
    Reject { & $module {param($paths,$path) Assert-HexorSourceReceipt @([pscustomobject]@{Path=$path;SHA256='not-a-hash'}) $paths} $requiredPaths $requiredPaths[0] } 'Malformed source digests rejected'
    Reject { & $module {param($records,$paths) Assert-HexorSourceReceipt $records $paths} (@($sourceRecords[1..93]) + @($sourceRecords[1])) $requiredPaths } 'Duplicate entries cannot hide a missing required path'
    $extra = Join-Path $sandbox 'extra.cs'; [IO.File]::WriteAllText($extra,'extra qualified input')
    $withExtra = @($sourceRecords) + @([pscustomobject]@{Path=$extra;SHA256=(Hash $extra)})
    & $module {param($records,$paths) Assert-HexorSourceReceipt $records $paths} $withExtra $requiredPaths
    Check $true 'Unexpected extra receipt entries remain permitted only with verified provenance'
    [IO.File]::WriteAllText($extra,'modified extra')
    Reject { & $module {param($records,$paths) Assert-HexorSourceReceipt $records $paths} $withExtra $requiredPaths } 'Modified unexpected source entry fails its hash check'
    [IO.File]::WriteAllText($requiredPaths[0],'modified required source')
    Reject { & $module {param($records,$paths) Assert-HexorSourceReceipt $records $paths} $sourceRecords $requiredPaths } 'Modified required source entry fails independently'
    $exception = Join-Path $root '.build/final-v1.0.14-20261116/unsigned-distribution-owner-exception.json'
    $exceptionCopy = Join-Path $sandbox 'unsigned-exception.json'
    ([IO.File]::ReadAllText($exception) + ' ') | Set-Content -LiteralPath $exceptionCopy
    Reject { Test-HexorUnsignedV1014 $exceptionCopy $sandbox } 'Any unsigned authorization modification is rejected'
    $unsigned = Join-Path $sandbox 'unsigned'; New-Item -ItemType Directory -Path $unsigned | Out-Null
    foreach ($name in @('Hexor-AION-Control-Center.exe','Hexor.Updater.exe')) {
        Copy-Item -LiteralPath (Join-Path $root '.build/final-v1.0.14-20261116/candidate/payload/Hexor-AION-Control-Center.exe') -Destination (Join-Path $unsigned $name)
    }
    $unsignedResult = Test-HexorUnsignedV1014 $exception $unsigned
    Check ($unsignedResult.Count -eq 2 -and @($unsignedResult | Where-Object Status -CNE 'NotSigned').Count -eq 0) 'Exact v14-only unsigned authorization verifies only the two named v14 fixture files'
    Copy-Item -LiteralPath (Join-Path $root 'output/assessment-1.0.13/shipped/Hexor.Updater.exe') -Destination (Join-Path $unsigned 'Hexor.Updater.exe') -Force
    Reject { Test-HexorUnsignedV1014 $exception $unsigned } 'The v14 unsigned exception cannot authorize a previous-version binary'
    $public = Read-HexorQualifiedPackage $root (Join-Path $root 'output/assessment-1.0.13/publish-20261030-01/public-release') '1.0.13'
    Check ($public.Hashes.Count -eq 2) 'Real v13 signed package has exact two-file allowlist and both correct versions'
    Copy-Item -LiteralPath (Join-Path $root 'output/assessment-1.0.13/publish-20261030-01/public-release/latest.json') -Destination (Join-Path $sandbox 'latest.json')
    $envelope = Get-Content (Join-Path $sandbox 'latest.json') -Raw | ConvertFrom-Json
    $envelope.Signature = [Convert]::ToBase64String([byte[]]::new(384)); $envelope | ConvertTo-Json | Set-Content (Join-Path $sandbox 'latest.json')
    Reject { Read-HexorQualifiedPackage $root $sandbox '1.0.13' } 'Invalid RSA-PSS manifest signature blocks package qualification'
    Write-Output "ALL $script:passed QUALIFICATION CHECKS PASSED"
} finally {
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolved) -notlike 'HexorQualificationTests-*') { throw 'Unsafe fixture cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
