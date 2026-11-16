#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [Parameter(Mandatory)][string]$GitHubRepository,
    [string]$ReleaseDirectory,
    [Parameter(Mandatory)][switch]$PublishGitHub
)
$ErrorActionPreference = 'Stop'
if (-not $PublishGitHub) { throw 'Publishing requires the explicit -PublishGitHub switch after repository and signing configuration are approved.' }
$root = Split-Path -Parent $PSScriptRoot
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $root "output\releases\$Version" }
Import-Module (Join-Path $PSScriptRoot 'HexorGitHubRelease.psm1') -Force
Publish-HexorGitHubRelease -GitHubRepository $GitHubRepository -Version $Version -ReleaseDirectory $ReleaseDirectory -Root $root
