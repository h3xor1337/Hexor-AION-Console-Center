# HEXOR AION CONTROL CENTER

Independent .NET 10 WPF Windows settings panel with a purple, black and white theme.

Launch `output/customer-review/Hexor-AION-Control-Center.exe`.

The fixed, centered window has exactly four tabs: General, Auto Task, Character Enhancement, Shugo Festival. Longer sections scroll within the window. General offers language preferences, existing settings-folder selection, confirmed game-location saves and optional manual labels. Technical details and storage synchronization retry are in collapsed Diagnostics.

Auto Task exposes four independent behavior toggles, including AutoDailyQuests, plus the existing resource and auction settings. Character race/job DWORDs are shown read-only until their enum mappings are verified. Enhancement retains both power caps and eight toggles. Shugo retains two independent settings. Saves use the existing verified JSON/registry adapters, backups, read-back and conflict protection. Saving confirms stored preferences, not application to running automation. See [the registry coverage and verification report](docs/LC_REGISTRY_UPDATE.md).

Daily Activities, Work Schedule, race/job selectors and remote controls remain absent from the customer interface. Existing stored workflow/schedule data and working persistence services remain intact; navigation does not delete customer data. No game files, private IPC, licensing or authentication are modified.

Official logo/icon assets were not supplied in the project. Text branding and the existing application icon remain; see docs/BRANDING_ASSETS_REQUIRED.md. No substitute official branding was invented.

Build and test:

```powershell
dotnet build Hexor.ControlCenter.csproj -c Release
dotnet run --project tests/Hexor.Tests.csproj -c Release
dotnet publish Hexor.ControlCenter.csproj -c Release -o output/customer-review
```

The published Windows x64 single-file build is self-contained and unsigned. Preferences: `%LOCALAPPDATA%\HexorAion\preferences.json`. Privacy-safe diagnostics: `%LOCALAPPDATA%\HexorAion\diagnostics.log`. Discovery/read operations do not modify original provider settings; first-run Hexor preferences are separate.

Missing/corrupt original configuration is not fabricated or overwritten. Select existing settings or restore a known-good backup through the provider's supported recovery procedure. Never edit the same original settings simultaneously in both applications.

Read HEXOR_INTEGRATION_REPORT.md for the latest scope, validation, branding dependency and integration limits. Existing manual engine tests remain unexecuted. Hexor is a settings editor; it does not promise live automation control or settings refresh.

Current redesign: manual-profile controls are removed (stored profile data is preserved). The supported dark custom title bar provides minimize/close and native caption dragging. General's advanced configuration tools are collapsed. Branding resource integration is prepared, but the exact supplied Assets/Hexor.ico and Assets/Hex Logo-Transparent.png are absent; the current review artifact is not a completed official-branding build. See Assets/README.md and the latest integration-report section.

Automatic Hexor updates use a public GitHub release-only repository and a signed manifest on raw GitHub content hosting. See [the architecture, signing, publishing commands and test results](docs/HEXOR_AUTO_UPDATE_SYSTEM.md). Production repository/signing approval and trust provisioning are required before customer distribution; local preparation never uploads unless `-PublishGitHub` is explicitly supplied.
