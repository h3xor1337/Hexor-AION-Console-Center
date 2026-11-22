#requires -Version 7.0
[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
 [Parameter(Mandatory)][string]$PrivateKeyPath,
 [string]$GitHubRepository,
 [string]$DownloadUrl,
 [Parameter(Mandatory)][string]$ReleaseNotesPath,
 [string]$OutputDirectory,
 [switch]$Mandatory,
 [switch]$PublishGitHub
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if($Version -ceq '1.0.14'){
 $gateEvidence=Join-Path $root ('.build/preparation-gate-'+[guid]::NewGuid().ToString('N'))
 & (Join-Path $PSScriptRoot 'Test-HexorConsoleRelease.ps1') -Phase Preparation -ResultDirectory $gateEvidence
}
$keyPath=(Resolve-Path -LiteralPath $PrivateKeyPath).Path
if($keyPath.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Private key must be outside the repository'}
$aioRoot=[IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $root) 'Aion AIO'))
if($keyPath.StartsWith($aioRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Private key must be outside the AIO Installer repository'}
$trust=Get-Content -Raw -LiteralPath (Join-Path $root 'Updates\trust.json') | ConvertFrom-Json
if(-not $trust.PublicKeyPem -or ([uri]$trust.ManifestUrl).Scheme -ne 'https'){throw 'Configure embedded Updates/trust.json before publishing'}
Import-Module (Join-Path $PSScriptRoot 'HexorGitHubRelease.psm1') -Force
if(-not $GitHubRepository){
 if($trust.ManifestUrl -cmatch '^https://raw\.githubusercontent\.com/([^/]+/[^/]+)/main/latest\.json$'){$GitHubRepository=$Matches[1]}
 else{throw 'Supply the approved GitHubRepository and configure its raw GitHub manifest URL in embedded trust first.'}
}
$github=Get-HexorGitHubReleaseConfiguration -GitHubRepository $GitHubRepository -Version $Version -Trust $trust
if($DownloadUrl -and $DownloadUrl -cne $github.DownloadUrl){throw "DownloadUrl must match the versioned GitHub asset: $($github.DownloadUrl)"}
$DownloadUrl=$github.DownloadUrl
$rsa=[Security.Cryptography.RSA]::Create()
try{
 $rsa.ImportFromPem([IO.File]::ReadAllText($keyPath))
 if($rsa.KeySize -lt 3072 -or $rsa.ExportSubjectPublicKeyInfoPem().Trim() -ne $trust.PublicKeyPem.Trim()){throw 'Signing key does not match embedded trusted public key (RSA 3072+ required)'}
 $notes=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleaseNotesPath).Path)
 if($notes.Length -gt 24000){throw 'Release notes exceed manifest limit'}
 $out=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root "output\releases\$Version"}
 if(Test-Path -LiteralPath $out){throw 'Release output already exists; choose a new version or explicitly archive previous output'}
 New-Item -ItemType Directory -Path $out | Out-Null
 & dotnet build (Join-Path $root 'tests\Hexor.Tests.csproj') -c Release -r win-x64
 if($LASTEXITCODE -ne 0){throw 'Test executable build failed'}
 & (Join-Path $root 'tests\bin\Release\net10.0-windows\win-x64\Hexor.Tests.exe')
 if($LASTEXITCODE -ne 0){throw 'Existing tests failed'}
 & dotnet run --project (Join-Path $root 'UpdateTests\UpdateTests.csproj') -c Release
 if($LASTEXITCODE -ne 0){throw 'Updater tests failed'}
 & (Join-Path $PSScriptRoot 'Test-HexorGitHubPublishing.ps1')
 $payload=Join-Path $out 'payload'
 & dotnet publish (Join-Path $root 'Hexor.ControlCenter.csproj') -t:Rebuild -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false -o $payload
 if($LASTEXITCODE -ne 0){throw 'Control Center publish failed'}
 & dotnet publish (Join-Path $root 'Updater\Hexor.Updater.csproj') -t:Rebuild -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false -o $payload
 if($LASTEXITCODE -ne 0){throw 'Helper publish failed'}
 # The updater rarely changes while the console changes every release. Its sources are
 # fingerprinted against a durable stamp: when the fingerprint is unchanged and the freshly
 # built helper is byte-identical to the one customers already run, the package ships without
 # it and the OTA download stays at roughly half the size. Losing the stamp only means one
 # heavier release, never a wrong one: a missing helper is harmless, a changed one ships.
 $updaterSources=@('Updater\Program.cs','Updater\Hexor.Updater.csproj','Updates\UpdateCore.cs','Updates\UpdateDialog.cs','Updates\trust.json') | ForEach-Object {Join-Path $root $_}
 $updaterFingerprint=-join (($updaterSources | Sort-Object) | ForEach-Object {[IO.Path]::GetFileName($_);(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash})
 $stampPath=Join-Path $root '.build\updater-stamp.json'
 $stamp=$null;if(Test-Path -LiteralPath $stampPath){try{$stamp=Get-Content -Raw -LiteralPath $stampPath | ConvertFrom-Json}catch{}}
 $updaterChanged=(-not $stamp)-or($stamp.SourceHash -cne $updaterFingerprint)
 $updaterVersion=$Version
 $updaterExe=Join-Path $payload 'Hexor.Updater.exe'
 $updaterHash=(Get-FileHash -LiteralPath $updaterExe -Algorithm SHA256).Hash.ToLowerInvariant()
 $includeUpdater=$updaterChanged-or($stamp.ExeSha256 -cne $updaterHash)
 if($includeUpdater){New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stampPath) | Out-Null;@{SourceHash=$updaterFingerprint;Version=$updaterVersion;ExeSha256=$updaterHash} | ConvertTo-Json | Set-Content -LiteralPath $stampPath -Encoding utf8}
 # Rebrand the vendor Aion2.Updater.exe from its pristine original so customers receive the
 # Hexor-branded updater alongside the console; the shipped companion is byte-for-byte the
 # artifact the live console produces and the console verifies it against the signed manifest.
 $vendorOriginal=Join-Path $env:LOCALAPPDATA 'Programs\Hexor\AION Auto Updater\_hexor_backup\Aion2.Updater.exe.original'
 if(-not (Test-Path -LiteralPath $vendorOriginal)){throw 'Vendor Aion2.Updater.exe original not found; restore the vendor backup before rebranding.'}
 & dotnet run --project (Join-Path $root 'scripts\HexorUpdaterRebrand\HexorUpdaterRebrand.csproj') -c Release -- $vendorOriginal (Join-Path $payload 'Aion2.Updater.exe')
 if($LASTEXITCODE -ne 0){throw 'Aion2.Updater.exe rebrand failed'}
 $files=@(Get-ChildItem -LiteralPath $payload -File -Recurse)
 $expectedPackageFiles=@('Hexor-AION-Control-Center.exe','Hexor.Updater.exe','Aion2.Updater.exe')
 if(($files.Count -ne $expectedPackageFiles.Count) -or (@($files | Where-Object Name -notin $expectedPackageFiles).Count)){throw 'Unexpected package files'}
 $expectedVersion=[version]$Version
 foreach($file in @($files | Where-Object Name -in @('Hexor-AION-Control-Center.exe','Hexor.Updater.exe'))){
  $actual=[Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
  $actualVersion=[version]::new($actual.FileMajorPart,$actual.FileMinorPart,$actual.FileBuildPart)
  $expectedHelperVersion=if($file.Name -eq 'Hexor.Updater.exe'){[version]$updaterVersion}else{$expectedVersion}
  if($actualVersion -ne $expectedHelperVersion){throw "Published executable version metadata does not match $($file.Name): reports $actualVersion, expected $expectedHelperVersion"}
 }
 if($Version -ceq '1.0.14'){
  Import-Module (Join-Path $PSScriptRoot 'HexorReleaseQualification.psm1') -Force
  $signingDisposition=Test-HexorUnsignedV1014 (Join-Path $root '.build/final-v1.0.14-20261116/unsigned-distribution-owner-exception.json') $payload
  Write-HexorGateEvidence (Join-Path $out 'candidate-authenticode-exception.json') $signingDisposition
 }
 $package=Join-Path $out "Hexor-$Version-win-x64.zip"
 # The OTA package always carries the console; the Hexor updater travels in it only when it
 # changed, and the rebranded vendor updater travels only in its own companion archive so
 # every installed version stays compatible.
 $hexorFiles=@($files | Where-Object Name -in @('Hexor-AION-Control-Center.exe','Hexor.Updater.exe'))
 Compress-Archive -LiteralPath $hexorFiles.FullName -DestinationPath $package
 # Companion release asset: the rebranded vendor updater, delivered as its own archive and
 # referenced from the signed manifest; installed consoles fetch it automatically.
 $companionZip=Join-Path $out "Hexor-Aion2-Updater-$Version-win-x64.zip"
 Compress-Archive -LiteralPath (Join-Path $payload 'Aion2.Updater.exe') -DestinationPath $companionZip
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $companionEntries=[IO.Compression.ZipFile]::OpenRead($companionZip)
 try{
  if($companionEntries.Entries.Count -ne 1 -or $companionEntries.Entries[0].FullName -cne 'Aion2.Updater.exe'){throw 'Companion archive must contain exactly Aion2.Updater.exe'}
  $companionStream=$companionEntries.Entries[0].Open();$magic=@($companionStream.ReadByte(),$companionStream.ReadByte());$companionStream.Dispose()
  if($magic[0] -ne 77 -or $magic[1] -ne 90){throw 'Companion archive entry is not an executable'}
 }finally{$companionEntries.Dispose()}
 $hash=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
 $companionHash=(Get-FileHash -LiteralPath $companionZip -Algorithm SHA256).Hash.ToLowerInvariant()
 # Light package: console only. Consoles whose helper understands partial installs
 # (helper version >= LightMinUpdaterVersion) download this instead of the full package.
 $lightZip=Join-Path $out "Hexor-$Version-light-win-x64.zip"
 Compress-Archive -LiteralPath (@($files | Where-Object Name -eq 'Hexor-AION-Control-Center.exe').FullName) -DestinationPath $lightZip
 $lightEntries=[IO.Compression.ZipFile]::OpenRead($lightZip)
 try{
  if($lightEntries.Entries.Count -ne 1 -or $lightEntries.Entries[0].FullName -cne 'Hexor-AION-Control-Center.exe'){throw 'Light archive must contain exactly the console executable'}
  $lightStream=$lightEntries.Entries[0].Open();$lightMagic=@($lightStream.ReadByte(),$lightStream.ReadByte());$lightStream.Dispose()
  if($lightMagic[0] -ne 77 -or $lightMagic[1] -ne 90){throw 'Light archive entry is not an executable'}
 }finally{$lightEntries.Dispose()}
 $lightHash=(Get-FileHash -LiteralPath $lightZip -Algorithm SHA256).Hash.ToLowerInvariant()
 $lightUrl="https://github.com/$($github.Repository)/releases/download/v$Version/Hexor-$Version-light-win-x64.zip"
 $companionUrl="https://github.com/$($github.Repository)/releases/download/v$Version/Hexor-Aion2-Updater-$Version-win-x64.zip"
 $manifest=[ordered]@{SchemaVersion=1;Version=$Version;ReleaseDate=[DateTimeOffset]::UtcNow.ToString('O');DownloadUrl=$DownloadUrl;Sha256=$hash;ReleaseNotes=$notes;MinimumUpdaterVersion='1.0.0';Mandatory=[bool]$Mandatory;Aion2UpdaterUrl=$companionUrl;Aion2UpdaterSha256=$companionHash;LightDownloadUrl=$lightUrl;LightSha256=$lightHash;LightMinUpdaterVersion='1.0.31'}
 $bytes=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
 $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
 @{Payload=[Convert]::ToBase64String($bytes);Signature=[Convert]::ToBase64String($signature)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'latest.json') -Encoding utf8
 "$hash  $([IO.Path]::GetFileName($package))`n$companionHash  $([IO.Path]::GetFileName($companionZip))`n$lightHash  $([IO.Path]::GetFileName($lightZip))" | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt')
 $notes | Set-Content -LiteralPath (Join-Path $out 'RELEASE-NOTES.md')
 if($Version -ceq '1.0.14'){
  & (Join-Path $PSScriptRoot 'Test-HexorConsoleRelease.ps1') -Phase Package -ReleaseDirectory $out -ResultDirectory (Join-Path $root ('.build/package-gate-'+[guid]::NewGuid().ToString('N')))
 }
 Write-Output "Release prepared at $out. GitHub asset: $DownloadUrl. Stable manifest: $($trust.ManifestUrl)."
}finally{$rsa.Dispose()}
if($PublishGitHub){
 & (Join-Path $PSScriptRoot 'Publish-HexorGitHub.ps1') -Version $Version -GitHubRepository $GitHubRepository -ReleaseDirectory $out -PublishGitHub
}else{
 Write-Output 'No files were uploaded. After configuration approval, publish this prepared release with scripts/Publish-HexorGitHub.ps1 -Version <version> -GitHubRepository <actual-owner/repository> -PublishGitHub.'
}
