# Versioned, owner-approved Console-only qualification. No historical baseline writes.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ScopeDigest = '878676D04805EF9905D2205B0F7A50E6A4BFD0855DDE134D6DA812C0CB0D153B'
$script:OwnedNames = @('Hexor-AION-Control-Center.exe', 'Hexor.Updater.exe')
$script:UnsignedV1014Digest = 'EE37CCE13D5D310311825479AD1EC0A61969F5C212256C572D6D3289B54F7007'

function Read-HexorConsoleScope([string]$Root, [string]$PolicyPath) {
    if ((Get-FileHash -LiteralPath $PolicyPath -Algorithm SHA256).Hash -cne $script:ScopeDigest) { throw 'Qualification scope differs from the exact owner-approved 77/856 record.' }
    $scope = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -Depth 30
    $baselinePath = Join-Path $Root 'output/validation-1.0.12/publish-resume-20261029-01/protected-before.json'
    if ((Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash -cne $scope.BaselineSHA256) { throw 'Historical baseline changed.' }
    $baseline = @(Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json)
    $map = @{}
    foreach ($entry in $baseline) {
        if ($map.ContainsKey($entry.Path)) { throw 'Duplicate historical path.' }
        $map[$entry.Path] = $entry.Sha256
    }
    $seen = @{}
    foreach ($entry in $scope.Entries) {
        if ($seen.ContainsKey($entry.Path) -or -not $map.ContainsKey($entry.Path) -or $map[$entry.Path] -cne $entry.ApprovedHistoricalSHA256) { throw 'Scope does not preserve the historical path/hash inventory.' }
        $seen[$entry.Path] = $true
    }
    if ($seen.Count -ne 933 -or $baseline.Count -ne 933 -or
        @($scope.Entries | Where-Object ProposedQualification -CEQ 'RequiredExactHistoricalArtifactHash').Count -ne 73 -or
        @($scope.Entries | Where-Object ProposedQualification -CEQ 'RequiredVersionAwareConsoleArtifact').Count -ne 4 -or
        @($scope.Entries | Where-Object ProposedQualification -CEQ 'RequiredInstalledEnvironmentReport').Count -ne 856) { throw 'Approved qualification partition is not exactly 73+4 blocking / 856 audit-only.' }
    return $scope
}

function Get-HexorFileHash([string]$Path) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked or non-file release artifact rejected.' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Test-HexorInstalledTransition($Entries, $BaselinePackage, $TargetPackage) {
    # All four paths remain mandatory. Only signed, explicitly authorized transitions
    # may replace historical hashes; package functions supply these hash maps.
    $historical = $true; $updated = $null -ne $BaselinePackage -and $null -ne $TargetPackage; $recovered = $updated
    foreach ($entry in $Entries) {
        $actual = Get-HexorFileHash $entry.Path
        $historical = $historical -and $actual -ceq $entry.ApprovedHistoricalSHA256
        if ($updated -or $recovered) {
            $name = [IO.Path]::GetFileName($entry.Path)
            $backup = [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($entry.Path)) -ceq '.hexor-update'
            $updatedExpected = if ($backup) { $BaselinePackage.Hashes[$name] } else { $TargetPackage.Hashes[$name] }
            $updated = $updated -and $actual -ceq $updatedExpected
            $recovered = $recovered -and $actual -ceq $BaselinePackage.Hashes[$name]
        }
    }
    if ($historical) { return 'HistoricalBeforeUpdate' }
    if ($updated) { return 'AuthorizedUpdatedWithPreviousVersionBackups' }
    if ($recovered) { return 'RecoveredPreviousVersionWithTransactionBackups' }
    throw 'Mandatory installed Console/backup hashes fail the historical or signed version-aware transition contract.'
}

function Test-HexorBlockingScope($Scope, $BaselinePackage = $null, $TargetPackage = $null) {
    $fixed = @($Scope.Entries | Where-Object ProposedQualification -CEQ 'RequiredExactHistoricalArtifactHash')
    foreach ($entry in $fixed) {
        if ((Get-HexorFileHash $entry.Path) -cne $entry.ApprovedHistoricalSHA256) { throw "Mandatory artifact or pinned-trust hash mismatch: $($entry.Path)" }
    }
    $installed = @($Scope.Entries | Where-Object ProposedQualification -CEQ 'RequiredVersionAwareConsoleArtifact')
    if ($fixed.Count -ne 73 -or $installed.Count -ne 4) { throw 'All 77 blocking checks are required.' }
    $state = Test-HexorInstalledTransition $installed $BaselinePackage $TargetPackage
    return [pscustomobject]@{ Passed = $true; BlockingChecks = 77; InstalledState = $state }
}

function Get-HexorEnvironmentAudit($Scope) {
    $entries = @($Scope.Entries | Where-Object ProposedQualification -CEQ 'RequiredInstalledEnvironmentReport')
    if ($entries.Count -ne 856) { throw 'All 856 environment audit entries are required.' }
    $records = foreach ($entry in $entries) {
        $actual = $null; $status = 'Unavailable'
        try { $actual = Get-HexorFileHash $entry.Path; $status = if ($actual -ceq $entry.ApprovedHistoricalSHA256) { 'HistoricalMatch' } else { 'UnresolvedHashDifference' } }
        catch { $status = 'UnresolvedMissingOrUnreadable' }
        [pscustomobject]@{ Path = $entry.Path; HistoricalSHA256 = $entry.ApprovedHistoricalSHA256; ObservedSHA256 = $actual; Finding = $status; ChangedHashAccepted = $false }
    }
    return ,@($records)
}

function Read-HexorQualifiedPackage([string]$Root, [string]$Directory, [string]$Version) {
    $publisher = Import-Module (Join-Path $Root 'scripts/HexorGitHubRelease.psm1') -Force -PassThru
    $trust = Get-Content (Join-Path $Root 'Updates/trust.json') -Raw | ConvertFrom-Json
    $manifest = & $publisher { param($bytes,$key) Read-HexorSignedManifest -Bytes $bytes -PublicKeyPem $key } ([IO.File]::ReadAllBytes((Join-Path $Directory 'latest.json'))) $trust.PublicKeyPem
    $expectedUrl = "https://github.com/h3xor1337/hexor-updates/releases/download/v$Version/Hexor-$Version-win-x64.zip"
    if ($manifest.Version -cne $Version -or $manifest.DownloadUrl -cne $expectedUrl -or [version]$manifest.MinimumUpdaterVersion -gt [version]'1.0.0') { throw 'Signed package version, helper requirement or exact approved download URL mismatch.' }
    $package = Join-Path $Directory "Hexor-$Version-win-x64.zip"
    if ((Get-HexorFileHash $package) -ine $manifest.Sha256) { throw 'Signed ZIP SHA256 mismatch.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    $hashes = @{}; $scratch = Join-Path ([IO.Path]::GetTempPath()) ('HexorPackageInspection-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    try {
        if ($zip.Entries.Count -ne 2) { throw 'Package must contain exactly two owned executables.' }
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -cnotin $script:OwnedNames -or $hashes.ContainsKey($entry.FullName) -or $entry.Length -lt 2 -or $entry.Length -gt 600000000 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe or duplicate package entry.' }
            $destination = Join-Path $scratch $entry.FullName
            $input = $entry.Open(); $output = [IO.File]::Create($destination)
            try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
            $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination)
            if ([version]::new($metadata.FileMajorPart,$metadata.FileMinorPart,$metadata.FileBuildPart) -ne [version]$Version) { throw 'Executable version differs from signed package version.' }
            $hashes[$entry.FullName] = Get-HexorFileHash $destination
        }
    } finally {
        $zip.Dispose()
        # Only the two explicitly named files in our newly created directory.
        foreach ($name in $script:OwnedNames) { $file = Join-Path $scratch $name; if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
        Remove-Item -LiteralPath $scratch
    }
    return [pscustomobject]@{ Version = $Version; Hashes = $hashes; Package = $package; Manifest = $manifest }
}

function Get-HexorQualificationInputs([string]$Root) {
    $extensions = @('.cs','.csproj','.xaml','.json','.manifest','.ps1','.psm1','.ico','.png')
    $files = [Collections.Generic.List[string]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Root -File) { if ($item.Extension -in $extensions) { $files.Add($item.FullName) } }
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($name in @('Compatibility','Runtime','Updates','Updater','ReleaseTools','scripts','tests','Assets')) {
        $directory = Join-Path $Root $name; if (Test-Path -LiteralPath $directory -PathType Container) { $pending.Push($directory) }
    }
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop()) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked qualification input rejected.' }
            if ($item.PSIsContainer) { if ($item.Name -notin @('bin','obj','.build','.obj-build')) { $pending.Push($item.FullName) } }
            elseif ($item.Extension -in $extensions) { $files.Add($item.FullName) }
        }
    }
    return $files.ToArray()
}

function Assert-HexorSourceReceipt([object[]]$SourceHashes,[string[]]$RequiredPaths) {
    if ($null -eq $SourceHashes -or $SourceHashes.Count -eq 0) { throw 'Qualification source inventory is empty.' }
    if ($null -eq $RequiredPaths -or $RequiredPaths.Count -eq 0) { throw 'Required qualification source inventory is empty.' }
    $sourcePaths = @{}
    foreach ($record in $SourceHashes) {
        if ($null -eq $record -or 'Path' -notin $record.PSObject.Properties.Name -or 'SHA256' -notin $record.PSObject.Properties.Name -or
            $record.Path -isnot [string] -or [string]::IsNullOrWhiteSpace($record.Path) -or
            $record.SHA256 -isnot [string] -or $record.SHA256 -cnotmatch '^[A-Fa-f0-9]{64}$') { throw 'Malformed qualification source record: Path and SHA256 are required.' }
        try { $path = [IO.Path]::GetFullPath($record.Path) } catch { throw "Malformed qualification source path: $($record.Path)" }
        if ($sourcePaths.ContainsKey($path)) { throw "Duplicate source proof rejected: $path" }
        $sourcePaths[$path] = $record.SHA256
    }
    $required = @{}; $missing = [Collections.Generic.List[string]]::new()
    foreach ($entry in $RequiredPaths) {
        if ([string]::IsNullOrWhiteSpace($entry)) { throw 'Malformed required qualification source path.' }
        try { $path = [IO.Path]::GetFullPath($entry) } catch { throw "Malformed required qualification source path: $entry" }
        if ($required.ContainsKey($path)) { throw "Duplicate required qualification source path: $path" }
        $required[$path] = $true
        if (-not $sourcePaths.ContainsKey($path)) { $missing.Add($path) }
    }
    if ($missing.Count -gt 0) { throw ('Qualification receipt omits required source paths: ' + ($missing -join '; ')) }
    # Extra receipt entries remain allowed, as before, but every entry must have
    # valid provenance. No extra/duplicate entry substitutes for a required path.
    foreach ($path in $sourcePaths.Keys) {
        if ((Get-HexorFileHash $path) -cne $sourcePaths[$path]) { throw "Qualified source bytes changed after tests: $path" }
    }
}

function Assert-HexorReleaseReceipt([string]$Root, [string]$Directory) {
    $receiptPath = Join-Path $Directory 'qualification.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 30
    if ($receipt.Version -cne '1.0.14' -or -not $receipt.Passed) { throw 'Final qualification receipt must pass for exactly v1.0.14.' }
    $required = @('qualification-gate-tests','full-regression-sequential','full-regression-concurrent','diagnostics-stress','logging','registry','ota-security','ota-upgrade-rollback-recovery','signing-regression','publishing-contracts','windows-signing')
    foreach ($name in $required) {
        $gate = @($receipt.Gates | Where-Object Name -CEQ $name)
        if ($gate.Count -ne 1 -or -not $gate[0].Passed -or (Get-HexorFileHash $gate[0].EvidencePath) -cne $gate[0].EvidenceSHA256) { throw "Required release proof absent, failed or changed: $name" }
    }
    if (@($receipt.SourceHashes).Count -eq 0 -or @($receipt.ArtifactHashes).Count -ne 6) { throw 'Source and six final release/payload hash records are required.' }
    $requiredPaths = @(Get-HexorQualificationInputs $Root)
    Assert-HexorSourceReceipt @($receipt.SourceHashes) $requiredPaths
    foreach ($record in @($receipt.ArtifactHashes)) {
        if ((Get-HexorFileHash $record.Path) -cne $record.SHA256) { throw "Qualified candidate bytes changed after tests: $($record.Path)" }
    }
    $expectedFiles = @('latest.json','SHA256SUMS.txt','RELEASE-NOTES.md','Hexor-1.0.14-win-x64.zip','payload/Hexor-AION-Control-Center.exe','payload/Hexor.Updater.exe')
    foreach ($name in $expectedFiles) {
        $expectedPath = [IO.Path]::GetFullPath((Join-Path $Directory $name))
        if (@($receipt.ArtifactHashes | Where-Object { [IO.Path]::GetFullPath($_.Path) -ieq $expectedPath }).Count -ne 1) { throw 'Receipt does not cover exact final release assets and payload.' }
    }
    if ($receipt.WindowsSigning.Version -cne '1.0.14') { throw 'Windows signing authorization is version-specific.' }
    if ($receipt.WindowsSigning.Mode -ceq 'OwnerApprovedUnsignedException') {
        $null = Test-HexorUnsignedV1014 $receipt.WindowsSigning.ExceptionPath (Join-Path $Directory 'payload')
    } elseif ($receipt.WindowsSigning.Mode -ceq 'Authenticode') {
        & (Join-Path $Root 'scripts/Test-HexorAuthenticode.ps1') -Path (Join-Path $Directory 'payload/Hexor-AION-Control-Center.exe'),(Join-Path $Directory 'payload/Hexor.Updater.exe') -ApprovedSignerThumbprint $receipt.WindowsSigning.ApprovedSignerThumbprint -ApprovedPublisherSubject $receipt.WindowsSigning.ApprovedPublisherSubject -SignToolPath $receipt.WindowsSigning.SignToolPath
    } else { throw 'Explicit v1.0.14 Windows signing disposition is required.' }
}

function Test-HexorUnsignedV1014([string]$ExceptionPath,[string]$PayloadDirectory) {
    if ((Get-HexorFileHash $ExceptionPath) -cne $script:UnsignedV1014Digest) { throw 'Unsigned exception differs from the exact v1.0.14 owner authorization.' }
    $exception = Get-Content -LiteralPath $ExceptionPath -Raw | ConvertFrom-Json
    if ($exception.Version -cne '1.0.14' -or $exception.Status -cne 'OwnerApprovedUnsignedDistributionException' -or
        @($exception.Files).Count -ne 2 -or (Compare-Object $script:OwnedNames @($exception.Files))) { throw 'Unsigned exception scope/version mismatch.' }
    $records = foreach ($name in $script:OwnedNames) {
        $path = Join-Path $PayloadDirectory $name
        $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        if ([version]::new($metadata.FileMajorPart,$metadata.FileMinorPart,$metadata.FileBuildPart) -ne [version]'1.0.14') { throw 'Unsigned exception cannot apply to another executable version.' }
        $hash = Get-HexorFileHash $path
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne 'NotSigned') { throw 'Unexpected executable signature state; exception only covers unsigned owned executables.' }
        [pscustomobject]@{ Path=$path; SHA256=$hash; Status='NotSigned'; AuthenticodeSigningCheck='Not passed'; Disposition='Owner-approved v1.0.14-only unsigned distribution exception' }
    }
    return ,@($records)
}

function Write-HexorGateEvidence([string]$Path, $Value) {
    $stream = [IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 30)); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
}

Export-ModuleMember -Function Read-HexorConsoleScope,Test-HexorBlockingScope,Get-HexorEnvironmentAudit,Read-HexorQualifiedPackage,Assert-HexorReleaseReceipt,Test-HexorUnsignedV1014,Write-HexorGateEvidence
