# Legion Commander registry integration review

Verified 2026-11-16 (Asia/Shanghai). Scope follows the owner's confirmation to implement the supplied export.

## Reference and coverage

The supplied `LC New.reg` was read as UTF-16 Windows Registry Editor 5.00 data. It contains **28 values**, not 40. No registry export was imported or copied into source control.

| Section | Values in the export | Console behavior |
| --- | --- | --- |
| AutoTask | CharacterRace, CharacterJob | Read-only unsigned DWORD display; unknown values and unsupported types preserved. No enum labels invented. |
| AutoTask | ClearSideQuestsBeforeDungeon, ExploreCavesBeforeDungeon, SwitchToGatherAfterTasks | Existing independent toggles retained. |
| AutoTask | AutoDailyQuests | Added working independent toggle using exact DWORD 0/1 and read-back verification. Missing value displays OFF without creating a default; saved values are preserved at startup. |
| Enhance | AutoEnchantMaxPower, AutoUpgradeMaxBattlePowerK | Existing independent numeric controls retained; initialization defaults remain 1500 and 100. |
| Enhance | AutoBuyOdEnergy, AutoBuyReviveStone, AutoDungeonQuickJoinEnabled, AutoDailyWaveEnabled, AutoNightmareEnabled, AutoAwakenEnabled, AutoOdMorphEnabled, DestroyInsteadOfExtract | All eight existing toggles retained. |
| MiniGame | AutoEnter, UseTaiwanVillageRules | Both existing independent Hexor toggles retained. |
| ResourceGrab | AutoEnterAbyss, AbyssLayer, AbyssCounterAttack, GuardianLogoutAvoidance, AuctionAutoSell, AuctionPriceMode, AutoDungeonPreferred | Existing controls and exact encodings retained. |
| Nightmare | Two ID records and one empty `.run` value | Runtime data: no controls, reads, modifications, or resets added. Exact purpose is not established by the export. Customer identifiers are omitted from this document. |

Total: 23 editable settings, two unresolved character identity DWORDs, three runtime values.

The export does **not** contain EnchantBoundKinahOnly, CoinShopNightmareDaevanion, CoinShopNightmareStigma, CoinShopFestivalDaevanion, CoinShopFestivalStigma, CoinShopAbyssStigma, GenusInsightAutoAnalyze, AutoDungeonUrugugu, GatherTargetNames, GatherTargets, GatherQualityFilterEnabled, GatherQualityFilterLevel, or GatherIgnoreAether. Those requested additions and the 24-resource parser/serializer remain unimplemented pending the export containing their identifiers and serialization format. No gathering targets or enum mappings were invented.

## Preservation and rollback

- Missing AutoEnterAbyss remains OFF; AbyssLayer remains Lower=21 / Middle=23.
- AuctionAutoSell still resets OFF and AuctionPriceMode to Average=1 on each startup, as previously approved.
- AutoDungeonPreferred remains Krao=0 / Draupnir=1.
- Unrelated values, unknown character numbers, malformed types, and Unicode multiline strings survive daily-quest edits.
- Daily-quest edits only change AutoTask/AutoDailyQuests. They do not patch portable JSON or claim live automation refresh.
- Schema version and initializer were left unchanged, so the new toggle introduces no startup writes.
- No changes to vendor files, protected baselines, OTA manifest, public RSA-PSS trust, customer configurations, or release version. No customer build or publication was performed.
- Source rollback: remove the new toggle and identity panel, restore the service guard for AutoDailyQuests, and revert their tests. Existing saved registry selections remain in place. Do not reset customer registry keys for rollback.

## Verification actually performed

`dotnet build tests/Hexor.Tests.csproj -c Debug --no-restore`: application and test projects passed, zero warnings/errors.

Run the test **executable**, not `dotnet Hexor.Tests.dll`: existing subprocess tests use Environment.ProcessPath to relaunch themselves.

| Command argument to tests/bin/Debug/net10.0-windows/win-x64/Hexor.Tests.exe | Result |
| --- | --- |
| --daily-registry-only | 10 passed: missing/unknown values, exact 0/1 writes, Unicode preservation, malformed types, unknown unsigned identity display, blocked enum writes |
| --registry-only | 72 passed: existing defaults, startup policies, preservation, concurrency, isolated registry access |
| --compatibility-only | 74 passed: persistence, backups, conflicts, portable JSON preservation, independent registry edits |
| --wpf-only | 83 passed: real toggle binding writes, all four pages, unchanged settings documents, scrolling and scaled rendering |

All registry writes in these checks use unique Software/HexorTests fixtures, never the live customer key. WPF captures were generated under ignored .build/lc-registry-validation; Auto Task was visually inspected at 100% and 150%. The full regression/release gate was not run. The first compatibility invocation exposed missing fixture setup in the newly added focused entry point; that setup was corrected and the suite passed. No unrelated production fix was made.

Gathering count / round-trip result: unavailable because there are zero gathering lists in this export; no 24-resource round-trip result is claimed.

## Source-control preparation and blockers

The supplied workspace had no .git directory or parent repository, so existing local branches/history, unpublished v1.0.14 changes, and historical diffs could not be recovered or verified. No existing source files were discarded. A new local repository was initialized on feature/lc-registry-update with origin pointing to the requested source repository.

The GitHub page was inspected read-only and remained publicly readable and empty. No remote write was made. The owner subsequently authorized the source push and public update release on 2026-11-16. That supersedes the earlier publication restriction; release integrity checks still apply.

The expanded .gitignore excludes build/release payloads, vendor reference binaries/decompiled material, customer registry/configuration/database data, WebView profiles, snapshots, historical validation evidence, logs, and signing secrets. Only this new review document is included from docs; older local reports remain on disk and are excluded from the proposed import. Public Updates/trust.json is required by the application and remains unchanged.

A pre-staging candidate scan for private keys, GitHub/provider tokens, credential URLs and credential assignments found one intentional name:secret example in DownloadTests.cs used to test URL rejection; no actual credential was found by that scan. This is a bounded source review, not a guarantee that an initial public import is safe. The owner has explicitly authorized initial source publication. The initial-import whitespace check reports pre-existing extra blank lines at EOF in MainWindow.cs, Services.cs, ViewModels.cs, tests/CustomerDiscoveryTests.cs, and tests/Program.cs; these are preserved rather than rewriting unrelated source.

No Git author identity was preconfigured. The authenticated GitHub account was verified as h3xor1337 (account ID 341697001), with administrative access to the source repository. Source commits use that verified account name and its GitHub noreply address; no private email is exposed and no global Git identity is changed. The owner now authorizes source publication and a public application update. v1.0.13 remains the current customer release until the next candidate passes all required checks. A fresh 2026-11-16 read-only preflight still reports 855/933 matches, 74 hash mismatches and four missing files. The four installed vendor binaries also have different hashes from the 2026-11-05 investigation. Baselines and protected files are unchanged. Detailed path/hash evidence remains local and excluded from source control.

Readiness: the supplied 28-value export is covered within the identity/runtime restrictions, and the changes pass the focused checks above. The full original 40-value/24-resource objective remains incomplete until the matching export is supplied. The initial source import is authorized. Customer release preparation/publication remains blocked pending resolution of the protected-integrity discrepancies; a general release request does not establish the provenance of changed vendor binaries or approve baseline amendments.
