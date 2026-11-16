[CmdletBinding()]
param(
    [string]$EvidenceDirectory = (Join-Path $env:LOCALAPPDATA 'HexorAion\DevelopmentInventory'),
    [ValidateSet('Before','After','Compare')][string]$Action,
    [string]$UpdaterDirectory,
    [string[]]$GameDirectory,
    [string]$ApplicationDataDirectory,
    [switch]$NonInteractive
)

$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
$here=$PSScriptRoot
$capture=Join-Path $here 'Capture-AionAutomationInventory.ps1'
$compare=Join-Path $here 'Compare-AionAutomationInventory.ps1'
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$logPath=Join-Path $EvidenceDirectory 'guided-inventory.log'
$games=New-Object 'System.Collections.Generic.List[string]'
foreach($path in @($GameDirectory)){if($path){$games.Add($path) | Out-Null}}
$appDataWasConfirmed=$PSBoundParameters.ContainsKey('ApplicationDataDirectory')
$hadFailure=$false

function Write-InventoryLog([string]$Message) {
    $line=('[{0:o}] {1}' -f [DateTime]::UtcNow,$Message)
    Add-Content -LiteralPath $script:logPath -Value $line -Encoding UTF8
}

function Get-NormalizedPath([string]$Path) {
    if([string]::IsNullOrWhiteSpace($Path)){return $null}
    try {return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path.TrimEnd([char[]]@('\','/')).ToUpperInvariant()}
    catch {return $Path.TrimEnd([char[]]@('\','/')).ToUpperInvariant()}
}

function Get-UniquePaths([string[]]$Paths) {
    $seen=@{}
    foreach($path in $Paths) {
        if([string]::IsNullOrWhiteSpace($path)){continue}
        $key=Get-NormalizedPath $path
        if(-not $seen.ContainsKey($key)){$seen[$key]=$path}
    }
    return @($seen.Values)
}

function Pick([string]$Title,[string]$Suggestion,[switch]$Required) {
    if($Suggestion){Write-Host "Suggested: $Suggestion"}
    $answer=Read-Host 'Use this path? [Y]es / [N]o choose folder / [S]kip'
    if($answer -match '^[Yy]' -and $Suggestion -and (Test-Path -LiteralPath $Suggestion)){return (Resolve-Path -LiteralPath $Suggestion).Path}
    if($answer -match '^[Nn]') {
        $dialog=New-Object Windows.Forms.FolderBrowserDialog
        $dialog.Description=$Title
        if($dialog.ShowDialog() -eq 'OK'){return $dialog.SelectedPath}
    }
    if($Required){throw 'A required folder was not selected.'}
    return $null
}

function Get-SteamCandidates {
    $roots=New-Object 'System.Collections.Generic.List[string]'
    $steam=Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
    if($steam -and $steam.SteamPath){$roots.Add($steam.SteamPath) | Out-Null}
    foreach($root in @(Get-UniquePaths $roots.ToArray())) {
        $vdf=Join-Path $root 'steamapps\libraryfolders.vdf'
        if(Test-Path -LiteralPath $vdf) {
            try {
                [regex]::Matches((Get-Content -Raw -LiteralPath $vdf), '"path"\s+"([^"]+)"') | ForEach-Object {
                    $roots.Add($_.Groups[1].Value.Replace('\\','\')) | Out-Null
                }
            }
            catch {Write-Host "Unable to read Steam library list: $($_.Exception.Message)" -ForegroundColor Yellow}
        }
    }
    $candidates=foreach($root in Get-UniquePaths $roots.ToArray()) {
        $candidate=Join-Path $root 'steamapps\common\AION2'
        if(Test-Path -LiteralPath (Join-Path $candidate 'Aion2\Binaries\Win64\Aion2.exe')){$candidate}
    }
    return @(Get-UniquePaths $candidates)
}

function Confirm-CaptureRoots {
    $uniqueGames=Get-UniquePaths $script:games.ToArray()
    $script:games.Clear();foreach($path in $uniqueGames){$script:games.Add($path) | Out-Null}
    if($script:UpdaterDirectory -and $script:games.Count -gt 0){
        return
    }
    foreach($candidate in Get-SteamCandidates) {
        if((Get-UniquePaths ($script:games.ToArray()+@($candidate))).Count -gt $script:games.Count) {
            $selected=Pick 'Confirm detected AION2 game directory' $candidate
            if($selected){$script:games.Add($selected) | Out-Null}
        }
    }
    if($script:games.Count -eq 0) {
        $manual=Pick 'Select AION2 installation directory' '' -Required
        if($manual){$script:games.Add($manual) | Out-Null}
    }
    $uniqueGames=Get-UniquePaths $script:games.ToArray()
    $script:games.Clear();foreach($path in $uniqueGames){$script:games.Add($path) | Out-Null}
    if(-not $script:UpdaterDirectory) {
        $local=[Environment]::GetFolderPath('LocalApplicationData')
        $script:UpdaterDirectory=Pick 'Confirm original updater directory' (Join-Path $local 'Programs\Hexor\AION Auto Updater') -Required
    }
    if(-not $script:appDataWasConfirmed) {
        $local=[Environment]::GetFolderPath('LocalApplicationData')
        $script:ApplicationDataDirectory=Pick 'Confirm updater application-data directory' (Join-Path $local 'Aion2Updater')
        $script:appDataWasConfirmed=$true
    }
}

function Invoke-Capture([string]$Phase) {
    Confirm-CaptureRoots
    if(-not $script:UpdaterDirectory -or $script:games.Count -eq 0){throw 'An updater directory and at least one AION2 game directory are required.'}
    Write-Host 'Starting inventory capture...' -ForegroundColor Cyan
    Write-InventoryLog "Starting $Phase capture."
    & $script:capture -Phase $Phase -OutputDirectory $script:EvidenceDirectory -UpdaterRoots $script:UpdaterDirectory -GameDirectories $script:games.ToArray() -ApplicationDataRoots @($script:ApplicationDataDirectory | Where-Object {$_})
    $path=Join-Path $script:EvidenceDirectory ('inventory-'+$Phase.ToLowerInvariant()+'.json')
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Capture did not produce a valid output file: $path"}
    try {Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -ErrorAction Stop | Out-Null}
    catch {throw "Capture output is not valid JSON: $path. Details: $($_.Exception.Message)"}
    Write-Host 'Capture completed successfully.' -ForegroundColor Green
    Write-InventoryLog "Completed $Phase capture: $path"
}

function Invoke-Comparison {
    $before=Join-Path $script:EvidenceDirectory 'inventory-before.json'
    $after=Join-Path $script:EvidenceDirectory 'inventory-after.json'
    Write-InventoryLog 'Starting comparison.'
    & $script:compare -Before $before -After $after -OutputDirectory $script:EvidenceDirectory
    Write-InventoryLog 'Completed comparison.'
}

function Invoke-Action([string]$SelectedAction) {
    try {
        if($SelectedAction -eq 'Before'){Invoke-Capture 'Before'}
        elseif($SelectedAction -eq 'After'){Invoke-Capture 'After'}
        elseif($SelectedAction -eq 'Compare'){Invoke-Comparison}
    }
    catch {
        $script:hadFailure=$true
        $message=$_.Exception.Message
        Write-Host "Operation failed: $message" -ForegroundColor Red
        Write-Host "Details were logged to: $script:logPath" -ForegroundColor Yellow
        Write-InventoryLog "Failed $SelectedAction operation: $message"
        if($script:NonInteractive){throw}
    }
}

Write-Host 'Hexor development inventory - read only. No updater, game, file, or registry value will be changed.'
Write-InventoryLog 'Launcher started.'
if($Action) {
    Invoke-Action $Action
    if($hadFailure){exit 1}
    exit 0
}
if($NonInteractive){throw 'NonInteractive mode requires -Action Before, After, or Compare.'}

while($true) {
    Write-Host ''
    Write-Host '1. Capture BEFORE'
    Write-Host '2. Capture AFTER'
    Write-Host '3. Generate Comparison Report'
    Write-Host '4. Exit'
    $choice=(Read-Host 'Choose action').Trim()
    if($choice -eq '1'){Invoke-Action 'Before';continue}
    if($choice -eq '2'){Invoke-Action 'After';continue}
    if($choice -eq '3'){Invoke-Action 'Compare';continue}
    if($choice -eq '4'){break}
    Write-Host 'Invalid menu selection. No files or registry entries were changed.' -ForegroundColor Yellow
}
if($hadFailure){exit 1}
