using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Hexor.Compatibility;
using Hexor.Runtime;

namespace Hexor;

public sealed class RegistryToggle : Observable
{
    private readonly IRegistrySettingsService registry;
    private readonly Action<string> report;
    private readonly Func<int?>? read;
    private readonly Action<int>? write;
    private readonly Func<bool>? editable;
    private readonly bool? missingDefault;
    private bool? value;
    private string detail = "";
    public string Section { get; }
    public string Name { get; }
    public string Label { get; }
    public bool IsEditable => editable?.Invoke() ?? true;
    public string Detail => detail;
    public string CustomerDetail => !IsEditable ? "Settings unavailable. Complete automation setup, then use Find settings in Advanced settings."
        : detail.Contains("Unsupported",StringComparison.OrdinalIgnoreCase) || detail.Contains("Unknown",StringComparison.OrdinalIgnoreCase)
            ? "Saved value needs review. Open Diagnostics for details."
            : detail.Contains("unavailable",StringComparison.OrdinalIgnoreCase) ? "Unable to read this setting." : "";
    public bool? Value
    {
        get => value;
        set
        {
            if (value == null || this.value == value || !IsEditable) return;
            try
            {
                if (write != null) write(value.Value ? 1 : 0);
                else registry.WriteVerified(Section, Name, value.Value ? 1 : 0);
                Reload(); report("Saved and verified: " + Label);
            }
            catch (Exception e)
            {
                Reload();
                report(e is PartialSettingsWriteException or ConfigurationConflictException ? e.Message : "Could not save setting (" + e.GetType().Name + ").");
            }
        }
    }
    public RegistryToggle(IRegistrySettingsService registry, string section, string name, string label, Action<string> report,
        bool? missingDefault = null, Func<int?>? read = null, Action<int>? write = null, Func<bool>? editable = null)
    {
        this.registry = registry; Section = section; Name = name; Label = label; this.report = report;
        this.missingDefault = missingDefault; this.read = read; this.write = write; this.editable = editable; Reload();
    }
    public void Reload()
    {
        try
        {
            if (!IsEditable) { value = null; detail = "Choose and load a valid portable configuration first"; }
            else
            {
                var entry = read != null ? new RegistryEntry(read(), true, true) : registry.Inspect(Section, Name);
                value = entry.Value switch { 0 => false, 1 => true, _ => entry.Exists ? null : missingDefault };
                detail = !entry.Supported ? "Unsupported registry type preserved" : read != null ? "Portable JSON value • registry mirror writes occur only on edit or explicit Retry" :
                    !entry.Exists ? "Recommended default shown • missing value remains unwritten" : value == null ? "Unknown value preserved; select explicitly to replace" : "Registry setting • independent write with read-back";
            }
        }
        catch (Exception e) { value = null; detail = "Settings unavailable (" + e.GetType().Name + ")"; }
        Changed(nameof(Value)); Changed(nameof(Detail)); Changed(nameof(IsEditable)); Changed(nameof(CustomerDetail));
    }
}

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly VendorUpdaterManager vendorUpdater;
    private int disposed;

    public string AutomationConnectionStatus => AutomationCapabilities.Connection;
    public string AutomationRunningState => AutomationCapabilities.State;
    public bool CanControlAutomation => false;
    public IJsonConfigurationService Json { get; }
    public IRegistrySettingsService Registry { get; }
    public Preferences Preferences { get; }
    public ObservableCollection<RegistryToggle> Toggles { get; } = [];
    private int auctionPriceMode = -1;
    public int AuctionPriceMode
    {
        get => auctionPriceMode;
        set
        {
            if (value is not (0 or 1) || value == auctionPriceMode) return;
            try { Registry.WriteVerified("ResourceGrab", "AuctionPriceMode", value); Status = "Auction price mode saved and verified."; }
            catch (Exception e) { Status = "Could not save auction price mode (" + e.GetType().Name + ")."; }
            ReloadResourceSettings();
        }
    }
    private int abyssLayer = -1;
    private int preferredDungeon = -1;
    public int AbyssLayer
    {
        get => abyssLayer;
        set { if (value is 0 or 1 && value != abyssLayer) SaveResourceChoice("AbyssLayer", value == 0 ? 21 : 23); }
    }
    public int PreferredDungeon
    {
        get => preferredDungeon;
        set { if (value is 0 or 1 && value != preferredDungeon) SaveResourceChoice("AutoDungeonPreferred", value); }
    }
    private void SaveResourceChoice(string name, int value)
    {
        try { Registry.WriteVerified("ResourceGrab", name, value); Status = "Saved and verified resource preference."; }
        catch (Exception e) { Status = "Could not save resource preference (" + e.GetType().Name + ")."; }
        ReloadResourceSettings();
    }
    private void ReloadResourceSettings()
    {
        try
        {
            auctionPriceMode = Registry.Read("ResourceGrab", "AuctionPriceMode") switch { 0 => 0, 1 => 1, _ => -1 };
            abyssLayer = Registry.Read("ResourceGrab", "AbyssLayer") switch { 21 => 0, 23 => 1, _ => -1 };
            preferredDungeon = Registry.Read("ResourceGrab", "AutoDungeonPreferred") switch { 0 => 0, 1 => 1, _ => -1 };
        }
        catch (Exception e)
        {
            auctionPriceMode = abyssLayer = preferredDungeon = -1;
            Status = "Resource settings unavailable (" + e.GetType().Name + ")";
        }
        Changed(nameof(AuctionPriceMode)); Changed(nameof(AbyssLayer)); Changed(nameof(PreferredDungeon));
    }
    public ObservableCollection<ConfigurationCandidate> Candidates { get; } = [];
    private readonly bool automaticDiscovery;
    private readonly ICharacterDetectionService runtimeDetector;
    private readonly IConfigurationInspector configurationInspector;
    private readonly Func<IReadOnlyList<string>> configurationCandidates;
    private VerifiedSettingsSession? session;
    private ConfigurationSnapshot? machine;
    private ConfigurationInspection? inspection;
    private ConfigurationResolution? resolution;
    private bool refreshingDetection;
    public bool OriginalConfigurationReady => session?.Ready == true && inspection?.Portable.Availability == ConfigurationAvailability.Ready;
    public string ConfigurationStatus
    {
        get
        {
            if (inspection == null) return resolution == null ? "Configuration not checked" : resolution.Candidates.Any(i => i.HasEvidence) ? "Configuration selection required" : "Configuration not found";
            var text = inspection.Summary;
            if (inspection.Portable.Availability == ConfigurationAvailability.Ready && inspection.Machine.Availability != ConfigurationAvailability.Ready)
                text += " • machine.json " + inspection.Machine.Availability.ToString().ToLowerInvariant();
            if (inspection.Portable.Availability == ConfigurationAvailability.Ready && Detection.Processes.Count > 0
                && inspection.Association(Detection).StartsWith("Not associated", StringComparison.Ordinal)) text += " • process not associated";
            return text;
        }
    }
    public string ConfigurationDetails => inspection == null ? resolution?.Note ?? "Select a LegionCommander data root or its config folder."
        : inspection.DataRoot + Environment.NewLine + inspection.FileAvailability + Environment.NewLine + inspection.Association(Detection)
            + (resolution == null ? "" : Environment.NewLine + resolution.Note);
    public string DiagnosticsProcess => Detection.Processes.Count == 0 ? "No AION2 process observed."
        : string.Join(Environment.NewLine, Detection.Processes.Select(p => $"PID {p.Id} • {p.ExecutablePath ?? "Executable path inaccessible"}"));
    public string DiagnosticsDataRoot => inspection?.DataRoot ?? "Not resolved; choose a data/config folder.";
    public string DiagnosticsFiles => inspection?.FileAvailability ?? "No selected root. No files were created.";
    public string DiagnosticsAssociation => inspection?.Association(Detection) ?? "No selected configuration to associate.";
    public string DiagnosticsCandidates => resolution == null ? "Explicit/injected folder selection."
        : resolution.Note + Environment.NewLine + string.Join(Environment.NewLine, resolution.Candidates.Select(i => i.DataRoot + " — " + i.Summary));
    public string DiagnosticsErrors => string.Join(Environment.NewLine, Detection.Errors
        .Concat(inspection?.Errors ?? resolution?.Candidates.SelectMany(c => c.Errors) ?? [])
        .Distinct(StringComparer.Ordinal).DefaultIfEmpty("None reported."));
    public string DiagnosticsObservedAt => Detection.ObservedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "Not observed yet";
    public string DiagnosticsLastIdentityUpdate => Detection.LastSuccessfulIdentityUpdate?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC' (historical; not proof of current identity)") ?? "None in this Hexor session.";
    public string DiagnosticsSettingDetails => string.Join(Environment.NewLine,Toggles.Select(t=>$"{t.Section}/{t.Name}: {t.Detail}"));
    public string OriginalUiCulture { get; set; } = "zh-CN";
    public string GamePathTw { get; set; } = "";
    public string GamePathKr { get; set; } = "";
    public string GamePathGlobal { get; set; } = "";
    public ObservableCollection<ManualCharacterProfile> ManualProfiles { get; } = [];
    public string ManualCharacterName { get; set; } = "";
    public string ManualServer { get; set; } = "";
    public ManualCharacterProfile? SelectedManualProfile
    {
        get => ManualProfiles.FirstOrDefault(p => p.Id == Preferences.SelectedManualProfileId);
        set
        {
            var previous = Preferences.SelectedManualProfileId;
            if (previous == value?.Id) return;
            Preferences.SelectedManualProfileId = value?.Id;
            if (!Save()) Preferences.SelectedManualProfileId = previous;
            Changed(); Changed(nameof(ManualProfileStatus));
        }
    }
    public string ManualProfileStatus => SelectedManualProfile is { } p
        ? $"Manual profile: {p.Character} • {p.Server}. User configured; not detected or linked to a running process."
        : "No manual profile selected. Automatic identity detection awaits an authorized interface.";
    public bool AddManualProfile()
    {
        var name = ManualCharacterName.Trim(); var server = ManualServer.Trim();
        if (name.Length is < 1 or > 100 || server.Length is < 1 or > 100 || name.Any(char.IsControl) || server.Any(char.IsControl))
        { Status = "Enter a character name and server (1–100 characters each, no control characters)."; return false; }
        var profile = new ManualCharacterProfile(Guid.NewGuid().ToString("N"), name, server);
        var previous = Preferences.SelectedManualProfileId;
        Preferences.ManualProfiles.Add(profile); Preferences.SelectedManualProfileId = profile.Id;
        if (!Save()) { Preferences.ManualProfiles.Remove(profile); Preferences.SelectedManualProfileId = previous; return false; }
        ManualProfiles.Add(profile); Changed(nameof(SelectedManualProfile)); Changed(nameof(ManualProfileStatus)); return true;
    }
    public void RemoveManualProfile()
    {
        if (SelectedManualProfile is not { } profile) return;
        var index = Preferences.ManualProfiles.IndexOf(profile);
        Preferences.ManualProfiles.Remove(profile); Preferences.SelectedManualProfileId = null;
        if (!Save()) { Preferences.ManualProfiles.Insert(index,profile); Preferences.SelectedManualProfileId = profile.Id; return; }
        ManualProfiles.Remove(profile); Changed(nameof(SelectedManualProfile)); Changed(nameof(ManualProfileStatus));
    }
    public MachinePathProposal? ProposeDetectedPath(string key)
    {
        if (session == null || machine?.Availability != ConfigurationAvailability.Ready || !LegionConfigurationService.MachineKeys.Contains(key))
        { Status = "Load a valid machine.json and choose a supported path setting first."; return null; }
        var process = Detection.Processes.Count == 1 ? Detection.Processes[0] : null;
        if (process?.ExecutablePath == null || process.StartedAt == null || !Path.IsPathFullyQualified(process.ExecutablePath)
            || !string.Equals(Path.GetFileName(process.ExecutablePath), "AION2.exe", StringComparison.OrdinalIgnoreCase))
        { Status = "A unique running AION2 process with readable executable path and start time is required."; return null; }
        return new(process.Id,process.ExecutablePath,process.StartedAt.Value,key,machine);
    }
    public bool ConfirmDetectedPath(MachinePathProposal proposal)
    {
        try
        {
            var current = ProposeDetectedPath(proposal.Key);
            if (current == null || current.ProcessId != proposal.ProcessId || current.ProcessStartedAt != proposal.ProcessStartedAt
                || !ExecutablePaths.Equal(current.ExecutablePath,proposal.ExecutablePath) || !ReferenceEquals(current.Expected,proposal.Expected))
                throw new InvalidDataException("Process or configuration changed. Review the detected path again.");
            session!.SaveMachinePaths(proposal.Expected,new Dictionary<string,string> { [proposal.Key] = proposal.ExecutablePath });
            RefreshMachineAssociation(); Status = "Confirmed executable path saved and verified. Live identity remains independent."; return true;
        }
        catch (Exception e) { ReportSaveFailure(e); return false; }
    }
    private void RefreshMachineAssociation()
    {
        machine = session!.Machine; inspection = configurationInspector.Inspect(session.Service.Folder);
        GamePathTw = machine.Value("GamePath_TW") ?? ""; GamePathKr = machine.Value("GamePath_KR") ?? ""; GamePathGlobal = machine.Value("GamePath_Global") ?? "";
        foreach (var property in new[] { nameof(GamePathTw),nameof(GamePathKr),nameof(GamePathGlobal),nameof(ConfigurationStatus),nameof(ConfigurationDetails),nameof(DiagnosticsAssociation) }) Changed(property);
    }
    public string WorkflowNotice => session?.Document.NeedsWorkflowMigration == true
        ? "Legacy workflow shown using verified migration rules. Apply explicitly to migrate; disk is unchanged."
        : "Saving preserves ordinary duplicates and IDs, and keeps one enabled Switch Character step last.";
    private string status = "Ready. Reads do not change LegionCommander settings.";
    public string Status { get => status; set { if(Set(ref status, value)) Changed(nameof(CustomerStatus)); } }
    private string updateStatus = "Installation ready.";
    public string UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }
    public string CustomerStatus => status.StartsWith("Hexor settings", StringComparison.Ordinal) || status.StartsWith("Some settings could not", StringComparison.Ordinal) || status.StartsWith("Settings not found", StringComparison.Ordinal) || status.StartsWith("Multiple", StringComparison.Ordinal) || status.StartsWith("No supported settings", StringComparison.Ordinal) ? status
        : status.StartsWith("Found the sole", StringComparison.Ordinal) ? "Settings loaded."
        : status.Contains("partial",StringComparison.OrdinalIgnoreCase) || status.Contains("mirror failed",StringComparison.OrdinalIgnoreCase)
        || status.Contains("not synchronized",StringComparison.OrdinalIgnoreCase) ? "Some settings were saved. Open Diagnostics to review and retry."
        : status.Contains("changed externally",StringComparison.OrdinalIgnoreCase) ? "Settings changed in another application. Reload before saving."
        : status.StartsWith("Loaded",StringComparison.OrdinalIgnoreCase) || status.Contains("saved preferences were not rewritten",StringComparison.OrdinalIgnoreCase) ? "Settings loaded."
        : status.Contains("saved",StringComparison.OrdinalIgnoreCase) && !status.Contains("not saved",StringComparison.OrdinalIgnoreCase) && !status.Contains("Loaded",StringComparison.OrdinalIgnoreCase)
            ? "Settings saved successfully."
        : status.Contains("Unable",StringComparison.OrdinalIgnoreCase) || status.Contains("Could not",StringComparison.OrdinalIgnoreCase)
            || status.Contains("invalid",StringComparison.OrdinalIgnoreCase) || status.Contains("unavailable",StringComparison.OrdinalIgnoreCase)
            || status.Contains("not saved",StringComparison.OrdinalIgnoreCase) ? "Unable to save or load settings. Open Diagnostics for details."
        : status.StartsWith("Gear cap must",StringComparison.OrdinalIgnoreCase) || status.StartsWith("Enter",StringComparison.OrdinalIgnoreCase) ? status
        : status.StartsWith("Gear cap is unchanged",StringComparison.OrdinalIgnoreCase) ? "This setting is already saved."
        : status.StartsWith("Select a configuration",StringComparison.OrdinalIgnoreCase) || status.StartsWith("Load a valid",StringComparison.OrdinalIgnoreCase) ? "Choose and load an existing settings folder first."
        : status.StartsWith("A unique running",StringComparison.OrdinalIgnoreCase) ? "Start one game session to review its location."
        : "Ready. Choose a setting to get started.";
    public string GameStatus => Detection.Processes.Count > 0 ? "Game detected." : Detection.Errors.Count > 0 ? "Unable to check game status." : "Game not running.";
    public string CustomerConfigurationStatus => ConfigurationStatus.Split(" • ")[0];
    private Detection detection = new("Character not detected", "Server not detected", "Not detected", "Checking…", null);
    public Detection Detection { get => detection; private set { if(Set(ref detection, value)) { Changed(nameof(GameStatus)); Changed(nameof(CustomerConfigurationStatus)); } } }
    private Instruction? selectedInstruction;
    public Instruction? SelectedInstruction { get => selectedInstruction; set => Set(ref selectedInstruction, value); }
    public string GatherMin { get; set; }
    public string GatherMax { get; set; }
    public string MaxAccounts { get; set; }
    public string WorkMin { get; set; }
    public string WorkMax { get; set; }
    public string RestMin { get; set; }
    public string RestMax { get; set; }
    private string enhanceCap = "1400";
    public string EnhanceCap { get => enhanceCap; set => Set(ref enhanceCap, value); }
    public string CurrentCap { get; private set; } = "Default 1400 (not written)";
    private string upgradeMaxBattlePowerK = "100";
    public string UpgradeMaxBattlePowerK { get => upgradeMaxBattlePowerK; set => Set(ref upgradeMaxBattlePowerK, value); }
    public string CurrentUpgradeMaxBattlePowerK { get; private set; } = "Default 100K (not written)";
    public string RawClass { get; private set; } = "Unknown";
    public string RawRace { get; private set; } = "Unknown";
    public string RegistryCharacterRace { get; private set; } = "Missing";
    public string RegistryCharacterJob { get; private set; } = "Missing";
    public RelayCommand SaveDailyCommand { get; }
    public RelayCommand SaveScheduleCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand DownCommand { get; }
    private readonly CancellationTokenSource lifetime = new();

    public MainViewModel(IJsonConfigurationService? json = null, IRegistrySettingsService? registry = null, ILegionConfigurationService? configuration = null,
        ICharacterDetectionService? runtimeDetector = null, IConfigurationInspector? configurationInspector = null,
        Func<IReadOnlyList<string>>? configurationCandidates = null, VendorUpdaterManager? vendorUpdater = null)
    {
        automaticDiscovery = json == null && registry == null || configurationCandidates != null;
        this.runtimeDetector = runtimeDetector ?? new CharacterDetectionService(new GameInstallationDiscoveryService());
        this.vendorUpdater = vendorUpdater ?? new VendorUpdaterManager();
        this.configurationInspector = configurationInspector ?? new ConfigurationInspector();
        Json = json ?? new JsonConfigurationService(Path.Combine(Diagnostics.Root, "preferences.json"), new SettingsBackupService());
        Registry = registry ?? new RegistrySettingsService();
        try
        {
            Preferences = Json.Load();
            if (Json is JsonConfigurationService service && service.RecoveredInvalidJson) Status = "Invalid Hexor preferences retained; defaults loaded.";
        }
        catch (Exception e) { Preferences = new(); Status = "Preferences unavailable (" + e.GetType().Name + ")."; }
        this.configurationCandidates = configurationCandidates ?? (() => LegionConfigurationLocator.Candidates(Preferences.ConfigurationFolder));
        Preferences.ManualProfiles ??= [];
        foreach (var profile in Preferences.ManualProfiles) ManualProfiles.Add(profile);
        GatherMin = Preferences.GatherMin.ToString(); GatherMax = Preferences.GatherMax.ToString();
        MaxAccounts = Preferences.MaxAccounts.ToString(); WorkMin = Preferences.WorkMin.ToString(); WorkMax = Preferences.WorkMax.ToString();
        RestMin = Preferences.RestMin.ToString(); RestMax = Preferences.RestMax.ToString();
        foreach (var i in Preferences.Instructions) { i.MinimumText = i.MinimumMinutes.ToString(); i.MaximumText = i.MaximumMinutes.ToString(); }
        Preferences.Instructions.CollectionChanged += (_, _) => RenumberInstructions();
        RenumberInstructions();
        foreach (var item in new[] { ("ClearSideQuestsBeforeDungeon", "Clear side quests before dungeon quests"),
            ("ExploreCavesBeforeDungeon", "Explore caves before dungeon quests"), ("SwitchToGatherAfterTasks", "Switch to gathering after tasks complete") })
        {
            var name = item.Item1;
            Toggles.Add(new(Registry, "AutoTask", name, item.Item2, s => Status = s, false));
        }
        AddToggle("AutoTask", "AutoDailyQuests", "Automatically complete daily quests", false);
        AddToggle("Enhance", "AutoBuyOdEnergy", "Automatically buy Od Energy", false);
        AddToggle("Enhance", "AutoBuyReviveStone", "Automatically buy Resurrection Stones", false);
        AddToggle("Enhance", "AutoDungeonQuickJoinEnabled", "Quick Join existing rooms at 60K+ battle power", false);
        AddToggle("Enhance", "AutoDailyWaveEnabled", "Automatically enter Daily Wave", false);
        AddToggle("Enhance", "AutoNightmareEnabled", "Automatically enter Nightmare", false);
        AddToggle("Enhance", "AutoAwakenEnabled", "Automatically Awaken", false);
        AddToggle("Enhance", "AutoOdMorphEnabled", "Automatically morph Od", false);
        AddToggle("Enhance", "DestroyInsteadOfExtract", "Destroy Instead of Extracting at 100,000 Enhancement Stones", false);
        AddToggle("MiniGame", "AutoEnter", "Automatically Enter Shugo Festival", false);
        AddToggle("MiniGame", "UseTaiwanVillageRules", "Stay AFK in Shugo Festival", false);
        AddToggle("ResourceGrab", "AutoEnterAbyss", "Automatically enter Abyss", null);
        AddToggle("ResourceGrab", "AbyssCounterAttack", "Attack Nearby Players", null);
        AddToggle("ResourceGrab", "GuardianLogoutAvoidance", "Avoid Captcha System by Logging out/in", null);
        AddToggle("ResourceGrab", "AuctionAutoSell", "Automatically sell at auction", null);
        SaveDailyCommand = new(SaveDaily); SaveScheduleCommand = new(SaveSchedule);
        AddCommand = new(() => { var step = new Instruction(); Preferences.Instructions.Add(step); SelectedInstruction = step; });
        DuplicateCommand = new(() => { if (SelectedInstruction is { } i) { var copy = i.Copy(); Preferences.Instructions.Insert(Preferences.Instructions.IndexOf(i) + 1, copy); SelectedInstruction = copy; } }, () => SelectedInstruction != null);
        DeleteCommand = new(() => { if (SelectedInstruction is { } i) { Preferences.Instructions.Remove(i); SelectedInstruction = Preferences.Instructions.FirstOrDefault(); } }, () => SelectedInstruction != null);
        UpCommand = new(() => Move(-1), () => SelectedInstruction != null && Preferences.Instructions.IndexOf(SelectedInstruction) > 0);
        DownCommand = new(() => Move(1), () => SelectedInstruction != null && Preferences.Instructions.IndexOf(SelectedInstruction) < Preferences.Instructions.Count - 1);
        if (configuration != null) Attach(configuration);
        InitializeRegistryDefaults();
        ReloadRegistry();
    }
    private void AddToggle(string section, string name, string label, bool? fallback) => Toggles.Add(new(Registry, section, name, label, s => Status = s, fallback));
    private void RenumberInstructions()
    { for (int i = 0; i < Preferences.Instructions.Count; i++) Preferences.Instructions[i].SequenceNumber = i + 1; }
    private void Move(int offset) { if (SelectedInstruction is { } item) { var index = Preferences.Instructions.IndexOf(item); if (index + offset >= 0 && index + offset < Preferences.Instructions.Count) Preferences.Instructions.Move(index, index + offset); } }

    public void Attach(ILegionConfigurationService configuration)
    {
        resolution = null;
        session = new(configuration, Registry);
        inspection = configurationInspector.Inspect(configuration.Folder);
        LoadAttached();
    }
    public bool ConfirmConfigurationFolder(string folder)
    {
        var candidate = configurationInspector.Inspect(folder);
        if (candidate.Portable.Availability != ConfigurationAvailability.Ready && candidate.Machine.Availability != ConfigurationAvailability.Ready)
        {
            Status = "No supported settings found in this folder. Choose the automation data folder or its config folder after completing initial setup.";
            return false;
        }
        Preferences.ConfigurationFolder = candidate.DataRoot;
        Save();
        Attach(new LegionConfigurationService(candidate.DataRoot));
        return true;
    }
    public void ReloadConfiguration() { if (session == null) { Status = "Select a configuration folder first."; return; } session.Reload(); inspection = configurationInspector.Inspect(session.Service.Folder); LoadAttached(); }
    public bool NeedsRegistryInitialization()
    {
        try { return new RegistryInitializationService(Registry).HasMissingValues(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Status = "Unable to check supported settings. Check registry access and retry Sync Legion Commander Settings in Advanced settings."; return false; }
    }
    public RegistrySynchronizationResult InitializeRegistryDefaults()
    {
        var result = new RegistryInitializationService(Registry).Ensure();
        if(session != null) ReloadConfiguration(); else ReloadRegistry();
        Diagnostics.Log("registry-synchronization", "settings-refresh; success=" + result.Success, result.Success ? DiagnosticSeverity.Information : DiagnosticSeverity.Error);
        Status = result.Message;
        return result;
    }
    private void LoadAttached()
    {
        if (session!.Ready)
        {
            var document = session.Document;
            var schedule = ScheduleSettings.Read(document);
            Preferences.ScheduleEnabled = schedule.Enabled; MaxAccounts = schedule.Accounts.ToString();
            WorkMin = schedule.WorkMin.ToString(); WorkMax = schedule.WorkMax.ToString(); RestMin = schedule.RestMin.ToString(); RestMax = schedule.RestMax.ToString();
            OriginalUiCulture = document.Value("UserLanguage") ?? "zh-CN";
            Preferences.GameLanguage = document.Value("GameLanguage") switch { "en-US" => "English", "ko-KR" => "Korean", _ => "Traditional Chinese" };
            LoadPlan(document.EffectiveWorkflow);
            machine = session.Machine;
            if (machine.Availability == ConfigurationAvailability.Ready)
            { GamePathTw = machine.Value("GamePath_TW") ?? ""; GamePathKr = machine.Value("GamePath_KR") ?? ""; GamePathGlobal = machine.Value("GamePath_Global") ?? ""; }
            Status = "Loaded verified settings without writes. " + (document.NeedsWorkflowMigration ? "Workflow migration is staged, not saved." : "");
        }
        else Status = session.Document.Message;
        ReloadRegistry(); Changed(string.Empty);
    }
    private void LoadPlan(WorkflowPlan plan)
    {
        Preferences.Instructions.Clear();
        foreach (var s in plan.Steps) Preferences.Instructions.Add(new()
        {
            Id = s.Id, Type = FromAction(s.Action), Enabled = s.Enabled, MinimumMinutes = s.MinimumMinutes,
            MaximumMinutes = s.MaximumMinutes, MinimumText = s.MinimumMinutes.ToString(), MaximumText = s.MaximumMinutes.ToString(),
            SmartPatrol = s.SmartPatrol, ResetTimer = s.ResetTimer, CompatibleFields = (System.Text.Json.Nodes.JsonObject)s.Preserved.DeepClone()
        });
        Preferences.AfterFinish = plan.Finish.ToString(); SelectedInstruction = Preferences.Instructions.FirstOrDefault();
    }
    private static InstructionType FromAction(WorkflowAction a) => a switch
    { WorkflowAction.Gather => InstructionType.Gathering, WorkflowAction.FloorGold => InstructionType.KinahFarming, WorkflowAction.AutoTask => InstructionType.AutoQuest, WorkflowAction.KraoDungeon => InstructionType.AutoKraoDungeon, WorkflowAction.SwitchCharacter => InstructionType.SwitchCharacter, _ => throw new InvalidDataException() };
    private static WorkflowAction ToAction(InstructionType a) => a switch
    { InstructionType.Gathering => WorkflowAction.Gather, InstructionType.KinahFarming => WorkflowAction.FloorGold, InstructionType.AutoQuest => WorkflowAction.AutoTask, InstructionType.AutoKraoDungeon => WorkflowAction.KraoDungeon, InstructionType.SwitchCharacter => WorkflowAction.SwitchCharacter, _ => throw new InvalidDataException("Unsupported instruction.") };

    public void ReloadRegistry()
    {
        foreach (var toggle in Toggles) toggle.Reload();
        ReloadResourceSettings();
        Changed(nameof(DiagnosticsSettingDetails));
        try
        {
            string Identity(string name)
            {
                var entry = Registry.Inspect("AutoTask", name);
                return !entry.Exists ? "Missing" : !entry.Supported ? "Unsupported type preserved" : unchecked((uint)entry.Value!.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            RegistryCharacterRace = Identity("CharacterRace");
            RegistryCharacterJob = Identity("CharacterJob");
            RawClass = OriginalConfigurationReady ? session!.Document.Value("AutoTask_Job") ?? Registry.Read("AutoTask", "CharacterJob")?.ToString() ?? "Default 1" : Registry.Read("AutoTask", "CharacterJob")?.ToString() ?? "Missing";
            RawRace = OriginalConfigurationReady ? session!.Document.Value("AutoTask_Race") ?? Registry.Read("AutoTask", "CharacterRace")?.ToString() ?? "Default 2" : Registry.Read("AutoTask", "CharacterRace")?.ToString() ?? "Missing";
            var cap = Registry.Inspect("Enhance", "AutoEnchantMaxPower");
            CurrentCap = cap.Value?.ToString() ?? (cap.Exists ? "Unsupported type preserved" : "Default 1400 (not written)");
            EnhanceCap = Math.Clamp(cap.Value ?? 1400, 1100, 1500).ToString(); Changed(string.Empty);
            var battlePower = Registry.Inspect("Enhance", "AutoUpgradeMaxBattlePowerK");
            CurrentUpgradeMaxBattlePowerK = battlePower.Value?.ToString() ?? (battlePower.Exists ? "Unsupported type preserved" : "Default 100K (not written)");
            UpgradeMaxBattlePowerK = Math.Clamp(battlePower.Value ?? 100, 1, 100).ToString(); Changed(string.Empty);
        }
        catch (Exception e) { Status = "Registry unavailable (" + e.GetType().Name + ")."; }
    }
    public void SaveCap()
    {
        if (!int.TryParse(EnhanceCap, out var cap) || cap < 1100 || cap > 1500) { Status = "Gear cap must be 1100–1500."; return; }
        try
        {
            if (Registry.Read("Enhance", "AutoEnchantMaxPower") == cap) { Status = "Gear cap is unchanged; no registry write was made."; return; }
            Registry.WriteVerified("Enhance", "AutoEnchantMaxPower", cap); CurrentCap = cap.ToString(); Changed(nameof(CurrentCap)); Status = "Gear cap saved and read back successfully.";
        }
        catch (Exception e) { Status = "Cap was not saved (" + e.GetType().Name + ")."; }
    }
    public void SaveUpgradeMaxBattlePowerK()
    {
        if (!int.TryParse(UpgradeMaxBattlePowerK, out var value) || value < 1 || value > 100) { Status = "Maximum battle power must be 1–100K."; return; }
        try
        {
            if (Registry.Read("Enhance", "AutoUpgradeMaxBattlePowerK") == value) { Status = "Maximum battle power is unchanged; no registry write was made."; return; }
            Registry.WriteVerified("Enhance", "AutoUpgradeMaxBattlePowerK", value); CurrentUpgradeMaxBattlePowerK = value.ToString(); Changed(nameof(CurrentUpgradeMaxBattlePowerK)); Status = "Maximum battle power saved and read back successfully.";
        }
        catch (Exception e) { Status = "Maximum battle power was not saved (" + e.GetType().Name + ")."; }
    }
    public bool Save() { try { Json.Save(Preferences); Status = "Saved to Hexor configuration."; return true; } catch (Exception e) { Status = "Could not save preferences (" + e.GetType().Name + ")."; return false; } }
    public void SaveDaily()
    {
        if (session == null)
        {
            if (!Range(GatherMin, GatherMax, out var min, out var max)) { Status = "Gathering duration must be positive, and minimum must not exceed maximum."; return; }
            foreach (var step in Preferences.Instructions)
            {
                if (!Range(step.MinimumText, step.MaximumText, out int stepMin, out int stepMax))
                { Status = "Gathering duration must be numeric and within verified bounds."; return; }
                step.MinimumMinutes = stepMin; step.MaximumMinutes = stepMax;
            }
            Preferences.GatherMin = min; Preferences.GatherMax = max; if (Save()) Status = "Saved Hexor draft only; choose a valid LegionCommander configuration to apply."; return;
        }
        if (!session.Ready) { Status = session.Document.Message; return; }
        try
        {
            var steps = Preferences.Instructions.Select(i =>
            {
                if (!int.TryParse(i.MinimumText, out int min) || !int.TryParse(i.MaximumText, out int max)) throw new InvalidDataException("Step duration must be numeric.");
                return new WorkflowStep { Id = i.Id, Action = ToAction(i.Type), Enabled = i.Enabled, MinimumMinutes = min,
                    MaximumMinutes = max, SmartPatrol = i.SmartPatrol, ResetTimer = i.ResetTimer, Preserved = i.CompatibleFields ?? new() };
            }).ToArray();
            if (!Enum.TryParse<CompletionAction>(Preferences.AfterFinish, out var finish) || !Enum.IsDefined(finish))
                throw new InvalidDataException("Choose GatherOnCharacter or NextAccount.");
            session.SaveWorkflow(new() { Revision = session.Document.StoredWorkflow.Revision, Steps = steps, Finish = finish });
            LoadPlan(session.Document.EffectiveWorkflow); Changed(nameof(WorkflowNotice)); Changed(nameof(ConfigurationStatus));
            Status = "Workflow saved and verified. One enabled Switch Character step is last; no live execution requested.";
        }
        catch (Exception e) { ReportSaveFailure(e); }
    }
    public void SaveSchedule()
    {
        if (!int.TryParse(MaxAccounts, out var accounts) || accounts < 1 || accounts > 16 || !Range(WorkMin, WorkMax, out var min, out var max)
            || !Range(RestMin, RestMax, out var rmin, out var rmax)) { Status = "Enter positive account and duration values within verified bounds; minimum must not exceed maximum."; return; }
        if (session == null)
        {
            Preferences.MaxAccounts = accounts; Preferences.WorkMin = min; Preferences.WorkMax = max; Preferences.RestMin = rmin; Preferences.RestMax = rmax;
            if (Save()) Status = "Saved Hexor schedule draft only; no scheduler notification sent."; return;
        }
        if (!session.Ready) { Status = session.Document.Message; return; }
        try { session.SaveSchedule(new(Preferences.ScheduleEnabled, accounts, min, max, rmin, rmax)); Status = "Work Schedule persisted and verified. LegionCommander was not notified; reload it there before relying on changes."; }
        catch (Exception e) { ReportSaveFailure(e); }
    }
    private static bool Range(string a, string b, out int min, out int max) { max = 0; return int.TryParse(a, out min) && int.TryParse(b, out max) && min >= 1 && max >= min && max <= 10080; }
    private void ReportSaveFailure(Exception e) => Status = e is InvalidDataException or ConfigurationConflictException or PartialSettingsWriteException ? e.Message : "Settings were not saved (" + e.GetType().Name + ").";

    public void RetryAutoTaskMirror() { try { if (session == null) throw new InvalidDataException("Select a configuration first."); Status = session.RetryMirror(); } catch (Exception e) { ReportSaveFailure(e); } }
    public void SaveOriginalUiLanguage() { try { if (session == null) throw new InvalidDataException("Select a configuration first."); session.SaveLanguage("UserLanguage", OriginalUiCulture); Status = "LegionCommander UI preference saved; its running process was not notified."; } catch (Exception e) { ReportSaveFailure(e); } }
    public void SaveMachinePaths()
    {
        try { if (session == null || machine == null) throw new InvalidDataException("Select a configuration first."); session.SaveMachinePaths(machine, new Dictionary<string, string> { ["GamePath_TW"] = GamePathTw.Trim(), ["GamePath_KR"] = GamePathKr.Trim(), ["GamePath_Global"] = GamePathGlobal.Trim() }); RefreshMachineAssociation(); Status = "Machine paths saved and verified; no game was launched."; }
        catch (Exception e) { ReportSaveFailure(e); }
    }
    public bool SetGameLanguage(string language)
    {
        try
        {
            if (session != null) session.SaveLanguage("GameLanguage", language switch { "English" => "en-US", "Korean" => "ko-KR", "Traditional Chinese" => "zh-TW", _ => throw new InvalidDataException("Unsupported language.") });
            Preferences.GameLanguage = language;
            if (session == null) return Save();
            Status = "Game language preference saved and verified; applies to supported launch arguments, not a live language switch."; return true;
        }
        catch (Exception e) { ReportSaveFailure(e); return false; }
    }
    public void Preset(int index)
    {
        var actions = index switch
        {
            1 => new[] { WorkflowAction.FloorGold, WorkflowAction.Gather, WorkflowAction.SwitchCharacter },
            2 => new[] { WorkflowAction.Gather, WorkflowAction.SwitchCharacter },
            3 => new[] { WorkflowAction.AutoTask, WorkflowAction.FloorGold, WorkflowAction.Gather, WorkflowAction.SwitchCharacter },
            _ => new[] { WorkflowAction.AutoTask, WorkflowAction.KraoDungeon, WorkflowAction.FloorGold, WorkflowAction.SwitchCharacter }
        };
        LoadPlan(new() { Steps = actions.Select(a => new WorkflowStep { Action = a, ResetTimer = index == 2 && a == WorkflowAction.Gather }).ToArray() });
        Status = "Verified preset staged. Apply instruction settings to save.";
    }

    public async Task DiscoverAsync()
    {
        if (!automaticDiscovery) return; // Injected/test storage never probes customer configuration.
        try
        {
            var found = await Task.Run(() => new ConfigurationResolver(configurationInspector).Resolve(configurationCandidates(), Preferences.ConfigurationFolder), lifetime.Token);
            resolution = found;
            Candidates.Clear();
            foreach (var item in found.Candidates) Candidates.Add(new(item.DataRoot, item.Summary));
            if (found.Selected != null) { Attach(new LegionConfigurationService(found.Selected.DataRoot)); Status = found.Note; }
            else { session = null; inspection = null; ReloadRegistry(); Status = found.Note; }
            resolution = found;
            Changed(string.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Diagnostics.Log("discovery", e.GetType().Name); Status = "Unable to search for settings. Try Find settings or choose the automation settings folder in Advanced settings."; }
    }
    // The vendor updater is deliberately on-demand. Hexor startup never probes, launches, or interrupts it.
    public Task OpenAionUpdaterAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return Task.CompletedTask;
        Diagnostics.Log("updater-launch", "customer-launch-requested");
        var result = vendorUpdater.DetectOrStart();
        Status = UpdateStatus = result.Message;
        return Task.CompletedTask;
    }

    public async Task RefreshDetectionAsync()
    {
        if (refreshingDetection || lifetime.IsCancellationRequested) return;
        refreshingDetection = true;
        try
        {
            Detection = await runtimeDetector.DetectAsync(lifetime.Token);
            if (Detection.Client.StartsWith("Unknown", StringComparison.Ordinal) && Detection.ExecutablePath != null)
            {
                var paths = session?.Machine;
                if (paths?.Availability == ConfigurationAvailability.Ready)
                {
                    var matches = LegionConfigurationService.MachineKeys
                        .Where(key => ExecutablePaths.Equal(paths.Value(key), Detection.ExecutablePath)).ToArray();
                    if (matches.Length == 1)
                        Detection = Detection with { Client = matches[0] switch
                        {
                            "GamePath_TW" => "Taiwan installation (configured path)",
                            "GamePath_KR" => "Korea installation (configured path)",
                            _ => "Global installation (configured path)"
                        } };
                }
            }
            bool wasReady = OriginalConfigurationReady;
            var observedSession = session;
            if (observedSession != null)
            {
                var observedFiles = await Task.Run(() => configurationInspector.Inspect(observedSession.Service.Folder), lifetime.Token);
                if (ReferenceEquals(observedSession, session)) inspection = observedFiles;
            }
            if (wasReady != OriginalConfigurationReady) foreach (var toggle in Toggles.Where(t => t.Section == "AutoTask")) toggle.Reload();
            // Do not reload the editing snapshot or discard workflow drafts during background polling.
            Changed(nameof(ConfigurationStatus)); Changed(nameof(CustomerConfigurationStatus)); Changed(nameof(ConfigurationDetails)); Changed(nameof(OriginalConfigurationReady));
            Changed(nameof(DiagnosticsProcess)); Changed(nameof(DiagnosticsDataRoot)); Changed(nameof(DiagnosticsFiles));
            Changed(nameof(DiagnosticsAssociation)); Changed(nameof(DiagnosticsCandidates)); Changed(nameof(DiagnosticsErrors)); Changed(nameof(DiagnosticsObservedAt)); Changed(nameof(DiagnosticsLastIdentityUpdate));
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Detection = new("Character not detected", "Server not detected", "Unknown", "Detection unavailable", null)
            { Errors = ["Runtime refresh failed (" + e.GetType().Name + ")."] };
            Changed(nameof(DiagnosticsErrors)); Changed(nameof(DiagnosticsLastIdentityUpdate)); Diagnostics.Log("detection", e.GetType().Name);
        }
        finally { refreshingDetection = false; }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

