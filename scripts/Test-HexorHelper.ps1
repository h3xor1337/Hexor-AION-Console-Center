#requires -Version 7.0
param([string]$BaselineDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $BaselineDirectory){$BaselineDirectory=Join-Path $root 'output\auto-update'}
$baselineApp=Join-Path $BaselineDirectory 'Hexor-AION-Control-Center.exe'
$baselineVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($baselineApp))
if([version]::new($baselineVersion.FileMajorPart,$baselineVersion.FileMinorPart,$baselineVersion.FileBuildPart) -ge [version]'1.0.1'){throw 'This integration fixture requires a baseline older than 1.0.1. Publish an explicit 1.0.0 baseline first.'}
$sandbox=Join-Path ([IO.Path]::GetTempPath()) ('HexorHelperTest-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
$rsa=[Security.Cryptography.RSA]::Create(3072)
try{
 @{ManifestUrl='https://raw.githubusercontent.com/hexor-test-fixture/hexor-updates/main/latest.json';PublicKeyPem=$rsa.ExportSubjectPublicKeyInfoPem()} | ConvertTo-Json | Set-Content (Join-Path $sandbox 'trust.json')
 $core=[Security.SecurityElement]::Escape((Join-Path $root 'Updates\UpdateCore.cs'))
 $program=[Security.SecurityElement]::Escape((Join-Path $root 'Updater\Program.cs'))
 @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>Hexor.Updater</AssemblyName><Version>1.0.0</Version><PublishSingleFile>true</PublishSingleFile><IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract></PropertyGroup><ItemGroup><Compile Include="$core"/><Compile Include="$program"/><EmbeddedResource Include="trust.json" LogicalName="Hexor.UpdateTrust"/></ItemGroup></Project>
"@ | Set-Content (Join-Path $sandbox 'Helper.csproj')
 & dotnet publish (Join-Path $sandbox 'Helper.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $sandbox 'runner')
 if($LASTEXITCODE -ne 0){throw 'Test helper build failed'}
 $install=Join-Path $sandbox 'installed';New-Item -ItemType Directory -Path $install | Out-Null
 Copy-Item -LiteralPath $baselineApp -Destination $install
 Copy-Item -LiteralPath (Join-Path $sandbox 'runner\Hexor.Updater.exe') -Destination $install
 Set-Content (Join-Path $install 'customer.json') '{"preserve":true}'
 $payload=Join-Path $sandbox 'payload'
 & dotnet publish (Join-Path $root 'Hexor.ControlCenter.csproj') -c Release -r win-x64 --self-contained true -p:Version=1.0.1 -p:DebugType=None -p:DebugSymbols=false -o $payload
 if($LASTEXITCODE -ne 0){throw 'Test release build failed'}
 Copy-Item -LiteralPath (Join-Path $install 'Hexor.Updater.exe') -Destination $payload
 $package=Join-Path $sandbox 'package.zip';Compress-Archive -LiteralPath (Join-Path $payload 'Hexor-AION-Control-Center.exe'),(Join-Path $payload 'Hexor.Updater.exe') -DestinationPath $package
 $m=@{SchemaVersion=1;Version='1.0.1';ReleaseDate=[DateTimeOffset]::UtcNow.ToString('O');DownloadUrl='https://github.com/hexor-test-fixture/hexor-updates/releases/download/v1.0.1/Hexor-1.0.1-win-x64.zip';Sha256=(Get-FileHash $package).Hash;ReleaseNotes='Isolated helper test';MinimumUpdaterVersion='1.0.0';Mandatory=$false}
 $bytes=[Text.Encoding]::UTF8.GetBytes(($m | ConvertTo-Json -Compress))
 @{Payload=[Convert]::ToBase64String($bytes);Signature=[Convert]::ToBase64String($rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss))} | ConvertTo-Json | Set-Content (Join-Path $sandbox 'manifest.json')
 $psi=[Diagnostics.ProcessStartInfo]::new((Join-Path $sandbox 'runner\Hexor.Updater.exe'));$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
 $psi.Environment['HEXOR_UPDATER_NO_DIALOG']='1'
 foreach($arg in @($install,(Join-Path $sandbox 'manifest.json'),$package,'-1','exit')){$psi.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::Start($psi);if(-not $p.WaitForExit(30000) -or $p.ExitCode -ne 0){throw 'Signed helper process installation failed'}
 foreach($name in @('Hexor-AION-Control-Center.exe','Hexor.Updater.exe')){if((Get-FileHash (Join-Path $install $name)).Hash -ne (Get-FileHash (Join-Path $payload $name)).Hash -or -not (Test-Path (Join-Path $install ".hexor-update\$name"))){throw 'Installed or backup binary mismatch'}}
 if((Get-Content (Join-Path $install 'customer.json') -Raw).Trim() -ne '{"preserve":true}'){throw 'Settings changed'}
 Write-Output 'PASS: standalone helper validates signed metadata/package, installs real single-file executables, updates its installed copy, and retains backups and customer data.'
 $link=Join-Path $sandbox 'Hexor.lnk';$shell=New-Object -ComObject WScript.Shell;$shortcut=$shell.CreateShortcut($link);$shortcut.TargetPath=Join-Path $install 'Hexor-AION-Control-Center.exe';$shortcut.WorkingDirectory=$install;$shortcut.Save()
 Start-Process -FilePath $link
 Start-Sleep -Seconds 4
 $app=Get-Process -Name 'Hexor-AION-Control-Center' | Where-Object Path -eq (Join-Path $install 'Hexor-AION-Control-Center.exe')
 if(-not $app){throw 'Updated app did not launch via stable shortcut'}
 $app | Stop-Process
 $app | Wait-Process -Timeout 15
 foreach($stopped in $app){$stopped.Dispose()}
 Write-Output 'PASS: updated executable launches through unchanged shortcut path.'
 # Use an independent bootstrap installation for the locked-file failure case.
 $install=Join-Path $sandbox 'rollback-install';New-Item -ItemType Directory -Path $install | Out-Null
 Copy-Item -LiteralPath $baselineApp -Destination $install
 Copy-Item -LiteralPath (Join-Path $sandbox 'runner\Hexor.Updater.exe') -Destination $install
 $psi.ArgumentList.Clear();foreach($arg in @($install,(Join-Path $sandbox 'manifest.json'),$package,'-1','exit')){$psi.ArgumentList.Add($arg)}
 $locked=[IO.FileStream]::new((Join-Path $install 'Hexor.Updater.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{$p=[Diagnostics.Process]::Start($psi);if(-not $p.WaitForExit(30000) -or $p.ExitCode -eq 0){throw 'Locked helper should reject installation'}}finally{$locked.Dispose()}
 if(-not (Test-Path (Join-Path $install '.hexor-update\pending'))){throw 'Failed rollback should retain recovery journal'}
 $recover=[Diagnostics.ProcessStartInfo]::new((Join-Path $sandbox 'runner\Hexor.Updater.exe'));$recover.UseShellExecute=$false;$recover.CreateNoWindow=$true
 $recover.Environment['HEXOR_UPDATER_NO_DIALOG']='1'
 foreach($arg in @('--recover',$install,'-1')){$recover.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::Start($recover);if(-not $p.WaitForExit(30000) -or $p.ExitCode -ne 0){throw 'Recovery helper failed'}
 Start-Sleep -Seconds 4
 Get-Process -Name 'Hexor-AION-Control-Center' | Where-Object Path -eq (Join-Path $install 'Hexor-AION-Control-Center.exe') | Stop-Process
 if((Test-Path (Join-Path $install '.hexor-update\pending')) -or (Get-FileHash (Join-Path $install 'Hexor-AION-Control-Center.exe')).Hash -ne (Get-FileHash $baselineApp).Hash){throw 'Recovery did not restore bootstrap application'}
 if((Get-FileHash (Join-Path $install 'Hexor.Updater.exe')).Hash -ne (Get-FileHash (Join-Path $sandbox 'runner\Hexor.Updater.exe')).Hash){throw 'Recovery did not restore bootstrap helper'}
 Write-Output 'PASS: standalone helper fails safely on locked self replacement, retains recovery journal, then recovery restores and restarts the previous application.'
 Write-Output "Test artifacts retained: $sandbox"
}finally{$rsa.Dispose()}
