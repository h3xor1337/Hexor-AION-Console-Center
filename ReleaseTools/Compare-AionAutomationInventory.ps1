[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference='Stop'

function Read-Inventory([string]$Path,[string]$Label) {
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label inventory is missing: $Path. Complete Capture $Label before generating a comparison."
    }
    try {$inventory=Get-Content -Raw -LiteralPath $Path -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop}
    catch {throw "$Label inventory is not valid JSON: $Path. Capture it again before comparing. Details: $($_.Exception.Message)"}
    if($null -eq $inventory.schemaVersion -or $null -eq $inventory.files -or $null -eq $inventory.captureRoots) {
        throw "$Label inventory is incomplete or uses an unsupported schema: $Path. Capture it again before comparing."
    }
    return $inventory
}

function Get-PathKey([string]$Category,[string]$Root,[string]$RelativePath) {
    return ($Category+'|'+$Root.TrimEnd([char[]]@('\','/'))+'|'+$RelativePath).ToUpperInvariant()
}

function Get-RootSet($Inventory) {
    return @($Inventory.captureRoots | ForEach-Object {
        if([string]::IsNullOrWhiteSpace($_.category) -or [string]::IsNullOrWhiteSpace($_.root)) {
            throw 'Inventory capture roots are invalid. Recapture both inventories with the same tool version.'
        }
        ($_.category+'|'+$_.root.TrimEnd([char[]]@('\','/'))).ToUpperInvariant()
    } | Sort-Object -Unique)
}

function Write-AtomicText([string]$Text,[string]$FinalPath) {
    $temporaryPath=$FinalPath+'.partial.'+[guid]::NewGuid().ToString('N')
    try {
        Set-Content -LiteralPath $temporaryPath -Value $Text -Encoding UTF8 -ErrorAction Stop
        if(Test-Path -LiteralPath $FinalPath){[IO.File]::Replace($temporaryPath,$FinalPath,$null)}
        else {[IO.File]::Move($temporaryPath,$FinalPath)}
    }
    finally {if(Test-Path -LiteralPath $temporaryPath){Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue}}
}

$beforeInventory=Read-Inventory $Before 'BEFORE'
$afterInventory=Read-Inventory $After 'AFTER'
if([int]$beforeInventory.schemaVersion -ne [int]$afterInventory.schemaVersion) {
    throw "Inventory schema versions differ (BEFORE $($beforeInventory.schemaVersion), AFTER $($afterInventory.schemaVersion)). Recapture both inventories with the same tool version."
}
$beforeRoots=Get-RootSet $beforeInventory
$afterRoots=Get-RootSet $afterInventory
if(@(Compare-Object $beforeRoots $afterRoots).Count -gt 0) {
    throw 'BEFORE and AFTER inventories use different capture roots. Recapture using the same confirmed updater, cache, and game locations.'
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$remaining=@{}
foreach($file in $beforeInventory.files){$remaining[(Get-PathKey $file.category $file.root $file.relativePath)]=$file}
$changes=New-Object 'System.Collections.Generic.List[object]'
$unchanged=0
foreach($file in $afterInventory.files) {
    $key=Get-PathKey $file.category $file.root $file.relativePath
    if(-not $remaining.ContainsKey($key)) {
        $changes.Add([pscustomobject]@{change='Added';category=$file.category;root=$file.root;relativePath=$file.relativePath;beforeSha256=$null;afterSha256=$file.sha256;ownership='Suspected; requires review'}) | Out-Null
        continue
    }
    $old=$remaining[$key]
    if($old.sha256 -ne $file.sha256 -or $old.size -ne $file.size) {
        $changes.Add([pscustomobject]@{change='Modified';category=$file.category;root=$file.root;relativePath=$file.relativePath;beforeSha256=$old.sha256;afterSha256=$file.sha256;ownership='Suspected; requires review'}) | Out-Null
    }
    else {$unchanged++}
    $remaining.Remove($key)
}
foreach($old in $remaining.Values) {
    $changes.Add([pscustomobject]@{change='Removed';category=$old.category;root=$old.root;relativePath=$old.relativePath;beforeSha256=$old.sha256;afterSha256=$null;ownership='Suspected; requires review'}) | Out-Null
}

$changeRecords=@($changes | ForEach-Object {$_})
$summary=[ordered]@{
    schemaVersion=3;before=$Before;after=$After;generatedUtc=[DateTime]::UtcNow.ToString('o')
    counts=[ordered]@{added=@($changes|Where-Object change -eq 'Added').Count;modified=@($changes|Where-Object change -eq 'Modified').Count;removed=@($changes|Where-Object change -eq 'Removed').Count;unchanged=$unchanged}
    changes=$changeRecords
}
$json=Join-Path $OutputDirectory 'inventory-comparison.json'
Write-AtomicText ($summary|ConvertTo-Json -Depth 8) $json
$lines=New-Object 'System.Collections.Generic.List[string]'
$lines.Add('# AION automation inventory comparison') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("Added: $($summary.counts.added); Modified: $($summary.counts.modified); Removed: $($summary.counts.removed); Unchanged: $($summary.counts.unchanged)") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| Change | Category | Relative path | Review status |') | Out-Null
$lines.Add('| --- | --- | --- | --- |') | Out-Null
foreach($change in $changes){$lines.Add("| $($change.change) | $($change.category) | $($change.relativePath) | $($change.ownership) |") | Out-Null}
$report=Join-Path $OutputDirectory 'inventory-comparison.md'
Write-AtomicText ($lines -join [Environment]::NewLine) $report
Write-Host "Comparison completed successfully. JSON: $json" -ForegroundColor Green
Write-Host "Markdown report: $report"
Write-Host "Added: $($summary.counts.added); Modified: $($summary.counts.modified); Removed: $($summary.counts.removed); Unchanged: $($summary.counts.unchanged)"
