#requires -Version 7.0
# Isolated publishing orchestration tests: GitHub and network calls are mocked; no production trust is changed.
$ErrorActionPreference = 'Stop'
$module = Import-Module (Join-Path $PSScriptRoot 'HexorGitHubRelease.psm1') -Force -PassThru
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('HexorGitHubPublisherTests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $sandbox 'Updates') | Out-Null
$rsa = [Security.Cryptography.RSA]::Create(3072)
$repository = 'HexorPublisherTests/hexor-updates'
$trust = @{ ManifestUrl = "https://raw.githubusercontent.com/$repository/main/latest.json"; PublicKeyPem = $rsa.ExportSubjectPublicKeyInfoPem() }
$trust | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sandbox 'Updates\trust.json') -Encoding utf8
$script:passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
}
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert-True $rejected $Message
    $script:passed++
    Write-Output "PASS: $Message"
}
function New-TestManifest([string]$Version, [string]$Hash, [string]$Notes) {
    $manifest = [ordered]@{ SchemaVersion = 1; Version = $Version; ReleaseDate = [DateTimeOffset]::UtcNow.ToString('O'); DownloadUrl = "https://github.com/$repository/releases/download/v$Version/Hexor-$Version-win-x64.zip"; Sha256 = $Hash; ReleaseNotes = $Notes; MinimumUpdaterVersion = '1.0.0'; Mandatory = $false }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $envelope = @{ Payload = [Convert]::ToBase64String($bytes); Signature = [Convert]::ToBase64String($rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) }
    return [Text.Encoding]::UTF8.GetBytes(($envelope | ConvertTo-Json))
}
function New-TestRelease([string]$Name) {
    $releaseDirectory = Join-Path $sandbox $Name
    New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    $zipPath = Join-Path $releaseDirectory 'Hexor-1.0.1-win-x64.zip'
    [IO.File]::WriteAllBytes($zipPath, [Text.Encoding]::UTF8.GetBytes('Isolated publishing test package; no executable is built or installed.'))
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $bytes = New-TestManifest '1.0.1' $hash 'Mock release notes'
    [IO.File]::WriteAllBytes((Join-Path $releaseDirectory 'latest.json'), $bytes)
    $companionPath = Join-Path $releaseDirectory 'Hexor-Aion2-Updater-1.0.1-win-x64.zip'
    [IO.File]::WriteAllBytes($companionPath, [Text.Encoding]::UTF8.GetBytes('Isolated companion updater test package; no executable is built or installed.'))
    $companionHash = (Get-FileHash -LiteralPath $companionPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'SHA256SUMS.txt'), "$hash  Hexor-1.0.1-win-x64.zip`n$companionHash  Hexor-Aion2-Updater-1.0.1-win-x64.zip")
    [IO.File]::WriteAllText((Join-Path $releaseDirectory 'RELEASE-NOTES.md'), 'Mock release notes')
    return $releaseDirectory
}
function New-TestState([string]$ReleaseDirectory) {
    return [pscustomobject]@{
        Directory = $ReleaseDirectory
        Repository = $repository
        Calls = [Collections.Generic.List[string]]::new()
        Release = $null
        Remote = $null
        Reads = 0
        RacingRemote = $null
        PublicVerificationFails = $false
        FailUploadName = $null
        PrivateRepository = $false
        FailManifestPut = $false
        ReleaseListQueries = 0
        TagLookupCalled = $false
        Puts = 0
        CreatedBody = $null
    }
}
function Set-TestState($State) {
    & $module {
        param($State)
        $script:TestState = $State
        function script:Invoke-HexorGitHubCommand {
            param([string[]]$Arguments, [switch]$AllowNotFound, [switch]$NoJson)
            $state = $script:TestState
            if ($Arguments[0] -eq 'api') {
                if ($Arguments -contains 'PUT') {
                    if ($state.FailManifestPut) { throw 'Injected manifest-publication failure' }
                    $state.Calls.Add('manifest-put')
                    $bodyPath = $Arguments[[Array]::IndexOf($Arguments, '--input') + 1]
                    $state.CreatedBody = Get-Content -LiteralPath $bodyPath -Raw | ConvertFrom-Json
                    $state.Puts++
                    $state.Remote = [pscustomobject]@{ type = 'file'; encoding = 'base64'; sha = 'new-blob-sha'; content = $state.CreatedBody.content }
                    return [pscustomobject]@{ content = [pscustomobject]@{ sha = 'new-blob-sha' } }
                }
                if ($Arguments[1] -eq "repos/$($state.Repository)") { return [pscustomobject]@{ full_name = $state.Repository; private = $state.PrivateRepository; archived = $false; disabled = $false; default_branch = 'main' } }
                if ($Arguments[1] -like '*/contents/latest.json?ref=main') {
                    $state.Reads++
                    if ($state.Reads -ge 2 -and $null -ne $state.RacingRemote) { return $state.RacingRemote }
                    return $state.Remote
                }
                if ($Arguments -contains '--slurp') {
                    $null = $state.ReleaseListQueries++
                    if ($null -ne $state.Release) { return $state.Release }
                    return
                }
                if ($Arguments[1] -like '*/releases/tags/v1.0.1') { $state.TagLookupCalled = $true; throw 'Draft tag lookup must not be used.' }
            }
            if ($Arguments[0] -eq 'release') {
                switch ($Arguments[1]) {
                    'create' {
                        $state.Calls.Add('create-draft')
                        $state.Release = [pscustomobject]@{ id = 1; tag_name = 'v1.0.1'; draft = $true; prerelease = $false; assets = [Collections.Generic.List[object]]::new() }
                        return
                    }
                    'upload' {
                        $item = Get-Item -LiteralPath $Arguments[3]
                        if ($item.Name -ceq $state.FailUploadName) { throw 'Injected asset-upload failure' }
                        $state.Calls.Add("upload:$($item.Name)")
                        $state.Release.assets.Add([pscustomobject]@{ name = $item.Name; size = $item.Length; digest = "sha256:$((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"; browser_download_url = "https://github.com/$($state.Repository)/releases/download/v1.0.1/$($item.Name)" })
                        return
                    }
                    'download' {
                        $name = $Arguments[[Array]::IndexOf($Arguments, '--pattern') + 1]
                        $directory = $Arguments[[Array]::IndexOf($Arguments, '--dir') + 1]
                        Copy-Item -LiteralPath (Join-Path $state.Directory $name) -Destination (Join-Path $directory $name)
                        $state.Calls.Add("draft-download:$name")
                        return
                    }
                    'edit' {
                        $state.Calls.Add('publish-release')
                        $state.Release.draft = $false
                        return
                    }
                }
            }
            throw "Unexpected mocked GitHub operation: $($Arguments -join ' ')"
        }
        function script:Invoke-HexorPublicAssetVerification {
            param([string]$Root, [string]$ManifestPath, [string]$TrustPath, [string]$DownloadDirectory, $Configuration, [object[]]$Files)
            $state = $script:TestState
            if ($state.Release.draft -or $Files.Count -ne 5 -or $state.Release.assets.Count -ne 5) { throw 'Public verification ran before all five assets were published.' }
            $state.Calls.Add('verify-public-assets')
            if ($state.PublicVerificationFails) { throw 'Injected anonymous download/hash failure' }
        }
    } $State
}
function Invoke-TestPublish([string]$Directory) {
    Publish-HexorGitHubRelease -GitHubRepository $repository -Version '1.0.1' -ReleaseDirectory $Directory -Root $sandbox | Out-Null
}

try {
    $directory = New-TestRelease 'valid'
    $state = New-TestState $directory
    Set-TestState $state
    Invoke-TestPublish $directory
    Assert-True ($state.Puts -eq 1) 'stable manifest committed exactly once'
    Assert-True ($state.Calls.IndexOf('publish-release') -gt $state.Calls.IndexOf('upload:RELEASE-NOTES.md')) 'all assets upload before publication'
    Assert-True ($state.Calls.IndexOf('manifest-put') -gt $state.Calls.IndexOf('verify-public-assets')) 'public assets verify before stable manifest commit'
    Assert-True ($state.CreatedBody.branch -ceq 'main' -and $state.CreatedBody.content -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $directory 'latest.json')))) 'Contents API body preserves exact signed bytes and branch'
    Assert-True ($state.ReleaseListQueries -gt 0 -and -not $state.TagLookupCalled) 'draft discovery uses the releases list, not the draft-invisible tag endpoint'
    $script:passed++; Write-Output 'PASS: draft creation, four uploads, public verification, then exact signed manifest commit'

    $state = New-TestState $directory
    $state.Release = [pscustomobject]@{ id = 42; tag_name = 'v1.0.1'; draft = $true; prerelease = $false; assets = [Collections.Generic.List[object]]::new() }
    Set-TestState $state
    Invoke-TestPublish $directory
    Assert-True (-not $state.Calls.Contains('create-draft') -and $state.ReleaseListQueries -gt 0 -and -not $state.TagLookupCalled) 'existing draft release is resumed through the releases list without duplicate creation'
    $script:passed++; Write-Output 'PASS: existing draft resumes without the draft-invisible tag endpoint'

    $state.Calls.Clear()
    Invoke-TestPublish $directory
    Assert-True ($state.Puts -eq 1 -and -not $state.Calls.Contains('publish-release') -and -not $state.Calls.Contains('manifest-put') -and $state.Calls.Contains('verify-public-assets')) 'published-release retry is read-only when latest already matches'
    $script:passed++; Write-Output 'PASS: published-release retry verifies assets without clobbering or committing duplicate metadata'

    $state = New-TestState $directory; $state.PublicVerificationFails = $true; Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'public download failure blocks latest.json commit'
    Assert-True ($state.Puts -eq 0) 'no commit after public verification failure'
    $state.PublicVerificationFails = $false
    Invoke-TestPublish $directory
    Assert-True ($state.Puts -eq 1 -and (@($state.Calls | Where-Object { $_ -like 'upload:*' })).Count -eq 5) 'retry of published assets does not upload again'
    $script:passed++; Write-Output 'PASS: a failed public download can resume the prepared release without republishing assets'

    $state = New-TestState $directory; $state.FailUploadName = 'latest.json'; Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'interrupted upload leaves draft and does not publish the stable manifest'
    Assert-True ($state.Release.draft -and $state.Puts -eq 0 -and $state.Release.assets.Count -eq 1) 'interrupted upload retains only uploaded draft asset'
    $state.FailUploadName = $null
    Invoke-TestPublish $directory
    Assert-True ($state.Puts -eq 1 -and $state.Calls.Contains('draft-download:Hexor-1.0.1-win-x64.zip') -and (@($state.Calls | Where-Object { $_ -like 'upload:*' })).Count -eq 5) 'draft retry compares existing bytes and uploads missing assets only'
    $script:passed++; Write-Output 'PASS: draft upload resumes without clobbering existing assets'

    $state = New-TestState $directory; $state.FailUploadName = 'latest.json'; Set-TestState $state
    try { Invoke-TestPublish $directory } catch { }
    $state.FailUploadName = $null
    $state.Release.assets[0].digest = 'sha256:' + ('0' * 64)
    Assert-Rejected { Invoke-TestPublish $directory } 'different existing draft asset digest rejected without clobbering'
    Assert-True ($state.Release.draft -and $state.Puts -eq 0) 'different draft asset never published'

    $state = New-TestState $directory
    $older = New-TestManifest '1.0.0' ('c' * 64) 'Previous release'
    $state.Remote = [pscustomobject]@{ type = 'file'; encoding = 'base64'; sha = 'expected-existing-blob'; content = [Convert]::ToBase64String($older) }
    Set-TestState $state
    Invoke-TestPublish $directory
    Assert-True ($state.Puts -eq 1 -and $state.CreatedBody.sha -ceq 'expected-existing-blob') 'updating prior stable manifest includes current Git blob SHA'
    $script:passed++; Write-Output 'PASS: signed older stable manifest advances with optimistic concurrency'

    $state = New-TestState $directory; $state.PrivateRepository = $true; Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'private repository rejected before mutation'
    Assert-True ($state.Calls.Count -eq 0) 'private repository never mutated'

    $state = New-TestState $directory
    $state.Release = @(
        [pscustomobject]@{ id = 101; tag_name = 'v1.0.1'; draft = $true; prerelease = $false; assets = [Collections.Generic.List[object]]::new() },
        [pscustomobject]@{ id = 102; tag_name = 'v1.0.1'; draft = $true; prerelease = $false; assets = [Collections.Generic.List[object]]::new() }
    )
    Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'duplicate drafts with the same tag block publication'
    Assert-True (-not $state.Calls.Contains('create-draft') -and $state.Puts -eq 0) 'duplicate drafts are never resumed, replaced, or published'

    $state = New-TestState $directory
    $newer = New-TestManifest '1.0.2' ('a' * 64) 'Already newer'
    $state.Remote = [pscustomobject]@{ type = 'file'; encoding = 'base64'; sha = 'prior-blob'; content = [Convert]::ToBase64String($newer) }
    Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'signed newer root manifest blocks downgrade before release creation'
    Assert-True ($state.Calls.Count -eq 0) 'downgrade caused no mutation'

    $state = New-TestState $directory
    $state.RacingRemote = [pscustomobject]@{ type = 'file'; encoding = 'base64'; sha = 'racing-blob'; content = [Convert]::ToBase64String($newer) }
    Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'newer manifest appearing during public verification blocks final commit'
    Assert-True ($state.Puts -eq 0) 'concurrent newer manifest never replaced'

    $state = New-TestState $directory; $state.FailManifestPut = $true; Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'manifest publication failure leaves latest.json uncommitted'
    Assert-True ($state.Puts -eq 0 -and $state.Calls.Contains('publish-release')) 'manifest failure occurs only after public release verification'

    $state = New-TestState $directory
    $different = New-TestManifest '1.0.1' ('b' * 64) 'Different release'
    $state.Remote = [pscustomobject]@{ type = 'file'; encoding = 'base64'; sha = 'different-blob'; content = [Convert]::ToBase64String($different) }
    Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'same version with different signed bytes rejected'

    $state = New-TestState $directory
    $state.Remote = [pscustomobject]@{ type = 'file'; encoding = 'base64'; sha = 'invalid-blob'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"Payload":"AA==","Signature":"AA=="}')) }
    Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $directory } 'invalid existing root signature rejected'

    $tampered = New-TestRelease 'tampered-package'
    Add-Content -LiteralPath (Join-Path $tampered 'Hexor-1.0.1-win-x64.zip') -Value 'tampered'
    $state = New-TestState $tampered; Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $tampered } 'tampered local ZIP rejected before GitHub access'
    Assert-True ($state.Calls.Count -eq 0) 'tampered package caused no mutation'

    $invalid = New-TestRelease 'invalid-signature'
    $envelope = Get-Content -Raw -LiteralPath (Join-Path $invalid 'latest.json') | ConvertFrom-Json
    $envelope.Signature = [Convert]::ToBase64String((New-Object byte[] 384))
    $envelope | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $invalid 'latest.json')
    $state = New-TestState $invalid; Set-TestState $state
    Assert-Rejected { Invoke-TestPublish $invalid } 'invalid prepared manifest signature rejected before GitHub access'

    Assert-Rejected { Get-HexorGitHubReleaseConfiguration -GitHubRepository 'OWNER/hexor-updates' -Version '1.0.1' -Trust $trust | Out-Null } 'placeholder GitHub owner rejected'
    Assert-Rejected { Get-HexorGitHubReleaseConfiguration -GitHubRepository $repository -Version '1.0.1' -Trust @{ ManifestUrl = 'https://updates.example.com/latest.json'; PublicKeyPem = $trust.PublicKeyPem } | Out-Null } 'non-GitHub production manifest rejected'
    Assert-Rejected { Get-HexorGitHubReleaseConfiguration -GitHubRepository $repository -Version '1.0.1' -Trust @{ ManifestUrl = "https://raw.githubusercontent.com/$repository/main/latest.json?token=secret"; PublicKeyPem = $trust.PublicKeyPem } | Out-Null } 'credential/query-bearing manifest URL rejected'

    $diagnostic = & $module {
        New-HexorGitHubCliFailure -Arguments @('release', 'create', 'v1.0.1') -ExitCode 1 -Output '{"message":"Validation Failed","status":"422"}' -ErrorText 'gh: Validation Failed; token=ghp_sensitivevalue'
    }
    Assert-True ($diagnostic -match 'GitHub release create' -and $diagnostic -match 'exit code 1' -and $diagnostic -match 'GitHub API HTTP 422: Validation Failed') 'CLI failure identifies operation, exit code, and API error'
    Assert-True ($diagnostic -notmatch 'ghp_sensitivevalue') 'structured API diagnostics omit credential-bearing stderr'
    $redactedDiagnostic = & $module {
        New-HexorGitHubCliFailure -Arguments @('release', 'create', 'v1.0.1') -ExitCode 1 -Output '' -ErrorText 'gh: request failed; token=ghp_sensitivevalue'
    }
    Assert-True ($redactedDiagnostic -notmatch 'ghp_sensitivevalue' -and $redactedDiagnostic -match '\[REDACTED\]') 'fallback CLI diagnostics redact credentials'
    $script:passed += 3
    Write-Output 'PASS: CLI failure diagnostics are actionable and credential-safe'

    Write-Output "$script:passed publishing checks passed. No network requests, uploads, production keys, trust edits, or installs were performed."
    Write-Output "Isolated fixtures retained: $sandbox"
} finally {
    $rsa.Dispose()
    Remove-Module -Name $module.Name -Force -ErrorAction SilentlyContinue
}
