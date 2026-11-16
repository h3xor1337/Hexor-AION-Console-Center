[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Before','After')][string]$Phase,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string[]]$UpdaterRoots,
    [Parameter(Mandatory)][string[]]$GameDirectories,
    [string[]]$ApplicationDataRoots = @(),
    [string[]]$RegistryKeys = @('HKCU:\Software\LegionCommander')
)

$ErrorActionPreference='Stop'

function Get-Sha256([string]$FilePath) {
    $algorithm=[Security.Cryptography.SHA256]::Create()
    $stream=$null
    try {
        $stream=[IO.File]::Open($FilePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-','')
    }
    finally {
        if($stream){$stream.Dispose()}
        $algorithm.Dispose()
    }
}

function Get-CanonicalRoot([string]$Path) {
    if(-not (Test-Path -LiteralPath $Path -PathType Container)){ throw "Scoped root does not exist: $Path" }
    # DirectoryInfo keeps a long, filesystem-native path. Resolve-Path can return
    # an 8.3 alias, which is incompatible with FileInfo.FullName when deriving a
    # relative path on systems where short names are enabled.
    $resolved=[IO.DirectoryInfo]::new($Path).FullName
    $trimmed=$resolved.TrimEnd([char[]]@('\','/'))
    if([string]::IsNullOrWhiteSpace($trimmed) -or ($trimmed.Length -eq 2 -and $trimmed[1] -eq ':')){return $resolved}
    return $trimmed
}

function Get-UniqueCanonicalRoots([string[]]$Paths) {
    $roots=New-Object 'System.Collections.Generic.List[string]'
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($path in $Paths) {
        if([string]::IsNullOrWhiteSpace($path)){continue}
        $canonical=Get-CanonicalRoot $path
        if($seen.Add($canonical)){$roots.Add($canonical) | Out-Null}
    }
    return $roots.ToArray()
}

function Add-SkippedPath($State,[string]$Path,[string]$Reason) {
    $State.skippedPaths.Add([pscustomobject]@{path=$Path;reason=$Reason}) | Out-Null
    Write-Host "Skipped: $Path ($Reason)" -ForegroundColor Yellow
}

function Get-ScopedFiles([string]$Root,[string]$Category,$State) {
    $canonical=Get-CanonicalRoot $Root
    $pending=New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($canonical)
    while($pending.Count -gt 0) {
        $directory=$pending.Pop()
        Write-Host "Scanning $Category directory: $directory"
        try {$entries=[IO.Directory]::EnumerateFileSystemEntries($directory)}
        catch {Add-SkippedPath $State $directory $_.Exception.Message;continue}
        foreach($entry in $entries) {
            try {
                $attributes=[IO.File]::GetAttributes($entry)
                if(($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    Add-SkippedPath $State $entry 'Reparse point not followed'
                    continue
                }
                if(($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    $pending.Push($entry)
                    continue
                }
                $info=New-Object IO.FileInfo $entry
                $relative=$info.FullName.Substring($canonical.Length).TrimStart('\')
                $State.filesProcessed++
                $State.bytesProcessed+=$info.Length
                Write-Host ("[{0}] {1} files, {2:N0} bytes: {3}" -f $Category,$State.filesProcessed,$State.bytesProcessed,$relative)
                # The scope is intentionally restricted to updater/cache roots and AION2 Win64.
                # Every file in those narrow areas is a candidate; extension and file size never exclude it.
                $hash=Get-Sha256 $info.FullName
                $State.hashesCalculated++
                $State.files.Add([pscustomobject]@{
                    category=$Category;root=$canonical;relativePath=$relative;size=$info.Length
                    sha256=$hash;modifiedUtc=$info.LastWriteTimeUtc.ToString('o')
                }) | Out-Null
            }
            catch {Add-SkippedPath $State $entry $_.Exception.Message}
        }
    }
}

function Get-RegistryInventory([string]$Key,$State) {
    try {
        if(-not (Test-Path -LiteralPath $Key)){return [pscustomobject]@{key=$Key;exists=$false;valueNames=@();subkeys=@()}}
        # Never collect registry values. Names matching secrets are also excluded.
        $item=Get-Item -LiteralPath $Key
        $names=@($item.GetValueNames() | Where-Object {$_ -notmatch '(?i)(license|token|auth|credential|key)'})
        return [pscustomobject]@{key=$Key;exists=$true;valueNames=$names;subkeys=@($item.GetSubKeyNames())}
    }
    catch {Add-SkippedPath $State $Key $_.Exception.Message;return [pscustomobject]@{key=$Key;exists=$false;valueNames=@();subkeys=@()}}
}

function Write-AtomicJson([object]$Record,[string]$FinalPath) {
    $temporaryPath=$FinalPath+'.partial.'+[guid]::NewGuid().ToString('N')
    try {
        $Record | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8 -ErrorAction Stop
        Get-Content -Raw -LiteralPath $temporaryPath -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop | Out-Null
        if(Test-Path -LiteralPath $FinalPath) {[IO.File]::Replace($temporaryPath,$FinalPath,$null)}
        else {[IO.File]::Move($temporaryPath,$FinalPath)}
    }
    finally {
        if(Test-Path -LiteralPath $temporaryPath){Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue}
    }
}

$started=[Diagnostics.Stopwatch]::StartNew()
$state=[pscustomobject]@{
    files=New-Object 'System.Collections.Generic.List[object]'
    skippedPaths=New-Object 'System.Collections.Generic.List[object]'
    filesProcessed=0;bytesProcessed=[int64]0;hashesCalculated=0
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Write-Host "Starting $Phase inventory capture..." -ForegroundColor Cyan

$uniqueUpdaterRoots=Get-UniqueCanonicalRoots $UpdaterRoots
$uniqueApplicationDataRoots=Get-UniqueCanonicalRoots $ApplicationDataRoots
$uniqueGameDirectories=Get-UniqueCanonicalRoots $GameDirectories
$captureRoots=New-Object 'System.Collections.Generic.List[object]'
foreach($root in $uniqueUpdaterRoots) {
    $captureRoots.Add([pscustomobject]@{category='updater-install';root=$root}) | Out-Null
    Get-ScopedFiles $root 'updater-install' $state
}
foreach($root in $uniqueApplicationDataRoots) {
    $captureRoots.Add([pscustomobject]@{category='application-data';root=$root}) | Out-Null
    Get-ScopedFiles $root 'application-data' $state
}
foreach($game in $uniqueGameDirectories) {
    $win64=Join-Path $game 'Aion2\Binaries\Win64'
    if(Test-Path -LiteralPath $win64 -PathType Container){
        $canonicalWin64=Get-CanonicalRoot $win64
        $captureRoots.Add([pscustomobject]@{category='game-win64';root=$canonicalWin64}) | Out-Null
        Get-ScopedFiles $canonicalWin64 'game-win64' $state
    }
    else {Add-SkippedPath $state $win64 'Expected AION2 Win64 directory was not found'}
}

$updaters=@($uniqueUpdaterRoots | ForEach-Object {
    $exe=Join-Path $_ 'Aion2.Updater.exe'
    if(Test-Path -LiteralPath $exe){[pscustomobject]@{path=(Resolve-Path -LiteralPath $exe).Path;sha256=(Get-Sha256 $exe)}}
})
$captureRootRecords=@($captureRoots | ForEach-Object {$_})
$fileRecords=@($state.files | ForEach-Object {$_})
$skippedPathRecords=@($state.skippedPaths | ForEach-Object {$_})
$record=[ordered]@{
    schemaVersion=3;phase=$Phase;capturedUtc=[DateTime]::UtcNow.ToString('o')
    captureRoots=$captureRootRecords;updaterExecutables=$updaters;files=$fileRecords;registry=@($RegistryKeys|ForEach-Object{Get-RegistryInventory $_ $state})
    summary=[ordered]@{filesInventoried=$state.filesProcessed;bytesProcessed=$state.bytesProcessed;hashesCalculated=$state.hashesCalculated;elapsedSeconds=[math]::Round($started.Elapsed.TotalSeconds,2);skippedPaths=$skippedPathRecords}
    notes='Read-only capture. Game scope is Aion2\\Binaries\\Win64; updater and application-data roots are explicitly selected. Changes are evidence, not ownership.'
}
$path=Join-Path $OutputDirectory ('inventory-'+$Phase.ToLowerInvariant()+'.json')
Write-AtomicJson $record $path
Write-Host "Capture completed successfully. Output: $path" -ForegroundColor Green
Write-Host ("Files: {0}; hashes: {1}; bytes: {2:N0}; elapsed: {3:N2}s; skipped: {4}" -f $state.filesProcessed,$state.hashesCalculated,$state.bytesProcessed,$started.Elapsed.TotalSeconds,$state.skippedPaths.Count)
