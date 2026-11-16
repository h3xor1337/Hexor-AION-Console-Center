#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version = '1.0.7',
    [string]$ReleaseDirectory,
    [string]$BaselineDirectory,
    [string]$ResultPath,
    [switch]$FixtureOnly,
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$BaselineVersion = '1.0.6'
)
$ErrorActionPreference = 'Stop'
if ([version]$BaselineVersion -ge [version]$Version) { throw 'BaselineVersion must be older than the target Version.' }
$root = Split-Path -Parent $PSScriptRoot
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $root "output\releases\$Version" }
if (-not $BaselineDirectory) { $BaselineDirectory = Join-Path $root "output\releases\$BaselineVersion\payload" }
if (-not $ResultPath) {
    $suffix = if ($FixtureOnly) { '-fixtures' } else { '' }
    $ResultPath = Join-Path $root "output\ota-compatibility-$Version$suffix.json"
}
$ownedFiles = @('Hexor-AION-Control-Center.exe', 'Hexor.Updater.exe')
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$runId = [guid]::NewGuid().ToString('N')
$sandbox = [IO.Path]::GetFullPath((Join-Path $temporaryRoot "HexorOtaCompatibility-$runId"))
if (-not $sandbox.StartsWith([IO.Path]::TrimEndingDirectorySeparator($temporaryRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $sandbox) -cne "HexorOtaCompatibility-$runId" -or (Test-Path -LiteralPath $sandbox)) { throw 'Fresh temporary sandbox guard failed.' }
if (-not $FixtureOnly -and -not (Test-Path -LiteralPath (Join-Path $ReleaseDirectory 'latest.json'))) { throw 'Prepare and sign the release before running compatibility checks.' }
New-Item -ItemType Directory -Path $sandbox | Out-Null
$script:checks = [Collections.Generic.List[string]]::new()
$script:processes = [Collections.Generic.List[object]]::new()
$failure = $null
$fixtureKey = $null

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks.Add($Message)
    Write-Output "PASS: $Message"
}
function Assert-TestPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($sandbox + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Mutable fixture path escaped the temporary sandbox.' }
    return $full
}
function New-TestDirectory([string]$Name) {
    $path = Assert-TestPath (Join-Path $sandbox $Name)
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}
function File-Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Invoke-Helper([string]$Helper, [string[]]$Arguments, [string]$Label, [int]$ExpectedExit = 0) {
    $null = Assert-TestPath $Helper
    $start = [Diagnostics.ProcessStartInfo]::new($Helper)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment['HEXOR_UPDATER_NO_DIALOG'] = '1'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(60000)) { throw "$Label did not exit within its fixture deadline; retained at $sandbox" }
        $code = $process.ExitCode
        $script:processes.Add([pscustomobject]@{ Label = $Label; ExitCode = $code })
        Check ($code -eq $ExpectedExit) "$Label exits $ExpectedExit"
    } finally { $process.Dispose() }
}
function Publish-TestProject([string]$Project, [string]$Output, [string]$Label) {
    $null = Assert-TestPath $Project
    $null = Assert-TestPath $Output
    $log = Join-Path $sandbox "$Label-build.log"
    & dotnet publish $Project -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false -o $Output *> $log
    $code = $LASTEXITCODE
    $script:processes.Add([pscustomobject]@{ Label = "$Label build"; ExitCode = $code })
    Check ($code -eq 0) "$Label fixture builds successfully"
}
function Wait-Marker([string]$Install, [string]$Expected) {
    $marker = Join-Path $Install 'restart-marker.txt'
    $until = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $until) {
        if ((Test-Path -LiteralPath $marker) -and [IO.File]::ReadAllText($marker).Trim() -eq $Expected) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}
function Verify-Release([string]$Directory, [string]$ExpectedVersion, $Module, $Trust) {
    $envelopePath = Join-Path $Directory 'latest.json'
    $manifest = & $Module { param($Bytes, $Key) Read-HexorSignedManifest -Bytes $Bytes -PublicKeyPem $Key } ([IO.File]::ReadAllBytes($envelopePath)) $Trust.PublicKeyPem
    if ($manifest.Version -cne $ExpectedVersion -or [version]$manifest.MinimumUpdaterVersion -gt [version]'1.0.0') { throw 'Release version/helper compatibility does not match the expected baseline or target.' }
    $zipPath = Join-Path $Directory "Hexor-$ExpectedVersion-win-x64.zip"
    if ((File-Hash $zipPath) -ine $manifest.Sha256) { throw 'Release ZIP does not match its production signed digest.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($zip.Entries.Count -ne 2 -or @($zip.Entries | Where-Object FullName -CNotIn $ownedFiles).Count -ne 0 -or
            @($zip.Entries.FullName | Sort-Object -Unique).Count -ne 2) { throw 'Release ZIP must contain exactly the two owned executable names.' }
        $hashes = @{}
        foreach ($entry in $zip.Entries) {
            $stream = $entry.Open()
            try { $hashes[$entry.FullName] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        }
        return [pscustomobject]@{ Manifest = $manifest; ManifestPath = $envelopePath; Package = $zipPath; Hashes = $hashes }
    } finally { $zip.Dispose() }
}

try {
    if (-not $FixtureOnly) {
        $module = Import-Module (Join-Path $PSScriptRoot 'HexorGitHubRelease.psm1') -Force -PassThru
        $trust = Get-Content -Raw -LiteralPath (Join-Path $root 'Updates\trust.json') | ConvertFrom-Json
        $baseline = Verify-Release (Split-Path -Parent $BaselineDirectory) $BaselineVersion $module $trust
        $target = Verify-Release $ReleaseDirectory $Version $module $trust
        Check ($true) 'Production baseline and target signatures, package hashes, helper compatibility and two-file allowlists verify'
        foreach ($name in $ownedFiles) {
            Check ((File-Hash (Join-Path $BaselineDirectory $name)) -ceq $baseline.Hashes[$name]) "Production baseline $name matches its signed v$BaselineVersion ZIP"
        }
        $install = New-TestDirectory 'production-installed'
        $runner = New-TestDirectory 'production-runner'
        $inputs = New-TestDirectory 'production-inputs'
        foreach ($name in $ownedFiles) { Copy-Item -LiteralPath (Join-Path $BaselineDirectory $name) -Destination $install }
        $helper = Join-Path $runner 'Hexor.Updater.exe'
        Copy-Item -LiteralPath (Join-Path $BaselineDirectory 'Hexor.Updater.exe') -Destination $helper
        # The unchanged helper stages beside its input ZIP, so copy both signed inputs into the sandbox.
        $copiedManifest = Join-Path $inputs 'latest.json'
        $copiedPackage = Join-Path $inputs (Split-Path -Leaf $target.Package)
        Copy-Item -LiteralPath $target.ManifestPath -Destination $copiedManifest
        Copy-Item -LiteralPath $target.Package -Destination $copiedPackage
        [IO.File]::WriteAllText((Join-Path $install 'customer.json'), '{"synthetic":"preserve"}')
        [IO.File]::WriteAllText((Join-Path $install 'Aion2.Updater.exe'), 'synthetic unrelated file; never executed')
        Invoke-Helper $helper @($install, $copiedManifest, $copiedPackage, '-1', 'exit') "Actual v$BaselineVersion helper installs signed target"
        foreach ($name in $ownedFiles) {
            Check ((File-Hash (Join-Path $install $name)) -ceq $target.Hashes[$name]) "Actual helper installs target $name bytes"
            Check ((File-Hash (Join-Path $install ".hexor-update\$name")) -ceq $baseline.Hashes[$name]) "Actual helper retains previous $name backup"
        }
        Check (([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install $ownedFiles[0]))).ProductVersion -eq $Version) 'Installed production Control Center version matches signed target'
        Check ([IO.File]::ReadAllText((Join-Path $install 'customer.json')) -ceq '{"synthetic":"preserve"}' -and
            [IO.File]::ReadAllText((Join-Path $install 'Aion2.Updater.exe')) -ceq 'synthetic unrelated file; never executed') 'Actual production update preserves synthetic customer and unrelated vendor files'
        $unexpected = @(Get-Process -Name 'Hexor-AION-Control-Center' -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -ieq (Join-Path $install $ownedFiles[0]) } catch { $false } })
        Check ($unexpected.Count -eq 0) 'Exit-mode compatibility check does not launch the real Control Center'
    }

    # Restart tests use harmless console programs, not Hexor startup or any customer registry.
    $baselineSource = New-TestDirectory 'baseline-source'
    $targetSource = New-TestDirectory 'target-source'
    $helperSource = New-TestDirectory 'helper-source'
    $fixtureKey = [Security.Cryptography.RSA]::Create(3072)
    @{ ManifestUrl = 'https://raw.githubusercontent.com/hexor-test-fixture/hexor-updates/main/latest.json'; PublicKeyPem = $fixtureKey.ExportSubjectPublicKeyInfoPem() } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $helperSource 'trust.json') -Encoding utf8
    foreach ($specification in @(@($baselineSource, $baselineVersion), @($targetSource, $Version))) {
        $source = $specification[0]
        $stubVersion = $specification[1]
        @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>Hexor-AION-Control-Center</AssemblyName><Version>$stubVersion</Version><PublishSingleFile>true</PublishSingleFile><IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract></PropertyGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $source 'Stub.csproj')
        'File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "restart-marker.txt"), System.Reflection.Assembly.GetExecutingAssembly().GetName().Version + Environment.NewLine);' | Set-Content -LiteralPath (Join-Path $source 'Program.cs')
    }
    $core = [Security.SecurityElement]::Escape((Join-Path $root 'Updates\UpdateCore.cs'))
    $dialog = [Security.SecurityElement]::Escape((Join-Path $root 'Updates\UpdateDialog.cs'))
    $program = [Security.SecurityElement]::Escape((Join-Path $root 'Updater\Program.cs'))
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>Hexor.Updater</AssemblyName><Version>1.0.0</Version><PublishSingleFile>true</PublishSingleFile><IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract></PropertyGroup><ItemGroup><Compile Include="$core"/><Compile Include="$dialog"/><Compile Include="$program"/><EmbeddedResource Include="trust.json" LogicalName="Hexor.UpdateTrust"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $helperSource 'Helper.csproj')
    $baselineOutput = Join-Path $sandbox 'baseline-payload'
    $targetOutput = Join-Path $sandbox 'target-payload'
    $helperOutput = Join-Path $sandbox 'fixture-runner'
    Publish-TestProject (Join-Path $baselineSource 'Stub.csproj') $baselineOutput 'baseline'
    Publish-TestProject (Join-Path $targetSource 'Stub.csproj') $targetOutput 'target'
    Publish-TestProject (Join-Path $helperSource 'Helper.csproj') $helperOutput 'helper'
    $fixtureHelper = Join-Path $helperOutput 'Hexor.Updater.exe'
    Copy-Item -LiteralPath $fixtureHelper -Destination $targetOutput
    $fixturePackage = Join-Path $sandbox 'fixture-package.zip'
    Compress-Archive -LiteralPath ($ownedFiles | ForEach-Object { Join-Path $targetOutput $_ }) -DestinationPath $fixturePackage
    $manifest = [ordered]@{ SchemaVersion = 1; Version = $Version; ReleaseDate = [DateTimeOffset]::UtcNow.ToString('O'); DownloadUrl = "https://github.com/hexor-test-fixture/hexor-updates/releases/download/v$Version/Hexor-$Version-win-x64.zip"; Sha256 = File-Hash $fixturePackage; ReleaseNotes = 'Isolated harmless restart fixture'; MinimumUpdaterVersion = '1.0.0'; Mandatory = $false }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $fixtureManifest = Join-Path $sandbox 'fixture-manifest.json'
    @{ Payload = [Convert]::ToBase64String($bytes); Signature = [Convert]::ToBase64String($fixtureKey.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) } | ConvertTo-Json | Set-Content -LiteralPath $fixtureManifest -Encoding utf8
    $fixtureInstall = New-TestDirectory 'fixture-installed'
    Copy-Item -LiteralPath (Join-Path $baselineOutput $ownedFiles[0]), $fixtureHelper -Destination $fixtureInstall
    Invoke-Helper $fixtureHelper @($fixtureInstall, $fixtureManifest, $fixturePackage, '-1', 'restart') 'Fixture signed update with restart'
    Check (Wait-Marker $fixtureInstall "$Version.0") 'Updater restart flag launches the newly installed harmless app'
    foreach ($name in $ownedFiles) { Check ((File-Hash (Join-Path $fixtureInstall $name)) -ceq (File-Hash (Join-Path $targetOutput $name))) "Restarted fixture retains target $name bytes" }

    $recoveryInstall = New-TestDirectory 'fixture-recovery-installed'
    Copy-Item -LiteralPath (Join-Path $baselineOutput $ownedFiles[0]), $fixtureHelper -Destination $recoveryInstall
    $locked = [IO.FileStream]::new((Join-Path $recoveryInstall 'Hexor.Updater.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Invoke-Helper $fixtureHelper @($recoveryInstall, $fixtureManifest, $fixturePackage, '-1', 'exit') 'Fixture locked helper rejects update' 1 } finally { $locked.Dispose() }
    Check (Test-Path -LiteralPath (Join-Path $recoveryInstall '.hexor-update\pending')) 'Failed replacement retains the recovery journal'
    Invoke-Helper $fixtureHelper @('--recover', $recoveryInstall, '-1') 'Fixture recovery helper'
    Check (Wait-Marker $recoveryInstall "$baselineVersion.0") 'Recovery restarts the previous harmless app'
    Check (-not (Test-Path -LiteralPath (Join-Path $recoveryInstall '.hexor-update\pending'))) 'Successful recovery clears its pending journal'
    Check ((File-Hash (Join-Path $recoveryInstall $ownedFiles[0])) -ceq (File-Hash (Join-Path $baselineOutput $ownedFiles[0]))) 'Recovery restores the previous application bytes'
    Check ((File-Hash (Join-Path $recoveryInstall $ownedFiles[1])) -ceq (File-Hash $fixtureHelper)) 'Recovery restores the previous helper bytes'
} catch {
    $failure = $_.Exception.Message
} finally {
    if ($fixtureKey) { $fixtureKey.Dispose() }
    $result = [ordered]@{ Passed = (-not $failure); Version = $Version; FixtureOnly = [bool]$FixtureOnly; ChecksPassed = $script:checks.Count; ProcessResults = $script:processes.ToArray(); Checks = $script:checks.ToArray(); Failure = $failure; Sandbox = $sandbox; RealControlCenterLaunched = $false; VendorLaunched = $false; RealLegionRegistryModified = $false }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding utf8
    Write-Output "Results: $ResultPath"
    Write-Output "Fixtures retained: $sandbox"
}
if ($failure) { throw "OTA compatibility verification failed: $failure" }
Write-Output "$($script:checks.Count) OTA compatibility checks passed. No real Control Center/vendor application or customer registry was used."
