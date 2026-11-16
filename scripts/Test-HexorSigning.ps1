#requires -Version 7.0
# Isolated validation tests. No private key or trust material is generated.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$initializer = Join-Path $PSScriptRoot 'Initialize-HexorSigning.ps1'
$trustPath = Join-Path $root 'Updates\trust.json'
$trustHash = (Get-FileHash -LiteralPath $trustPath -Algorithm SHA256).Hash
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('HexorSigningTests-' + [guid]::NewGuid().ToString('N'))
$secure = Join-Path $sandbox 'secure'
$keyPath = Join-Path $secure 'release-private.pem'
New-Item -ItemType Directory -Path $secure | Out-Null

try {
    $rejected = $false
    try {
        & $initializer -PrivateKeyPath $keyPath -GitHubRepository 'h3xor1337/hexor-updates' -ValidateOnly | Out-Null
    } catch { $rejected = $_.Exception.Message -like '*inherited permissions disabled*' }
    if (-not $rejected) { throw 'An inherited signing-directory ACL was not rejected.' }
    Write-Output 'PASS: inherited signing-directory permissions are rejected.'

    $signer = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $secure /inheritance:r /grant:r "${signer}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not establish the isolated signing-directory ACL.' }
    $result = & $initializer -PrivateKeyPath $keyPath -GitHubRepository 'h3xor1337/hexor-updates' -ValidateOnly
    if ($result -notlike '*No key or trust material was created*') { throw 'Validation-only result was not explicit.' }
    if (Test-Path -LiteralPath $keyPath) { throw 'Validation-only mode created a private key.' }
    if ((Get-FileHash -LiteralPath $trustPath -Algorithm SHA256).Hash -cne $trustHash) { throw 'Validation-only mode changed embedded trust.' }
    Write-Output 'PASS: protected signing-directory ACL validates without creating credentials or changing trust.'
} finally {
    if (Test-Path -LiteralPath $sandbox) {
        & icacls.exe $sandbox /reset /T /C | Out-Null
        Remove-Item -LiteralPath $sandbox -Recurse -Force
    }
}
