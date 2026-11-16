#requires -Version 7.0
param([Parameter(Mandatory)][string]$PrivateKeyPath,[string]$GitHubRepository,[string]$ManifestUrl,[switch]$ValidateOnly)
$ErrorActionPreference='Stop'

function Assert-SigningDirectoryAcl([string]$Directory)
{
 if(-not (Test-Path -LiteralPath $Directory -PathType Container)){throw 'Signing-key parent directory must already exist and be secured before key generation.'}
 $item=Get-Item -LiteralPath $Directory -Force
 if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Signing-key directory cannot be a reparse point.'}
 $acl=Get-Acl -LiteralPath $Directory
 if(-not $acl.AreAccessRulesProtected){throw 'Signing-key directory must have inherited permissions disabled.'}
 $currentSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 $allowed=@($currentSid,'S-1-5-18')
 $full=[Security.AccessControl.FileSystemRights]::FullControl
 $fullControl=@{}
 foreach($rule in $acl.Access){
  if($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow){continue}
  try{$sid=$rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value}catch{throw "Cannot validate signing-directory principal: $($rule.IdentityReference.Value)"}
  if($sid -notin $allowed){throw "Signing-key directory grants access to an unapproved principal: $($rule.IdentityReference.Value)"}
  if(($rule.FileSystemRights -band $full) -eq $full){$fullControl[$sid]=$true}
 }
 if(-not $fullControl[$currentSid] -or -not $fullControl['S-1-5-18']){throw 'Signing-key directory must grant Full Control to the signing account and SYSTEM.'}
}

$root=Split-Path -Parent $PSScriptRoot
if(-not $GitHubRepository){
 if($ManifestUrl -cmatch '^https://raw\.githubusercontent\.com/([^/]+/[^/]+)/main/latest\.json$'){$GitHubRepository=$Matches[1]}else{throw 'Supply the actual approved -GitHubRepository owner/repository, or its raw main/latest.json URL.'}
}
$expectedUrl="https://raw.githubusercontent.com/$GitHubRepository/main/latest.json"
if($ManifestUrl -and $ManifestUrl -cne $expectedUrl){throw 'ManifestUrl does not match the approved GitHub repository.'}
Import-Module (Join-Path $PSScriptRoot 'HexorGitHubRelease.psm1') -Force
# Validate repository/endpoint before any key is generated. This temporary marker is never saved.
$null=Get-HexorGitHubReleaseConfiguration -GitHubRepository $GitHubRepository -Version '1.0.0' -Trust ([pscustomobject]@{ManifestUrl=$expectedUrl;PublicKeyPem='pending-generation'})
$ManifestUrl=$expectedUrl
$keyPath=[IO.Path]::GetFullPath($PrivateKeyPath)
if($keyPath.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Private key must remain outside the repository'}
$aioRoot=Join-Path (Split-Path -Parent $root) 'Aion AIO'
if($keyPath.StartsWith($aioRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Private key must remain outside the AIO repository'}
$keyDirectory=Split-Path -Parent $keyPath
Assert-SigningDirectoryAcl $keyDirectory
$trustPath=Join-Path $root 'Updates\trust.json'
$trust=Get-Content -Raw $trustPath | ConvertFrom-Json
if($ValidateOnly){
 if(-not $trust.PublicKeyPem){throw 'Embedded production public key is missing.'}
 $null=Get-HexorGitHubReleaseConfiguration -GitHubRepository $GitHubRepository -Version '1.0.0' -Trust $trust
 if(Test-Path -LiteralPath $keyPath -PathType Leaf){$private=[Security.Cryptography.RSA]::Create();try{$private.ImportFromPem([IO.File]::ReadAllText($keyPath));if($private.KeySize -lt 3072 -or $private.ExportSubjectPublicKeyInfoPem().Trim() -cne ([string]$trust.PublicKeyPem).Trim()){throw 'Signing private key does not match embedded production public trust.'}}finally{$private.Dispose()};Write-Output "Existing signing key and public trust validated. No key or trust material was created. Approved manifest: $ManifestUrl"}
 else{Write-Output "Existing public trust validated. No key or trust material was created. Approved manifest: $ManifestUrl"}
 return
}
if(Test-Path -LiteralPath $keyPath){throw 'Refusing to replace an existing signing key'}
if($trust.PublicKeyPem){throw 'Trust already provisioned. Replacing it would break installed customers; key rotation needs a separate migration.'}
$rsa=[Security.Cryptography.RSA]::Create(3072)
try{
 $keyFile=[IO.FileStream]::new($keyPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{
  $keyBytes=[Text.Encoding]::UTF8.GetBytes($rsa.ExportPkcs8PrivateKeyPem())
  try{$keyFile.Write($keyBytes,0,$keyBytes.Length);$keyFile.Flush($true)}finally{[Security.Cryptography.CryptographicOperations]::ZeroMemory($keyBytes)}
 }finally{$keyFile.Dispose()}
 @{ManifestUrl=$ManifestUrl;PublicKeyPem=$rsa.ExportSubjectPublicKeyInfoPem()} | ConvertTo-Json | Set-Content -LiteralPath $trustPath
 Write-Output 'Public trust configured. Protect and back up the external private PEM; rebuild the bootstrap AIO before distributing it.'
}finally{$rsa.Dispose()}
