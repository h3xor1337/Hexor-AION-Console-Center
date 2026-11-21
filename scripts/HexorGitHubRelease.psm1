# PowerShell 7. Production publishing is entered only through the explicit publishing script/switch.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-HexorGitHubReleaseConfiguration {
    param([Parameter(Mandatory)][string]$GitHubRepository, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)]$Trust)
    if ($GitHubRepository -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$') {
        throw 'GitHubRepository must be the actual OWNER/REPOSITORY, without a URL or .git suffix.'
    }
    $owner, $repository = $GitHubRepository.Split('/')
    if ($owner -in @('OWNER', 'YOUR-OWNER', 'YOUR_OWNER', 'YOUR_ORG', 'YOUR-ORG', 'YOUR-USERNAME', 'USERNAME', 'ORG', 'REPLACE-ME', 'PLACEHOLDER') -or $repository -in @('REPO', 'YOUR-REPO', 'PLACEHOLDER') -or $repository.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Configure the actual approved GitHub owner and repository; placeholders are not accepted.'
    }
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Only stable major.minor.patch versions are supported.' }
    $null = [version]$Version
    $manifestUrl = "https://raw.githubusercontent.com/$GitHubRepository/main/latest.json"
    if ($Trust.ManifestUrl -cne $manifestUrl -or [string]::IsNullOrWhiteSpace($Trust.PublicKeyPem)) {
        throw "Embedded trust must contain the approved public key and exact manifest URL: $manifestUrl"
    }
    [pscustomobject]@{
        Repository = $GitHubRepository
        ManifestUrl = $manifestUrl
        DownloadUrl = "https://github.com/$GitHubRepository/releases/download/v$Version/Hexor-$Version-win-x64.zip"
        Tag = "v$Version"
    }
}

function ConvertTo-HexorSafeDiagnostic {
    param([AllowEmptyString()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $safe = $Text
    $safe = [regex]::Replace($safe, '(?i)\b(?:gh[pous]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b', '[REDACTED]')
    $safe = [regex]::Replace($safe, '(?i)\b(?:authorization|token|access_token)\s*[:=]\s*\S+', '[REDACTED]')
    $safe = [regex]::Replace($safe, '(?i)\bbearer\s+\S+', '[REDACTED]')
    $safe = ($safe -replace '[\r\n]+', ' ').Trim()
    if ($safe.Length -gt 2048) { $safe = $safe.Substring(0, 2048) + ' [truncated]' }
    return $safe
}

function Get-HexorGitHubOperationName {
    param([Parameter(Mandatory)][string[]]$Arguments)
    if ($Arguments.Count -ge 2 -and $Arguments[0] -eq 'api') { return "GitHub API $($Arguments[1])" }
    if ($Arguments.Count -ge 2 -and $Arguments[0] -eq 'release') { return "GitHub release $($Arguments[1])" }
    return 'GitHub CLI operation'
}

function Get-HexorGitHubApiErrorDetail {
    param([AllowEmptyString()][string]$Output, [AllowEmptyString()][string]$ErrorText)
    foreach ($candidate in @($Output, $ErrorText)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $start = $candidate.IndexOf('{')
        if ($start -lt 0) { continue }
        try {
            $apiError = $candidate.Substring($start) | ConvertFrom-Json -Depth 20
            if ($null -ne $apiError -and $apiError.status -and $apiError.message) {
                return "GitHub API HTTP $($apiError.status): $(ConvertTo-HexorSafeDiagnostic ([string]$apiError.message))"
            }
        } catch { }
    }
    return ''
}

function Get-HexorPublicationStateSummary {
    $stateVariable = Get-Variable -Name HexorPublicationState -Scope Script -ErrorAction SilentlyContinue
    if ($null -eq $stateVariable) { return 'no publication workflow state was recorded' }
    $state = $stateVariable.Value
    $assets = if ($state.UploadedAssets.Count) { $state.UploadedAssets -join ', ' } else { 'none' }
    return "draftCreated=$($state.DraftCreated); uploadedAssets=$assets; releasePublished=$($state.ReleasePublished); manifestCommitted=$($state.ManifestCommitted)"
}

function New-HexorGitHubCliFailure {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][int]$ExitCode, [AllowEmptyString()][string]$Output, [AllowEmptyString()][string]$ErrorText)
    $operation = Get-HexorGitHubOperationName $Arguments
    $apiDetail = Get-HexorGitHubApiErrorDetail -Output $Output -ErrorText $ErrorText
    $fallbackSource = if ($ErrorText) { $ErrorText } else { $Output }
    $fallback = ConvertTo-HexorSafeDiagnostic $fallbackSource
    $detail = if ($apiDetail) { $apiDetail } elseif ($fallback) { "GitHub CLI diagnostic: $fallback" } else { 'GitHub CLI returned no diagnostic output.' }
    return "GitHub CLI failed during $operation (exit code $ExitCode). $detail Recorded publication state: $(Get-HexorPublicationStateSummary)."
}

function Read-HexorSignedManifest {
    param([Parameter(Mandatory)][byte[]]$Bytes, [Parameter(Mandatory)][string]$PublicKeyPem)
    if ($Bytes.Length -gt 131072) { throw 'Signed manifest exceeds the metadata size limit.' }
    $envelope = [Text.Encoding]::UTF8.GetString($Bytes) | ConvertFrom-Json
    $payload = [Convert]::FromBase64String($envelope.Payload)
    $signature = [Convert]::FromBase64String($envelope.Signature)
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($PublicKeyPem)
        if ($rsa.KeySize -lt 3072 -or -not $rsa.VerifyData($payload, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) {
            throw 'Signed manifest signature is invalid.'
        }
    } finally { $rsa.Dispose() }
    $manifest = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1 -or $manifest.Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or $manifest.Sha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'Unsupported signed manifest schema, version, or package digest.'
    }
    $null = [version]$manifest.Version
    return $manifest
}

function Invoke-HexorGitHubCommand {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowNotFound, [switch]$NoJson)
    $PSNativeCommandUseErrorActionPreference = $false
    $gh = Get-Command gh -CommandType Application -ErrorAction Stop
    $stderr = [IO.Path]::GetTempFileName()
    try {
        # gh owns authentication. Tokens are never extracted, logged, or supplied to the anonymous downloader.
        $output = & $gh.Source @Arguments 2> $stderr
        $exitCode = $LASTEXITCODE
        $json = $null
        if ($output) {
            try { $json = ($output -join "`n") | ConvertFrom-Json -Depth 100 } catch { if ($exitCode -eq 0 -and -not $NoJson) { throw 'GitHub CLI returned invalid JSON.' } }
        }
        $errorText = [IO.File]::ReadAllText($stderr)
        if ($exitCode -ne 0) {
            if ($AllowNotFound -and ($errorText -match '"status"\s*:\s*"404"' -or ($null -ne $json -and [string]$json.status -eq '404'))) { return $null }
            throw (New-HexorGitHubCliFailure -Arguments $Arguments -ExitCode $exitCode -Output ($output -join "`n") -ErrorText $errorText)
        }
        if (-not $NoJson) { return $json }
    } finally { Remove-Item -LiteralPath $stderr -Force -ErrorAction SilentlyContinue }
}

function Get-HexorGitHubReleases {
    param([Parameter(Mandatory)][string]$GitHubRepository)
    $pages = @(Invoke-HexorGitHubCommand -Arguments @('api', '--paginate', '--slurp', "repos/$GitHubRepository/releases?per_page=100"))
    $releases = [Collections.Generic.List[object]]::new()
    foreach ($page in $pages) {
        if ($null -ne $page.PSObject.Properties['tag_name']) {
            $releases.Add($page)
        } elseif ($page -is [Collections.IEnumerable] -and $page -isnot [string]) {
            foreach ($release in $page) { $releases.Add($release) }
        } elseif ($null -ne $page) {
            $releases.Add($page)
        }
    }
    foreach ($release in $releases) { Write-Output $release }
}

function Get-HexorGitHubReleaseByTag {
    param([Parameter(Mandatory)][string]$GitHubRepository, [Parameter(Mandatory)][string]$Tag)
    $matches = @(Get-HexorGitHubReleases -GitHubRepository $GitHubRepository | Where-Object { $property = $_.PSObject.Properties['tag_name']; $null -ne $property -and [string]$property.Value -ceq $Tag })
    if ($matches.Count -gt 1) {
        $ids = $matches | ForEach-Object id
        throw "Duplicate GitHub releases share tag ${Tag}: $($ids -join ', '). Resolve the duplicates manually before publishing."
    }
    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}

function Assert-HexorLatestVersion {
    param($RemoteContent, [byte[]]$LocalBytes, $LocalManifest, $Trust)
    if ($null -eq $RemoteContent) { return }
    if ($RemoteContent.type -ne 'file' -or $RemoteContent.encoding -ne 'base64' -or -not $RemoteContent.sha) { throw 'Repository latest.json is not a regular GitHub Contents file.' }
    $remoteBytes = [Convert]::FromBase64String($RemoteContent.content)
    $remoteManifest = Read-HexorSignedManifest -Bytes $remoteBytes -PublicKeyPem $Trust.PublicKeyPem
    if ([version]$remoteManifest.Version -gt [version]$LocalManifest.Version) { throw 'Refusing to replace latest.json with an older version.' }
    if ([version]$remoteManifest.Version -eq [version]$LocalManifest.Version -and [Convert]::ToBase64String($remoteBytes) -cne [Convert]::ToBase64String($LocalBytes)) {
        throw 'This version already has a different latest.json. Publish a newer version instead of replacing a release.'
    }
}

function Invoke-HexorPublicAssetVerification {
    param([string]$Root, [string]$ManifestPath, [string]$TrustPath, [string]$DownloadDirectory, $Configuration, [object[]]$Files)
    $project = Join-Path $Root 'ReleaseTools\Hexor.ReleaseTools.csproj'
    $zip = $Files | Where-Object Name -eq "Hexor-$($Configuration.Tag.Substring(1))-win-x64.zip"
    & dotnet run --project $project -c Release -- verify-download $ManifestPath $TrustPath (Join-Path $DownloadDirectory $zip.Name)
    if ($LASTEXITCODE -ne 0) { throw 'Public signed ZIP verification failed. Repository latest.json has not been changed; retry after correcting asset delivery.' }
    foreach ($file in $Files | Where-Object Name -ne $zip.Name) {
        $url = "https://github.com/$($Configuration.Repository)/releases/download/$($Configuration.Tag)/$($file.Name)"
        & dotnet run --project $project -c Release -- verify-file $url $file.Hash (Join-Path $DownloadDirectory $file.Name) $file.Size
        if ($LASTEXITCODE -ne 0) { throw "Public asset verification failed for $($file.Name). Repository latest.json has not been changed." }
    }
}

function Publish-HexorGitHubRelease {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$GitHubRepository, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$ReleaseDirectory, [Parameter(Mandatory)][string]$Root)
    if ($GitHubRepository -ceq 'h3xor1337/hexor-updates' -and $Version -ceq '1.0.14') {
        $gateEvidence = Join-Path $Root ('.build/publication-gate-' + [guid]::NewGuid().ToString('N'))
        & (Join-Path $Root 'scripts/Test-HexorConsoleRelease.ps1') -Phase Publication -ReleaseDirectory $ReleaseDirectory -ResultDirectory $gateEvidence
    }
    $trustPath = Join-Path $Root 'Updates\trust.json'
    $script:HexorPublicationState = [pscustomobject]@{ DraftCreated = $false; UploadedAssets = [Collections.Generic.List[string]]::new(); ReleasePublished = $false; ManifestCommitted = $false }
    $trust = Get-Content -Raw -LiteralPath $trustPath | ConvertFrom-Json
    $config = Get-HexorGitHubReleaseConfiguration -GitHubRepository $GitHubRepository -Version $Version -Trust $trust
    $manifestPath = Join-Path $ReleaseDirectory 'latest.json'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $manifest = Read-HexorSignedManifest -Bytes $manifestBytes -PublicKeyPem $trust.PublicKeyPem
    if ($manifest.Version -cne $Version -or $manifest.DownloadUrl -cne $config.DownloadUrl) { throw 'Prepared manifest version or DownloadUrl does not match the approved GitHub release.' }
    $names = @("Hexor-$Version-win-x64.zip", 'latest.json', 'SHA256SUMS.txt', 'RELEASE-NOTES.md', "Hexor-Aion2-Updater-$Version-win-x64.zip", "Hexor-$Version-light-win-x64.zip")
    $files = @($names | ForEach-Object {
        $path = Join-Path $ReleaseDirectory $_
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($item.PSIsContainer -or $item.Length -gt 600000000 -or $item.Length -eq 0) { throw "Invalid release asset: $_" }
        [pscustomobject]@{ Name = $_; Path = $item.FullName; Size = $item.Length; Hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    if ($files[0].Hash -cne $manifest.Sha256.ToLowerInvariant()) { throw 'Prepared package does not match the signed SHA-256 digest.' }
    $companionHashProperty = $manifest.PSObject.Properties['Aion2UpdaterSha256']
    if ($companionHashProperty -and $companionHashProperty.Value) {
        $companion = $files | Where-Object Name -eq "Hexor-Aion2-Updater-$Version-win-x64.zip"
        if (-not $companion) { throw 'Signed manifest advertises the companion updater but the asset is missing.' }
        if ($companion.Hash -cne $manifest.Aion2UpdaterSha256.ToLowerInvariant()) { throw 'Companion updater package does not match the signed SHA-256 digest.' }
    }
    $lightHashProperty = $manifest.PSObject.Properties['LightSha256']
    if ($lightHashProperty -and $lightHashProperty.Value) {
        $light = $files | Where-Object Name -eq "Hexor-$Version-light-win-x64.zip"
        if (-not $light) { throw 'Signed manifest advertises the light package but the asset is missing.' }
        if ($light.Hash -cne $manifest.LightSha256.ToLowerInvariant()) { throw 'Light package does not match the signed SHA-256 digest.' }
    }
    $checksumLines = [IO.File]::ReadAllText($files[2].Path).Trim() -split "\r?\n"
    foreach ($zip in @($files | Where-Object Name -like '*.zip')) {
        if ($checksumLines -cnotcontains "$($zip.Hash)  $($zip.Name)") { throw "SHA256SUMS.txt does not list the signed package: $($zip.Name)" }
    }
    if ([IO.File]::ReadAllText($files[3].Path).TrimEnd() -cne ([string]$manifest.ReleaseNotes).TrimEnd()) { throw 'Release notes do not match the signed manifest.' }

    $repository = Invoke-HexorGitHubCommand -Arguments @('api', "repos/$GitHubRepository")
    if ($repository.private -or $repository.archived -or $repository.disabled -or $repository.default_branch -cne 'main') { throw 'Use an active public releases repository with main as its default branch.' }
    if ($repository.full_name -cne $GitHubRepository) { throw "Use the repository's canonical owner/name spelling in both configuration and embedded trust: $($repository.full_name)" }
    # A missing root manifest is allowed for the first release; an invalid existing one is never silently replaced.
    $previous = Invoke-HexorGitHubCommand -Arguments @('api', "repos/$GitHubRepository/contents/latest.json?ref=main") -AllowNotFound
    Assert-HexorLatestVersion -RemoteContent $previous -LocalBytes $manifestBytes -LocalManifest $manifest -Trust $trust
    $release = Get-HexorGitHubReleaseByTag -GitHubRepository $GitHubRepository -Tag $config.Tag
    if ($null -eq $release) {
        Invoke-HexorGitHubCommand -Arguments @('release', 'create', $config.Tag, '--repo', $GitHubRepository, '--target', 'main', '--draft', '--title', "Hexor $Version", '--notes-file', $files[3].Path) -NoJson
        $script:HexorPublicationState.DraftCreated = $true
        foreach ($attempt in 1..5) {
            $release = Get-HexorGitHubReleaseByTag -GitHubRepository $GitHubRepository -Tag $config.Tag
            if ($null -ne $release) { break }
            Start-Sleep -Milliseconds 250
        }
        if ($null -eq $release) { throw "Draft release $($config.Tag) was created but is not visible through the GitHub releases list." }
    }
    if ($release.prerelease) { throw 'The versioned Hexor update must be a stable GitHub release.' }
    if ($release.draft) {
        foreach ($file in $files) {
            $existing = @($release.assets | Where-Object name -CEQ $file.Name)
            if ($existing.Count -gt 1) { throw "Duplicate GitHub asset: $($file.Name)" }
            if ($existing.Count -eq 1) {
                if ($existing[0].size -ne $file.Size) { throw "An existing draft asset differs: $($file.Name). Remove the draft manually or choose a new version." }
                if ($existing[0].PSObject.Properties['digest'] -and $existing[0].digest -and $existing[0].digest -cne "sha256:$($file.Hash)") { throw "An existing draft asset has a different SHA-256 digest: $($file.Name)." }
                # A retry never clobbers assets. Even older GitHub assets without server digests are compared byte-for-byte.
                $temporary = Join-Path ([IO.Path]::GetTempPath()) ('HexorDraftAsset-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $temporary | Out-Null
                try {
                    Invoke-HexorGitHubCommand -Arguments @('release', 'download', $config.Tag, '--repo', $GitHubRepository, '--pattern', $file.Name, '--dir', $temporary) -NoJson
                    if ((Get-FileHash -LiteralPath (Join-Path $temporary $file.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Hash) { throw "An existing draft asset differs: $($file.Name)." }
                } finally {
                    # Delete only these explicitly named files and then the empty, newly created directory.
                    Remove-Item -LiteralPath (Join-Path $temporary $file.Name) -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
                }
            } else {
                Invoke-HexorGitHubCommand -Arguments @('release', 'upload', $config.Tag, $file.Path, '--repo', $GitHubRepository) -NoJson
                $script:HexorPublicationState.UploadedAssets.Add($file.Name)
            }
        }
        $release = Get-HexorGitHubReleaseByTag -GitHubRepository $GitHubRepository -Tag $config.Tag
        foreach ($file in $files) {
            $asset = @($release.assets | Where-Object name -CEQ $file.Name)
            if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Size) { throw "Draft asset verification failed: $($file.Name)" }
        }
        Invoke-HexorGitHubCommand -Arguments @('release', 'edit', $config.Tag, '--repo', $GitHubRepository, '--draft=false', '--latest') -NoJson
        $script:HexorPublicationState.ReleasePublished = $true
    }
    # Existing published releases are read-only. All four public files must match before advancing the stable manifest.
    $release = Get-HexorGitHubReleaseByTag -GitHubRepository $GitHubRepository -Tag $config.Tag
    if ($release.draft -or $release.prerelease) { throw 'GitHub release is not publicly available as a stable release.' }
    foreach ($file in $files) {
        $asset = @($release.assets | Where-Object name -CEQ $file.Name)
        $expectedUrl = "https://github.com/$GitHubRepository/releases/download/$($config.Tag)/$($file.Name)"
        if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Size -or $asset[0].browser_download_url -cne $expectedUrl) { throw "Published GitHub asset is missing or differs: $($file.Name)" }
    }
    $downloadDirectory = Join-Path ([IO.Path]::GetTempPath()) ('HexorPublicVerification-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $downloadDirectory | Out-Null
    try {
        Invoke-HexorPublicAssetVerification -Root $Root -ManifestPath $manifestPath -TrustPath $trustPath -DownloadDirectory $downloadDirectory -Configuration $config -Files $files
    } finally {
        foreach ($file in $files) { Remove-Item -LiteralPath (Join-Path $downloadDirectory $file.Name) -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $downloadDirectory -Force -ErrorAction SilentlyContinue
    }
    # Re-read after delivery verification: reject downgrades and use the current blob SHA for optimistic concurrency.
    $previous = Invoke-HexorGitHubCommand -Arguments @('api', "repos/$GitHubRepository/contents/latest.json?ref=main") -AllowNotFound
    Assert-HexorLatestVersion -RemoteContent $previous -LocalBytes $manifestBytes -LocalManifest $manifest -Trust $trust
    $sameManifest = $null -ne $previous -and [Convert]::ToBase64String([Convert]::FromBase64String($previous.content)) -ceq [Convert]::ToBase64String($manifestBytes)
    if (-not $sameManifest) {
        $body = [ordered]@{ message = "Publish signed Hexor $Version update manifest"; branch = 'main'; content = [Convert]::ToBase64String($manifestBytes) }
        if ($null -ne $previous) { $body.sha = $previous.sha }
        $requestPath = [IO.Path]::GetTempFileName()
        try {
            [IO.File]::WriteAllText($requestPath, ($body | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
            $null = Invoke-HexorGitHubCommand -Arguments @('api', '--method', 'PUT', "repos/$GitHubRepository/contents/latest.json", '--input', $requestPath)
            $script:HexorPublicationState.ManifestCommitted = $true
        } finally { Remove-Item -LiteralPath $requestPath -Force -ErrorAction SilentlyContinue }
    }
    Write-Output "Published and verified Hexor $Version. Manifest: $($config.ManifestUrl)"
    Write-Output 'GitHub raw content caches may briefly serve the preceding signed manifest; verify raw delivery before customer acceptance testing.'
}

Export-ModuleMember -Function Get-HexorGitHubReleaseConfiguration, Publish-HexorGitHubRelease
