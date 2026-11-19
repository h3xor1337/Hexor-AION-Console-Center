# HEXOR AION CONTROL CENTER

Independent .NET 10 WPF Windows settings panel with a purple, black and white theme.

After a Debug build, launch `bin/Debug/net10.0-windows/win-x64/Hexor-AION-Control-Center.exe` to review the current development version.

The centered window has seven tabs: General, Auto Task, Character Enhancement, Shugo Festival, Daily Activities, Work Schedule, and Live Control. Longer sections scroll within the window. General offers language preferences, existing settings-folder selection, confirmed game-location saves and optional manual labels. Technical details and storage synchronization retry are in collapsed Diagnostics.

Auto Task exposes four independent behavior toggles, including AutoDailyQuests, plus the existing resource and auction settings. Gathering adds Uruguru Cave, quality filtering, Ignore Aether, and a scrollable resource editor that displays English names while preserving original identifiers and selections. Enhancement retains both power caps and now exposes fifteen toggles, including bound Kinah, coin shops and Genus Insight. Every registry write is mirrored into LegionCommander's shared settings documents (the store the running game reads), and Auto Task's dungeon settings use the installed LegionCommander format: a priority list (Uruguru/Krao/Draupnir Conquest/Draupnir Easy with switches and ordering) plus the Easy/Conquest mode. Shugo retains two independent settings. Character race and class defaults use the mappings verified in the installed LegionCommander settings dialog. Gathering quality offers White, Green, and Blue. An existing custom DWORD is shown and preserved until a named quality is applied. Saves use the existing verified JSON/registry adapters, backups, read-back and conflict protection. Saving confirms stored preferences, not application to running automation. See [the verified settings mapping](HEXOR_VERIFIED_SETTINGS_MAPPING.md) and [the integration report](HEXOR_INTEGRATION_REPORT.md).

Daily Activities edits the saved activity sequence, per-activity settings, and completion action. It can load the default sequence, import the workflow from a LegionCommander settings export, and export the current draft as workflow JSON. Work Schedule edits concurrency and work/rest ranges. Apply explicitly to save through the existing configuration adapters. Reload the saved settings in LegionCommander before relying on them.

The General status tile surfaces provider CAPTCHA activity from read-only signals (the provider's own `%TEMP%\Aion2Captcha` challenge artifacts and the newest `%APPDATA%\LegionCommander\Logs\debug_*.log` line tail). Hexor never reads challenge images or answers, never touches private IPC/injection channels, and never solves, submits, or bypasses a CAPTCHA. An optional preference pauses Hexor's own workflow/schedule apply actions while a challenge signal is active; it never pauses or controls the running automation.

Support logs (`%LOCALAPPDATA%\Hexor\ControlCenter\Logs\hexor.log`) are plain, readable lines — `2026-11-19 23:30:01.123 +08:00 [INFO] [sessionId] [pid 1234] component: Key=Value, …` — with structured JSON details flattened to `Key=Value` text and secret-like values redacted in place. The line format is stable for future debugging sessions: grep by component, severity (`INFO`/`WARN`/`ERROR`), or session id.

Live Control reads session state and record history from LegionCommander's local runtime bridge (`Aion2_RuntimeMcp_Commander_v1`, protocol version 1) while the installed LegionCommander is running. It lists active sessions, shows open automation incidents with claim-and-resolve, reads daily record summaries for a game day (UTC+8, YYYY-MM-DD), and lists the bridge's own CAPTCHA operation history. Operators can pause, continue, or explicitly confirm-stopping a session's daily workflow (`daily.control`); Hexor never starts automation, never adds or edits accounts, never touches game memory, and never reads or writes challenge content. Proxy management has no supported integration point (proxy lines live only in the vendor's private accounts database and the bridge exposes no proxy methods); account management and batch login are excluded by design.

The existing application icon is retained; branding assets and their status are documented in [Assets/README.md](Assets/README.md).

The source is prepared for 1.0.14; published 1.0.13 assets and the public OTA manifest are unchanged. Customer release packaging/publication remains separate from feature development.

Build and focused tests:

```powershell
dotnet build tests/Hexor.Tests.csproj -c Debug
./tests/bin/Debug/net10.0-windows/win-x64/Hexor.Tests.exe --lc-registry-only
./tests/bin/Debug/net10.0-windows/win-x64/Hexor.Tests.exe --registry-only
./tests/bin/Debug/net10.0-windows/win-x64/Hexor.Tests.exe --compatibility-only
./tests/bin/Debug/net10.0-windows/win-x64/Hexor.Tests.exe --wpf-only
```

The published Windows x64 single-file build is self-contained and unsigned. Preferences: `%LOCALAPPDATA%\HexorAion\preferences.json`. Privacy-safe diagnostics: `%LOCALAPPDATA%\HexorAion\diagnostics.log`. Discovery/read operations do not modify original provider settings; first-run Hexor preferences are separate.

Missing/corrupt original configuration is not fabricated or overwritten. Select existing settings or restore a known-good backup through the provider's supported recovery procedure. Never edit the same original settings simultaneously in both applications.

Read HEXOR_INTEGRATION_REPORT.md for the latest scope, validation, branding dependency and integration limits. Existing manual engine tests remain unexecuted. Hexor is a settings editor with explicit, operator-initiated session controls; it never starts automation or manages accounts.

Current redesign: manual-profile controls are removed (stored profile data is preserved). The supported dark custom title bar provides minimize/close and native caption dragging. General's advanced configuration tools are collapsed. Branding resource integration is prepared, but the exact supplied Assets/Hexor.ico and Assets/Hex Logo-Transparent.png are absent; the current review artifact is not a completed official-branding build. See Assets/README.md and the latest integration-report section.

Automatic Hexor updates use a public GitHub release-only repository and a signed manifest on raw GitHub content hosting. Customer release publication remains separate from feature development; local preparation does not upload unless `-PublishGitHub` is explicitly supplied.
