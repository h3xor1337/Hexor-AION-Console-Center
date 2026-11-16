using System.Reflection;
using System.IO;

namespace Hexor;

public enum RegistrySettingType { BooleanDWord, IntegerDWord }
public enum RegistryOwnership { HexorSupported, VendorManaged }

public sealed record RegistryDefinition(string Section, string Name, RegistrySettingType Type, int InitializationDefault,
    int Minimum, int Maximum, string UiBinding, int MigrationVersion, RegistryOwnership Ownership, bool ResetOnStartup = false)
{
    public bool IsValid(int value) => Section == "ResourceGrab" && Name == "AbyssLayer" ? value is 21 or 23 : value >= Minimum && value <= Maximum;
}

public sealed record RegistrySynchronizationResult(int SettingsChecked, int MissingSettingsInitialized,
    int ExistingSettingsPreserved, int InvalidValuesDetected, int ErrorsEncountered, int PreviousSchemaVersion,
    int SchemaVersion, IReadOnlyList<string> Issues)
{
    public int StartupDefaultsRestored { get; init; }
    public bool Success => ErrorsEncountered == 0;
    public string Message => $"Legion Commander settings synchronized. Checked: {SettingsChecked}; initialized: {MissingSettingsInitialized}; startup defaults restored: {StartupDefaultsRestored}; preserved: {ExistingSettingsPreserved}; invalid: {InvalidValuesDetected}; errors: {ErrorsEncountered}.";
}

// Reference exports establish names and types only; every newly introduced Boolean initializes OFF.
public static class LegionCommanderRegistrySchema
{
    public const int Version = 3;
    public static IReadOnlyList<RegistryDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        Bool("AutoTask", "ClearSideQuestsBeforeDungeon", "Auto Task / Clear side quests"),
        Bool("AutoTask", "ExploreCavesBeforeDungeon", "Auto Task / Explore caves"),
        Bool("AutoTask", "SwitchToGatherAfterTasks", "Auto Task / Switch to gathering"),
        Number("Enhance", "AutoEnchantMaxPower", 1500, 1100, 1500, "Character Enhancement / Gear enhancement level"),
        Bool("Enhance", "AutoBuyOdEnergy", "Membership Benefits / Buy Od Energy"),
        Bool("Enhance", "AutoBuyReviveStone", "Membership Benefits / Buy Resurrection Stones"),
        Bool("Enhance", "AutoDungeonQuickJoinEnabled", "Auto Dungeon / Quick Join"),
        Bool("Enhance", "AutoDailyWaveEnabled", "Auto Dungeon / Daily Wave"),
        Bool("Enhance", "AutoNightmareEnabled", "Auto Dungeon / Nightmare"),
        Bool("Enhance", "AutoAwakenEnabled", "Auto Dungeon / Awaken"),
        Bool("Enhance", "AutoOdMorphEnabled", "Auto Dungeon / Od Morph"),
        Number("Enhance", "AutoUpgradeMaxBattlePowerK", 100, 1, 100, "Character Enhancement / Maximum battle power"),
        Bool("Enhance", "DestroyInsteadOfExtract", "Equipment Extraction / Destroy instead of extracting"),
        Bool("MiniGame", "AutoEnter", "Mini Games / Auto Enter"),
        Bool("MiniGame", "UseTaiwanVillageRules", "Mini Games / Taiwan village rules"),
        Bool("ResourceGrab", "AutoEnterAbyss", "Resource Gathering / Enter Abyss"),
        Number("ResourceGrab", "AbyssLayer", 21, 21, 23, "Resource Gathering / Abyss layer"),
        Bool("ResourceGrab", "AbyssCounterAttack", "Resource Gathering / Counterattack"),
        Bool("ResourceGrab", "GuardianLogoutAvoidance", "Resource Gathering / Guardian logout avoidance"),
        Bool("ResourceGrab", "AuctionAutoSell", "Auto Auction / Auto sell") with { ResetOnStartup = true },
        Number("ResourceGrab", "AuctionPriceMode", 1, 0, 1, "Auto Auction / Price mode") with { ResetOnStartup = true },
        Number("ResourceGrab", "AutoDungeonPreferred", 0, 0, 1, "Auto Dungeon / Preferred dungeon")
    });
    private static RegistryDefinition Bool(string section, string name, string binding, int value = 0) => new(section, name, RegistrySettingType.BooleanDWord, value, 0, 1, binding, Version, RegistryOwnership.HexorSupported);
    private static RegistryDefinition Number(string section, string name, int value, int minimum, int maximum, string binding) => new(section, name, RegistrySettingType.IntegerDWord, value, minimum, maximum, binding, Version, RegistryOwnership.HexorSupported);
    public static RegistryDefinition? Find(string section, string name) => Definitions.FirstOrDefault(x => x.Section == section && x.Name == name);
}

// Compatibility facade retained for existing callers and publishing-contract tests.
public static class RegistryDefaults
{
    public static IReadOnlyList<RegistryDefinition> Template => LegionCommanderRegistrySchema.Definitions;
    public static bool IsValid(string section, string name, int value) => LegionCommanderRegistrySchema.Find(section, name)?.IsValid(value) == true;
}

public sealed class RegistryInitializationService(IRegistrySettingsService registry)
{
    private const string MutexName = "Local\\Hexor.LegionCommander.RegistrySynchronization";
    public bool HasMissingValues() => RegistryDefaults.Template.Any(t => !registry.Inspect(t.Section, t.Name).Exists);

    public RegistrySynchronizationResult Ensure()
    {
        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        Diagnostics.Log("registry-synchronization", $"started; application-version={appVersion}; schema-version={LegionCommanderRegistrySchema.Version}");
        var issues = new List<string>(); var created = 0; var preserved = 0; var invalid = 0; var errors = 0; var previousVersion = 0; var restored = 0;
        using var mutex = new Mutex(false, MutexName); var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { issues.Add("Synchronization lock timed out; no registry values were changed."); errors++; return Result(); }
            previousVersion = registry.ReadAppliedSchemaVersion();
            foreach (var item in RegistryDefaults.Template)
            {
                var label = item.Section + "." + item.Name;
                try
                {
                    var entry = registry.Inspect(item.Section, item.Name);
                    if (entry.Exists)
                    {
                        if (item.ResetOnStartup && (!entry.Supported || entry.Value != item.InitializationDefault))
                        {
                            registry.RestoreStartupDefault(item.Section, item.Name);
                            var verifiedDefault = registry.Inspect(item.Section, item.Name);
                            if (!verifiedDefault.Exists || !verifiedDefault.Supported || verifiedDefault.Value != item.InitializationDefault)
                                throw new IOException("Startup default read-back verification failed.");
                            restored++;
                            Diagnostics.Log("registry-synchronization", "startup-default-restored; setting=" + label);
                            continue;
                        }
                        preserved++;
                        if (!entry.Supported || entry.Value == null || !item.IsValid(entry.Value.Value))
                        { invalid++; issues.Add(label + ": invalid type or value preserved for customer review."); Diagnostics.Log("registry-synchronization", "invalid-value-preserved; setting=" + label, DiagnosticSeverity.Warning); }
                        continue;
                    }
                    if (registry.CreateMissingDefault(item.Section, item.Name, item.InitializationDefault))
                    { created++; Diagnostics.Log("registry-synchronization", "missing-value-created; setting=" + label + "; default=" + item.InitializationDefault); }
                    var verified = registry.Inspect(item.Section, item.Name);
                    if (!verified.Exists || !verified.Supported || verified.Value != item.InitializationDefault) throw new IOException("Registry read-back verification failed.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or NotSupportedException)
                { errors++; issues.Add(label + ": synchronization failed (" + e.GetType().Name + ")."); Diagnostics.Log("registry-synchronization", "failure; setting=" + label + "; type=" + e.GetType().Name, DiagnosticSeverity.Error); }
            }
            if (errors == 0)
            {
                try { registry.WriteAppliedSchemaVersion(LegionCommanderRegistrySchema.Version); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
                { errors++; issues.Add("Schema version could not be recorded (" + e.GetType().Name + ")."); }
            }
            return Result();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
            Diagnostics.Log("registry-synchronization", $"completed; checked={RegistryDefaults.Template.Count}; created={created}; restored={restored}; preserved={preserved}; invalid={invalid}; errors={errors}; migration={previousVersion}->{LegionCommanderRegistrySchema.Version}", errors > 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Information);
        }
        RegistrySynchronizationResult Result() => new(RegistryDefaults.Template.Count, created, preserved, invalid, errors, previousVersion, LegionCommanderRegistrySchema.Version, issues) { StartupDefaultsRestored = restored };
    }
}
