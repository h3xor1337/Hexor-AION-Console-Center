# Setting up your own HEXOR update channel

This guide covers taking a copy of the HEXOR Control Center source, generating your own
signing keys, pointing the console at your own GitHub repository, and publishing releases.

## 1. What to expect in the source folder

Everything needed is in the folder. Exclusions when zipping/sharing:
`.git/`, `bin/`, `obj/`, `output/`, `.build/`, `.vs/` (local build junk; signing keys live
outside the tree by design).

## 2. Prerequisites

- Windows with the .NET 10 SDK and PowerShell 7 (pwsh)
- A GitHub account and a new empty public repository for updates,
  for example `yourname/hexor-updates`, with a `main` branch

## 3. Generate your own signing keys and switch the update channel

Create the key directory first (a normal local folder; the script enforces access rules).
Then, from the source folder:

    pwsh scripts/Initialize-HexorSigning.ps1 -PrivateKeyPath D:\HexorSigning\release-private.pem -GitHubRepository yourname/hexor-updates

This writes your private key to the given path and rewrites `Updates\trust.json` with your
manifest URL (https://raw.githubusercontent.com/yourname/hexor-updates/main/latest.json)
and your public key. From this point the console checks your repository — not the original
publisher's — and nothing you publish can be confused with another publisher's releases.

## 4. Build and run the test gates

    dotnet build Hexor.ControlCenter.csproj -c Release
    dotnet run --project UpdateTests/UpdateTests.csproj -c Release

Both must pass before publishing.

## 5. Prepare and publish a release

    pwsh scripts/Publish-HexorRelease.ps1 -Version 1.0.31 -PrivateKeyPath D:\HexorSigning\release-private.pem -ReleaseNotesPath ReleaseNotes-1.0.31.md
    pwsh scripts/Publish-HexorGitHub.ps1 -Version 1.0.31 -GitHubRepository yourname/hexor-updates -PublishGitHub

The first command builds, signs and stages everything under `output/releases/<version>/`.
The second uploads it to your repository, re-downloads and verifies the public package, and
updates `latest.json`. Consoles built with your trust file check, download and install from
your repository automatically — at launch and every 30 minutes while open.

## 6. Rules

- Never share the private key. If it is lost, already-installed consoles will not accept new
  signatures without a manual reinstall.
- `Updates\trust.json` is embedded into the console at build time. After changing it, rebuild
  before publishing.
- Release versions must always increase; a lower version is refused.
- Before pushing an update, verify it installs on a real machine. The console restarts itself
  after installing; failed installs retain recovery backups for automatic recovery.
