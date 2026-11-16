#requires -Version 7.0
<# Read-only verification. This script cannot sign, import certificates or change Windows trust. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string[]]$ApprovedSignerThumbprint,
    [Parameter(Mandatory)][string]$ApprovedPublisherSubject,
    [string]$SignToolPath,
    [string]$ResultPath
)
$ErrorActionPreference = 'Stop'
if ($ResultPath -and (Test-Path -LiteralPath $ResultPath)) { throw 'Preserve existing verification evidence; choose a new ResultPath.' }
$allowed = @('Hexor-AION-Control-Center.exe', 'Hexor.Updater.exe', 'Aion2Updater_Setup.exe')
$results = [Collections.Generic.List[object]]::new()
foreach ($target in $Path) {
    $file = Get-Item -LiteralPath $target -ErrorAction Stop
    if ($file.PSIsContainer -or $file.Name -cnotin $allowed) { throw 'Only explicitly named Hexor application, helper and installer artifacts are supported. Vendor files are excluded.' }
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked artifact rejected.' }
    $before = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') { throw "A valid embedded Authenticode signature is required: $($file.Name) ($($signature.Status))." }
    $signer = $signature.SignerCertificate
    if ($signer.Thumbprint -notin $ApprovedSignerThumbprint -or $signer.Subject -cne $ApprovedPublisherSubject) { throw "Signer is outside the explicitly approved identity: $($file.Name)." }
    if ($signer.Subject -ceq $signer.Issuer) { throw 'A self-signed production signer is not accepted.' }
    $eku = @($signer.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($eku.Count -ne 1 -or '1.3.6.1.5.5.7.3.3' -notin @($eku[0].EnhancedKeyUsages | ForEach-Object Value)) { throw 'Signer lacks the code-signing EKU.' }
    if (-not $signature.TimeStamperCertificate) { throw "A verified timestamp is required: $($file.Name)." }
    if (-not $SignToolPath) {
        $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if (-not $command) { throw 'Install the approved Windows SDK SignTool, or supply -SignToolPath. Verification cannot be completed without it.' }
        $SignToolPath = $command.Source
    }
    $tool = Get-Item -LiteralPath $SignToolPath -ErrorAction Stop
    if ($tool.PSIsContainer -or $tool.Name -ine 'signtool.exe') { throw 'SignToolPath must name the approved Windows SDK signtool.exe.' }
    # /pa: Authenticode policy; /all: every embedded signature; /tw: missing timestamp warning.
    & $tool.FullName verify /pa /all /v /tw $file.FullName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SignTool rejected the signature, chain or timestamp: $($file.Name), exit $LASTEXITCODE." }
    $after = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ($before -cne $after) { throw 'Artifact changed during verification.' }
    $results.Add([pscustomobject]@{
        Path = $file.FullName; SHA256 = $after; Publisher = $signer.Subject
        SignerThumbprint = $signer.Thumbprint; TimestampPublisher = $signature.TimeStamperCertificate.Subject
        SignatureStatus = 'Valid'; SignToolVerified = $true; CheckedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    })
}
if ($ResultPath) {
    # CreateNew preserves other evidence even if another process creates the path concurrently.
    $stream = [IO.File]::Open($ResultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $results.ToArray() -Depth 4)); $stream.Write($bytes) }
    finally { $stream.Dispose() }
}
$results.ToArray()
