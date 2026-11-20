using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hexor.Compatibility;
using Hexor.Runtime;

namespace Hexor;

public sealed class DungeonPriorityRow : Observable
{
    private bool enabled;
    public DungeonPriorityRow(MainViewModel model, int mapId, string label, bool initialEnabled)
    {
        Model = model; MapId = mapId; Label = label; enabled = initialEnabled;
        MoveUp = new RelayCommand(() => model.MoveDungeonPriority(MapId, -1), () => model.CanMoveDungeonPriority(MapId, -1));
        MoveDown = new RelayCommand(() => model.MoveDungeonPriority(MapId, 1), () => model.CanMoveDungeonPriority(MapId, 1));
    }
    private MainViewModel Model { get; }
    public int MapId { get; }
    public string Label { get; }
    public int Ordinal { get; internal set; }
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (value == enabled) return;
            enabled = value;
            Changed(nameof(Enabled));
            Model.SaveDungeonPriority();
        }
    }
    public System.Windows.Input.ICommand MoveUp { get; }
    public System.Windows.Input.ICommand MoveDown { get; }
}

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
    private int dungeonMode = -1;
    public int AbyssLayer
    {
        get => abyssLayer;
        set { if (value is 0 or 1 && value != abyssLayer) SaveResourceChoice("AbyssLayer", value == 0 ? 21 : 23); }
    }
    public int DungeonMode
    {
        get => dungeonMode;
        set { if (value is 0 or 1 && value != dungeonMode) SaveResourceChoice("AutoDungeonMode", value); }
    }
    public ObservableCollection<DungeonPriorityRow> DungeonPriority { get; } = [];
    public void ReloadDungeonPriority()
    {
        var previous = DungeonPriority.ToDictionary(r => r.MapId, r => r.Enabled);
        DungeonPriority.Clear();
        DungeonRegistrySnapshot snapshot;
        try { snapshot = Registry.ReadDungeon(); }
        catch (Exception e) { Status = "Dungeon settings unavailable (" + e.GetType().Name + ")."; return; }
        var parsed = snapshot.Priority.Value is string text && snapshot.Priority.Supported ? DungeonCatalog.Parse(text)
            : snapshot.Mode.Value is int mode && snapshot.Mode.Exists && mode is 0 or 1 ? Registry.DungeonDefaults().ToList()
            : [];
        if (snapshot.Priority.Value is null && parsed.Count > 0)
        {
            // Vendor default seed: the Uruguru Cave entry follows the saved legacyUrugugu toggle.
            var urugugu = snapshot.LegacyUrugugu.Value == 1;
            parsed = parsed.Select(e => e.MapId == 600012 ? (e.MapId, urugugu) : e).ToList();
        }
        foreach (var (mapId, enabled) in parsed)
        {
            var row = new DungeonPriorityRow(this, mapId, DungeonCatalog.Label(mapId),
                previous.TryGetValue(mapId, out var priorEnabled) ? priorEnabled : enabled);
            row.Ordinal = DungeonPriority.Count + 1;
            DungeonPriority.Add(row);
        }
        dungeonMode = snapshot.Mode.Value is int value && snapshot.Mode.Supported && value is 0 or 1 ? value : -1;
        Changed(nameof(DungeonMode));
    }

    public void ResetDungeonPriority()
    {
        try
        {
            var urugugu = Registry.Read("ResourceGrab", "AutoDungeonUrugugu") == 1;
            Registry.WriteDungeonPriorityVerified([.. Registry.DungeonDefaults().Select(entry => entry.MapId == 600012 ? (entry.MapId, urugugu) : entry)]);
            Status = "Dungeon priority reset to the vendor default.";
        }
        catch (Exception e) { Status = "Could not reset dungeon priority (" + e.GetType().Name + ")."; }
        ReloadDungeonPriority();
    }
    public void SaveDungeonPriority()
    {
        try
        {
            Registry.WriteDungeonPriorityVerified([.. DungeonPriority.Select(r => (r.MapId, Enabled: r.Enabled))]);
            Status = "Dungeon priority saved and verified.";
        }
        catch (Exception e) { Status = "Could not save dungeon priority (" + e.GetType().Name + ")."; }
        ReloadDungeonPriority();
    }
    private int DungeonIndex(int mapId)
    {
        for (var index = 0; index < DungeonPriority.Count; index++)
            if (DungeonPriority[index].MapId == mapId) return index;
        return -1;
    }
    public bool CanMoveDungeonPriority(int mapId, int direction)
    {
        var index = DungeonIndex(mapId);
        if (index < 0) return false;
        var target = index + direction;
        return target >= 0 && target < DungeonPriority.Count;
    }
    public void MoveDungeonPriority(int mapId, int direction)
    {
        var index = DungeonIndex(mapId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= DungeonPriority.Count) return;
        DungeonPriority.Move(index, target);
        SaveDungeonPriority();
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
            dungeonMode = Registry.Read("ResourceGrab", "AutoDungeonMode") switch { 0 => 0, 1 => 1, _ => -1 };
        }
        catch (Exception e)
        {
            auctionPriceMode = abyssLayer = dungeonMode = -1;
            Status = "Resource settings unavailable (" + e.GetType().Name + ")";
        }
        Changed(nameof(AuctionPriceMode)); Changed(nameof(AbyssLayer)); Changed(nameof(DungeonMode));
        ReloadDungeonPriority();
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
    public string ConfigurationDetails => inspection == null ? resolution?.Note ?? "Select the game data root or its config folder."
        : inspection.DataRoot + Environment.NewLine + inspection.FileAvailability + Environment.NewLine + inspection.Association(Detection)
            + (resolution == null ? "" : Environment.NewLine + resolution.Note);
    public string DiagnosticsProcess => Detection.Processes.Count == 0 ? "No AION2 process observed."
        : string.Join(Environment.NewLine, Detection.Processes.Select(p => $"PID {p.Id} • {p.ExecutablePath ?? "Executable path inaccessible"}"
            + (p.Launcher is { } launcher ? $" • {launcher}" : "")));
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
    private string status = "Ready.";
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

    private string upgradeMaxBattlePowerK = "100";
    public string UpgradeMaxBattlePowerK { get => upgradeMaxBattlePowerK; set => Set(ref upgradeMaxBattlePowerK, value); }
    public string CurrentUpgradeMaxBattlePowerK { get; private set; } = "Default 100K (not written)";
    public string RawClass { get; private set; } = "Unknown";
    public string RawRace { get; private set; } = "Unknown";
    public int? AutoTaskRace => ReadCharacterDefault("CharacterRace");
    public int? AutoTaskJob => ReadCharacterDefault("CharacterJob");

    private int? ReadCharacterDefault(string key)
    {
        if (!OriginalConfigurationReady) return null;
        var stored = key == "CharacterRace" ? RawRace : RawClass;
        if (int.TryParse(stored, out int number)) return number;
        if (stored == "Default 2") return 2;
        if (stored == "Default 1") return 1;
        return null;
    }

    public void SaveCharacterDefault(string key, int value)
    {
        if (!OriginalConfigurationReady) { Status = "Select a configuration first."; return; }
        try
        {
            session!.SaveAutoTaskNumber(key, value);
            ReloadRegistry();
            Status = "Character default saved and verified.";
        }
        catch (PartialSettingsWriteException e)
        {
            ReloadRegistry();
            ReportSaveFailure(e);
        }
        catch (Exception e) { ReportSaveFailure(e); }
    }

    public string RegistryCharacterRace { get; private set; } = "Missing";
    public string RegistryCharacterJob { get; private set; } = "Missing";
    public string WorkflowEditorStatus { get; private set; } = "";
    public string ScheduleEditorStatus { get; private set; } = "";
    public RelayCommand SaveDailyCommand { get; }
    public RelayCommand SaveScheduleCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand DownCommand { get; }
    private readonly CancellationTokenSource lifetime = new();
    private readonly CaptchaMonitor captchaMonitor;
    private CaptchaSnapshot captchaSnapshot = new(CaptchaPhase.Idle, null, null, "No challenge has appeared yet. Auto-resolve is watching.", "");
    private string captchaStatusText = "CAPTCHA monitoring starting";
    private string captchaDiagnostics = "CAPTCHA monitor has not observed provider signals yet.";
    private bool pollingCaptcha;
    public string CaptchaStatusText { get => captchaStatusText; private set => Set(ref captchaStatusText, value); }
    public string CaptchaDiagnostics { get => captchaDiagnostics; private set => Set(ref captchaDiagnostics, value); }
    public bool CaptchaChallengeActive => captchaSnapshot.ChallengeActive;
    public IReadOnlyList<string> CaptchaRecordLines { get; private set; } = [];
    public string CaptchaRecordsSummary => CaptchaRecordLines.Count == 0
        ? "Nothing yet. Challenges, answers and results appear here as they happen."
        : $"Latest challenge activity ({CaptchaRecordLines.Count} entries).";
    private readonly object recordLock = new();
    private readonly List<(DateTimeOffset At, string Text)> solverRecordHistory = new();

    // Live session controls and record reads over the installed LegionCommander runtime bridge.
    // Hexor observes state and issues only explicit operator-initiated controls (pause/continue/stop
    // of a running session, incident resolution, record reads). Hexor never starts automation, never
    // touches game memory, and never adds, edits, or logs in game accounts.
    private readonly IRuntimeMcpClient runtimeMcp = new RuntimeMcpClient();
    // Hexor connects to the game session itself, so live status does not depend on
    // LegionCommander running. The bridge stays optional (controller records when it is open).
    private readonly GameIpcManager gameIpc = new();
    private readonly CaptchaAutoSolver captchaSolver;
    private string liveBridgeStatus = "Optional controller bridge not probed yet.";
    private string captchaAutoStatus = "Auto-resolve is starting…";
    private string liveGameStatus = "Game connection not checked yet. Refresh live status to check.";
    private IReadOnlyList<RuntimeSessionRow> liveSessionRows = [];
    private IReadOnlyList<RuntimeIncidentRow> liveIncidentRows = [];
    private IReadOnlyList<string> liveDailyRecords = [];
    private string liveDailyRecordsStatus = "Enter a game day (YYYY-MM-DD) and load records for one session account.";
    private IReadOnlyList<string> liveCaptchaRows = [];
    private string liveCaptchaStatus = "CAPTCHA activity is listed after a live refresh when a controller session is connected.";
    private bool refreshingLive;
    public string LiveBridgeStatus { get => liveBridgeStatus; private set => Set(ref liveBridgeStatus, value); }
    public string LiveGameStatus { get => liveGameStatus; private set => Set(ref liveGameStatus, value); }
    public string CaptchaAutoStatus { get => captchaAutoStatus; private set => Set(ref captchaAutoStatus, value); }
    public IReadOnlyList<RuntimeSessionRow> LiveSessionRows { get => liveSessionRows; private set => Set(ref liveSessionRows, value); }
    public IReadOnlyList<RuntimeIncidentRow> LiveIncidentRows { get => liveIncidentRows; private set => Set(ref liveIncidentRows, value); }
    public IReadOnlyList<string> LiveDailyRecords { get => liveDailyRecords; private set => Set(ref liveDailyRecords, value); }
    public string LiveDailyRecordsStatus { get => liveDailyRecordsStatus; private set => Set(ref liveDailyRecordsStatus, value); }
    public IReadOnlyList<string> LiveCaptchaRows { get => liveCaptchaRows; private set => Set(ref liveCaptchaRows, value); }
    public string LiveCaptchaStatus { get => liveCaptchaStatus; private set => Set(ref liveCaptchaStatus, value); }
    public IReadOnlyList<RuntimeSessionRow> LiveSessions => liveSessionRows;

    public async Task RefreshLiveStatusAsync()
    {
        if (refreshingLive || lifetime.IsCancellationRequested) return;
        refreshingLive = true;
        try
        {
            LiveGameStatus = gameIpc.Describe();
            RebuildLiveItems();
            var available = await Task.Run(() => runtimeMcp.ProbeAsync(lifetime.Token), lifetime.Token);
            if (!available)
            {
                LiveBridgeStatus = runtimeMcp.StatusDescription;
                LiveSessionRows = [];
                LiveIncidentRows = [];
                LiveCaptchaRows = [];
                liveCaptchaStatus = "No automation controller session is connected; nothing recorded yet.";
                Changed(nameof(LiveCaptchaStatus));
                RebuildLiveItems();
                Diagnostics.Log("live-control", "refreshed; bridge unavailable; direct sessions only");
                return;
            }
            var sessionsResult = await runtimeMcp.SendAsync("sessions.list", new { }, lifetime.Token);
            if (!sessionsResult.Ok)
            {
                LiveBridgeStatus = "The optional controller bridge returned an error: " + sessionsResult.Error;
                LiveSessionRows = [];
            }
            else
            {
                var rows = RuntimeMcpResponses.Sessions(sessionsResult.Payload);
                LiveBridgeStatus = rows.Count == 0
                    ? "Optional controller bridge is available. No controller sessions are connected."
                    : $"Optional controller bridge is available. Controller sessions: {rows.Count}.";
                LiveSessionRows = rows;
            }
            RebuildLiveItems();
            var incidents = new List<RuntimeIncidentRow>();
            foreach (var session in liveSessionRows)
            {
                var listResult = await runtimeMcp.SendAsync("automation.incidents.list", new
                {
                    accountId = session.AccountId,
                    afterSequence = 0,
                    status = "open",
                    limit = 50,
                }, lifetime.Token);
                if (listResult.Ok) incidents.AddRange(RuntimeMcpResponses.Incidents(listResult.Payload));
            }
            LiveIncidentRows = incidents;
            await LoadCaptchaOperationsFromSessionsAsync();
            RebuildLiveItems();
            Diagnostics.Log("live-control", $"refreshed; sessions={liveSessionRows.Count}; incidents={incidents.Count}");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { LiveBridgeStatus = "Optional controller bridge check failed (" + e.GetType().Name + ")."; Diagnostics.Log("live-control", "refresh-failed; type=" + e.GetType().Name); }
        finally { refreshingLive = false; }
    }

    // Runs an explicit operator control on one session account. The pause/continue/stop operations
    // are the vendor bridge's daily.control verbs; start is deliberately not exposed from Hexor.
    public async Task DailyControlAsync(int accountId, string operation)
    {
        try
        {
            var result = await runtimeMcp.SendAsync("daily.control", new { accountId, operation }, lifetime.Token);
            Status = result.Ok
                ? $"Session control '{operation}' was accepted for account {accountId}."
                : $"Session control '{operation}' was rejected: {result.Error}";
        }
        catch (Exception e) { Status = "Session control could not be sent (" + e.GetType().Name + ")."; }
        await RefreshLiveStatusAsync();
    }

    // Claims and resolves one open incident with the operator's chosen outcome. Claim leases the
    // incident to Hexor so concurrent resolvers cannot double-resolve it.
    public async Task ResolveIncidentAsync(RuntimeIncidentRow incident, string outcome, string summary)
    {
        try
        {
            var claim = await runtimeMcp.SendAsync("automation.incidents.claim", new
            {
                accountId = incident.AccountId,
                incidentId = incident.IncidentId,
                claimant = "Hexor Control Center",
                leaseMs = 60000,
            }, lifetime.Token);
            if (!claim.Ok) { Status = "Incident claim failed: " + claim.Error; return; }
            string claimToken = claim.Payload.TryGetProperty("claimToken", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString() ?? "" : "";
            var resolve = await runtimeMcp.SendAsync("automation.incidents.resolve", new
            {
                accountId = incident.AccountId,
                incidentId = incident.IncidentId,
                claimToken,
                outcome,
                summary,
                resumeStateMachine = outcome != "close_only",
            }, lifetime.Token);
            Status = resolve.Ok
                ? $"Incident {incident.IncidentId} resolved with outcome '{outcome}'."
                : "Incident resolution failed: " + resolve.Error;
        }
        catch (Exception e) { Status = "Incident resolution could not be sent (" + e.GetType().Name + ")."; }
        await RefreshLiveStatusAsync();
    }

    // daily.records uses the game's own day boundary (UTC+8) with the documented YYYY-MM-DD format.
    public async Task LoadDailyRecordsAsync(int accountId, string gameDay, int? serverId = null)
    {
        try
        {
            var summary = await runtimeMcp.SendAsync("daily.records", new
            {
                accountId,
                operation = "summary",
                gameDay,
                serverId,
            }, lifetime.Token);
            if (!summary.Ok)
            {
                LiveDailyRecords = [];
                LiveDailyRecordsStatus = "Daily records could not be read: " + summary.Error;
                return;
            }
            var rows = RuntimeMcpResponses.DailySummaries(summary.Payload);
            LiveDailyRecords = rows.Select(row =>
                $"#{row.AccountId} {row.GameDay} · {row.Summary}" + (row.Detail.Length > 0 ? " · " + row.Detail : "")).ToArray();
            LiveDailyRecordsStatus = rows.Count == 0
                ? $"No daily summaries are recorded for account {accountId} on {gameDay}."
                : $"Daily summaries for account {accountId} on {gameDay}: {rows.Count}.";
        }
        catch (Exception e) { LiveDailyRecordsStatus = "Daily records could not be read (" + e.GetType().Name + ")."; }
        Changed(nameof(LiveDailyRecords)); Changed(nameof(LiveDailyRecordsStatus));
    }

    private string liveRecordsGameDay = "";
    private string liveRecordsServerId = "";
    public string LiveRecordsGameDay
    {
        get => liveRecordsGameDay;
        set
        {
            if (Set(ref liveRecordsGameDay, value))
            {
                liveRecordsGameDayValid = System.Text.RegularExpressions.Regex.IsMatch(
                    value.Trim(), @"^\d{4}-\d{2}-\d{2}$");
                Changed(nameof(LiveRecordsGameDayValid));
            }
        }
    }
    private bool liveRecordsGameDayValid;
    public bool LiveRecordsGameDayValid => liveRecordsGameDayValid;
    public string LiveRecordsServerId { get => liveRecordsServerId; set => Set(ref liveRecordsServerId, value); }

    public async Task LoadDailyRecordsForAsync(LiveSessionRowViewModel item)
    {
        int? serverId = int.TryParse(liveRecordsServerId.Trim(), out var parsed) ? parsed : null;
        await LoadDailyRecordsAsync(item.Row.AccountId, liveRecordsGameDay.Trim(), serverId);
    }

    // Operator prompts (stop confirmation, incident resolution) are supplied by the window so the
    // confirmation dialogs stay in the UI layer.
    public Func<RuntimeSessionRow, bool>? LiveStopConfirmation { get; set; }
    public Func<RuntimeIncidentRow, (string Outcome, string Summary)?>? LiveResolvePrompt { get; set; }

    public IReadOnlyList<LiveSessionRowViewModel> LiveSessionItems { get; private set; } = [];
    public IReadOnlyList<LiveIncidentRowViewModel> LiveIncidentItems { get; private set; } = [];

    private void RebuildLiveItems()
    {
        var direct = gameIpc.Sessions().Where(s => s.ClientConnected)
            .Select(s => new RuntimeSessionRow($"game-{s.ProcessId}", 0, s.ProcessId, s.ActivityState, true,
                s.InGame ? "in game" : s.StatusSeen ? "connected" : "waiting", s.Level, s.MapId, Direct: true))
            .ToArray();
        var merged = direct.Concat(liveSessionRows.Where(b => direct.All(d => d.ProcessId != b.ProcessId))).ToArray();
        LiveSessionItems = merged.Select(row => new LiveSessionRowViewModel(this, row)).ToArray();
        LiveIncidentItems = liveIncidentRows.Select(row => new LiveIncidentRowViewModel(this, row)).ToArray();
        Changed(nameof(LiveSessionItems)); Changed(nameof(LiveIncidentItems));
    }

    // Game IPC events arrive on background threads; rebuild the live UI on the dispatcher.
    private void OnGameIpcChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) { RebuildGameIpcUi(); return; }
        if (dispatcher.CheckAccess()) RebuildGameIpcUi();
        else _ = dispatcher.InvokeAsync(RebuildGameIpcUi);
    }

    private void RebuildGameIpcUi()
    {
        LiveGameStatus = gameIpc.Describe();
        RebuildLiveItems();
        UpdateCaptchaAutoStatus();
        RefreshCaptchaActivityLog();
    }

    // Activity log merges the auto-resolver's own challenge history with the visual monitor's
    // observations, newest first, so a challenge that was solved or rejected always appears.
    private void RefreshCaptchaActivityLog()
    {
        var entries = new List<(DateTimeOffset At, string Text)>();
        entries.AddRange(captchaSolver.History.Select(h => (h.At, "Auto-resolve: " + h.Text)));
        lock (recordLock)
        {
            entries.AddRange(solverRecordHistory);
        }
        entries.AddRange(captchaSnapshot.RecentRecords.Select(record =>
            (record.ObservedAt, "Monitor: " + CaptchaPhaseLabel(record.Phase))));
        CaptchaRecordLines = entries.OrderByDescending(e => e.At)
            .Take(50)
            .Select(e => $"{e.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {e.Text}")
            .ToArray();
        Changed(nameof(CaptchaRecordLines));
        Changed(nameof(CaptchaRecordsSummary));
    }

    // Audible alert: the moment a game client reports a challenge, play a system sound while
    // the preference is on. The auto-resolve answers on its own; this only tells the operator.
    private void PlayCaptchaAlert(int pid, string requestId, string imageBase64, string? mimeType)
    {
        if (!Preferences.CaptchaNotifications) return;
        try { System.Media.SystemSounds.Exclamation.Play(); }
        catch (Exception e) { Diagnostics.Log("captcha-solver", "alert-sound-failed; type=" + e.GetType().Name); }
    }

    private void UpdateCaptchaAutoStatus()
    {
        lock (recordLock)
        {
            foreach (var entry in captchaSolver.History)
                if (!solverRecordHistory.Contains(entry)) solverRecordHistory.Add(entry);
            if (solverRecordHistory.Count > 50) solverRecordHistory.RemoveRange(0, solverRecordHistory.Count - 50);
        }
        var text = captchaSolver.Describe();
        if (!gameIpc.Sessions().Any(s => s.ClientConnected && s.PipeOwnedByHexor))
            text += "\nThe auto-resolve listens while a game session is connected.";
        CaptchaAutoStatus = text;
    }

    // Direct game controls: stop the running automation or ask the client DLL for a fresh status.
    public async Task GameStopAsync(int processId)
    {
        try
        {
            await gameIpc.SendCommandAsync(processId, "StopAll");
            Status = $"Stop command sent to the game session on PID {processId}.";
        }
        catch (Exception e) { Status = $"Stop command for PID {processId} failed ({e.GetType().Name})."; }
        RebuildGameIpcUi();
    }

    // Operator-initiated task sequence controls: the same commands the in-game menu and the
    // vendor console send (StartAutoTask / StopAll), addressed to the connected game session.
    public GameSessionSnapshot? ConnectedGameSession() =>
        gameIpc.Sessions().FirstOrDefault(s => s.ClientConnected && s.PipeOwnedByHexor);

    // Sequence runner following the vendor algorithm: the vendor console drives the daily
    // workflow with runtime commands (commander.command), one call per activity step, using the
    // same targets and arguments. Only explicit operator actions start or stop the sequence.
    private CancellationTokenSource? sequenceRun;
    private int sequencePid;
    private ulong sequenceLuaVmGeneration;

    private static (string Target, string Label) StepTarget(InstructionType type) => type switch
    {
        InstructionType.AutoQuest => ("auto_task", "Auto Dungeon"),
        InstructionType.AutoKraoDungeon => ("krao_dungeon", "Auto Krao Dungeon"),
        InstructionType.KinahFarming => ("combat", "Kinah Farming"),
        InstructionType.Gathering => ("gather", "Gathering"),
        _ => ("combat", "Switch Character"),
    };

    private string DescribeCommandResult(JsonElement result)
    {
        try
        {
            if (result.ValueKind != JsonValueKind.Object) return string.Empty;
            var status = result.TryGetProperty("Status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
            var error = result.TryGetProperty("Error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
            if (!string.IsNullOrWhiteSpace(error)) return "status=" + status + "; error=" + error;
            return "status=" + status;
        }
        catch { return string.Empty; }
    }

    private async Task<JsonElement> CommanderCommandAsync(int processId, string operation, object? arguments)
    {
        // Keys match the vendor console's serialization exactly (Newtonsoft default keeps
        // PascalCase): Operation / Arguments / CommandId / ExpectedLuaVmGeneration.
        return await gameIpc.SendRuntimeRequestAsync(processId, "commander.command", new Dictionary<string, object?>
        {
            ["Operation"] = operation,
            ["Arguments"] = arguments ?? new { },
            ["CommandId"] = Guid.NewGuid().ToString("N"),
            ["ExpectedLuaVmGeneration"] = sequenceLuaVmGeneration,
        }).ConfigureAwait(false);
    }

    // The vendor allocates each character's daily floor-gold target as a random 93.0%-99.0%
    // share of the daily kinah limit (RandomNumberGenerator.GetInt32(930, 991)) and reuses it
    // for the whole game day. Hexor keeps the same rule per (character, day) in memory.
    private readonly Dictionary<string, int> goldTargetPermille = new();
    private int DailyGoldTargetPermille(string characterName)
    {
        var key = characterName + "|" + DateTimeOffset.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        if (!goldTargetPermille.TryGetValue(key, out var permille))
        {
            permille = Random.Shared.Next(930, 991);
            goldTargetPermille[key] = permille;
            Diagnostics.Log("daily-sequence", "gold-target-allocated; character=" + characterName + "; permille=" + permille);
        }
        return permille;
    }

    private async Task<ulong> ReadLuaVmGenerationAsync(int processId)
    {
        var snapshot = await gameIpc.SendRuntimeRequestAsync(processId, "commander.snapshot", new { }).ConfigureAwait(false);
        if (snapshot.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "LuaVmGeneration", "luaVmGeneration", "luaVMGeneration", "LuaVMGeneration" })
                if (snapshot.TryGetProperty(name, out var node) && node.ValueKind is JsonValueKind.Number)
                    return node.GetUInt64();
        }
        return 0;
    }

    private async Task StopActivityAsync(int processId, string reason)
    {
        try { await CommanderCommandAsync(processId, "stop", new Dictionary<string, object?> { ["Reason"] = reason }).ConfigureAwait(false); }
        catch (Exception e) { Diagnostics.Log("daily-sequence", "runtime-stop-failed; type=" + e.GetType().Name); }
        // Belt and braces: the plain pipe stop clears the gather/combat/dungeon machines too.
        try { await gameIpc.SendCommandAsync(processId, "StopAll").ConfigureAwait(false); }
        catch (Exception e) { Diagnostics.Log("daily-sequence", "pipe-stop-failed; type=" + e.GetType().Name); }
    }

    public async Task StartDailySequenceAsync()
    {
        var session = ConnectedGameSession();
        if (session == null)
        {
            Status = "Start AION2 first — the task sequence runs inside the game.";
            return;
        }
        if (sequenceRun != null) { Status = "The sequence is already running; press Stop first."; return; }
        var steps = Preferences.Instructions.Where(i => i.Enabled && i.Type != InstructionType.SwitchCharacter).ToList();
        if (steps.Count == 0) { Status = "Enable at least one instruction before starting the sequence."; return; }
        try
        {
            sequenceLuaVmGeneration = await ReadLuaVmGenerationAsync(session.ProcessId);
            var run = new CancellationTokenSource();
            sequenceRun = run; sequencePid = session.ProcessId;
            Diagnostics.Log("daily-sequence", "started; pid=" + session.ProcessId + "; steps=" + steps.Count + "; luaVm=" + sequenceLuaVmGeneration);
            Status = "Sequence starting…";
            _ = RunSequenceAsync(steps, session.ProcessId, run.Token);
        }
        catch (Exception e)
        {
            Status = "The game did not accept the sequence start (" + e.GetType().Name + ").";
            Diagnostics.Log("daily-sequence", "start-failed; type=" + e.GetType().Name);
        }
        await Task.CompletedTask;
    }

    private async Task RunSequenceAsync(List<Instruction> steps, int processId, CancellationToken token)
    {
        try
        {
            // The vendor clears any previous activity, then starts each enabled step in order and
            // gives it its configured duration before moving on.
            await StopActivityAsync(processId, "sequence start");
            await Task.Delay(500, token);
            var hasFloorGold = steps.Any(s => s.Type == InstructionType.KinahFarming);
            for (var index = 0; index < steps.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var step = steps[index];
                var (target, label) = StepTarget(step.Type);
                var fresh = gameIpc.Sessions().FirstOrDefault(s => s.ProcessId == processId && s.ClientConnected && s.PipeOwnedByHexor);
                var arguments = new Dictionary<string, object?>
                {
                    ["SmartPatrol"] = step.SmartPatrol,
                    ["GoldTargetPermille"] = DailyGoldTargetPermille(string.IsNullOrWhiteSpace(fresh?.Name) ? "character" : fresh.Name),
                    ["QueueFloorGold"] = hasFloorGold,
                    ["QueueFloorGoldSmartPatrol"] = steps.FirstOrDefault(s => s.Type == InstructionType.KinahFarming)?.SmartPatrol ?? false,
                };
                var started = await CommanderCommandAsync(processId, target, arguments);
                var detail = DescribeCommandResult(started);
                Status = $"Sequence: {label} started ({index + 1} of {steps.Count}).";
                Diagnostics.Log("daily-sequence", $"step; pid={processId}; label={label}; target={target}; {detail}");
                if (detail.Contains("status=failed", StringComparison.Ordinal))
                {
                    Status = $"The {label} step was rejected by the game. {detail}";
                }
                RebuildGameIpcUi();
                var minutes = Math.Clamp(step.MinimumMinutes > 0 ? step.MinimumMinutes : 1, 1, 10080);
                await Task.Delay(TimeSpan.FromMinutes(minutes), token);
                if (index < steps.Count - 1)
                {
                    await StopActivityAsync(processId, "next step");
                    await Task.Delay(500, token);
                }
            }
            await StopActivityAsync(processId, "sequence finished");
            Status = "Sequence finished.";
            Diagnostics.Log("daily-sequence", "completed; pid=" + processId);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Status = $"The sequence stopped early ({e.GetType().Name}).";
            Diagnostics.Log("daily-sequence", "sequence-failed; type=" + e.GetType().Name);
        }
        finally { sequenceRun = null; RebuildGameIpcUi(); }
    }

    public async Task StopDailySequenceAsync()
    {
        var session = ConnectedGameSession();
        if (session == null && sequenceRun == null)
        {
            Status = "No connected game session — nothing to stop.";
            return;
        }
        try
        {
            sequenceRun?.Cancel();
            if (session != null) await StopActivityAsync(session.ProcessId, "stop requested");
            Status = $"Task sequence stopped for the game session on PID {session?.ProcessId ?? sequencePid}.";
            Diagnostics.Log("daily-sequence", "stopped; pid=" + (session?.ProcessId ?? sequencePid));
        }
        catch (Exception e) { Status = $"Stopping the task sequence failed ({e.GetType().Name})."; Diagnostics.Log("daily-sequence", "stop-failed; type=" + e.GetType().Name); }
        RebuildGameIpcUi();
    }

    // Vendor-parity gathering controls. The module ignores StartGather while AutoTask is
    // running and below level 45, so stop the task sequence first, then start gathering.
    public async Task StartGatheringAsync()
    {
        var session = ConnectedGameSession();
        if (session == null)
        {
            Status = "Start AION2 first — gathering runs inside the game.";
            return;
        }
        try
        {
            if (session.IsAutoTask)
            {
                await gameIpc.SendCommandAsync(session.ProcessId, "StopAll");
                Diagnostics.Log("gather-control", "auto-task-stopped-before-gather; pid=" + session.ProcessId);
                await Task.Delay(500);
            }
            await gameIpc.SendCommandAsync(session.ProcessId, "StartGather");
            Status = $"Gathering started for the game session on PID {session.ProcessId}.";
            Diagnostics.Log("gather-control", "started; pid=" + session.ProcessId);
        }
        catch (Exception e) { Status = $"Starting gathering failed ({e.GetType().Name})."; Diagnostics.Log("gather-control", "start-failed; type=" + e.GetType().Name); }
        RebuildGameIpcUi();
    }

    public async Task StopGatheringAsync()
    {
        var session = ConnectedGameSession();
        if (session == null)
        {
            Status = "No connected game session — nothing to stop.";
            return;
        }
        try
        {
            await gameIpc.SendCommandAsync(session.ProcessId, "StopGather");
            Status = $"Gathering stopped for the game session on PID {session.ProcessId}.";
            Diagnostics.Log("gather-control", "stopped; pid=" + session.ProcessId);
        }
        catch (Exception e) { Status = $"Stopping gathering failed ({e.GetType().Name})."; Diagnostics.Log("gather-control", "stop-failed; type=" + e.GetType().Name); }
        RebuildGameIpcUi();
    }

    public async Task GameRefreshAsync(int processId)
    {
        try
        {
            await gameIpc.SendCommandAsync(processId, "RefreshCurrentStatus");
            Status = $"Status refresh requested for PID {processId}.";
        }
        catch (Exception e) { Status = $"Status refresh for PID {processId} failed ({e.GetType().Name})."; }
    }

    private async Task LoadCaptchaOperationsFromSessionsAsync()
    {
        try
        {
            var lines = new List<string>();
            foreach (var session in liveSessionRows)
            {
                var list = await runtimeMcp.SendAsync("captcha.operations.list", new
                {
                    accountId = session.AccountId,
                    expectedRuntimeSessionId = session.RuntimeSessionId,
                    expectedProcessId = session.ProcessId,
                    limit = 20,
                }, lifetime.Token);
                if (!list.Ok) { lines.Add($"#{session.AccountId}: controller returned {list.Error}"); continue; }
                var rows = RuntimeMcpResponses.CaptchaOperations(list.Payload);
                if (rows.Count == 0) lines.Add($"#{session.AccountId}: no CAPTCHA operations recorded.");
                else lines.AddRange(rows.Select(row => $"#{row.AccountId} {row.State}" + (row.Detail.Length > 0 ? " · " + row.Detail : "") + $" ({row.OperationId})"));
            }
            LiveCaptchaRows = lines;
            liveCaptchaStatus = lines.Count == 0
                ? "No CAPTCHA activity recorded by the connected controller sessions."
                : $"CAPTCHA activity listed for {liveSessionRows.Count} session(s).";
        }
        catch (Exception e) { liveCaptchaStatus = "CAPTCHA operations could not be listed (" + e.GetType().Name + ")."; }
        Changed(nameof(LiveCaptchaRows)); Changed(nameof(LiveCaptchaStatus));
    }

    public MainViewModel(IJsonConfigurationService? json = null, IRegistrySettingsService? registry = null, ILegionConfigurationService? configuration = null,
        ICharacterDetectionService? runtimeDetector = null, IConfigurationInspector? configurationInspector = null,
        Func<IReadOnlyList<string>>? configurationCandidates = null, VendorUpdaterManager? vendorUpdater = null, CaptchaMonitor? captchaMonitor = null)
    {
        automaticDiscovery = json == null && registry == null || configurationCandidates != null;
        this.runtimeDetector = runtimeDetector ?? new CharacterDetectionService(new GameInstallationDiscoveryService());
        this.vendorUpdater = vendorUpdater ?? new VendorUpdaterManager();
        this.captchaMonitor = captchaMonitor ?? new CaptchaMonitor();
        this.configurationInspector = configurationInspector ?? new ConfigurationInspector();
        gameIpc.Changed += OnGameIpcChanged;
        gameIpc.Start();
        captchaSolver = new CaptchaAutoSolver(gameIpc);
        captchaSolver.Changed += () => OnGameIpcChanged();
        gameIpc.CaptchaRequestReceived += PlayCaptchaAlert;
        UpdateCaptchaAutoStatus();
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
        AddToggle("ResourceGrab", "AuctionAutoSell", "Automatically sell at auction", null);
        foreach (var feature in LegionCommanderFeatureSettings.Booleans)
            AddToggle(feature.Section, feature.Name, feature.Label, false);
        SelectedInstruction = Preferences.Instructions.FirstOrDefault();
        SaveDailyCommand = new(() => { SaveDaily(); ReportWorkflowEditorStatus(); });
        SaveScheduleCommand = new(() =>
        {
            SaveSchedule();
            ScheduleEditorStatus = Status;
            Changed(nameof(ScheduleEditorStatus));
        });
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
            var battlePower = Registry.Inspect("Enhance", "AutoUpgradeMaxBattlePowerK");
            CurrentUpgradeMaxBattlePowerK = battlePower.Value?.ToString() ?? (battlePower.Exists ? "Unsupported type preserved" : "Default 100K (not written)");
            UpgradeMaxBattlePowerK = Math.Clamp(battlePower.Value ?? 100, 1, 100).ToString(); Changed(string.Empty);
        }
        catch (Exception e) { Status = "Registry unavailable (" + e.GetType().Name + ")."; }
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
    private void ReportWorkflowEditorStatus()
    {
        WorkflowEditorStatus = Status;
        Changed(nameof(WorkflowEditorStatus));
    }

    public void LoadDefaultWorkflow()
    {
        LoadPlan(WorkflowPlan.Default());
        Changed(nameof(Preferences));
        Status = "Default sequence loaded into the draft. Apply to save it.";
        ReportWorkflowEditorStatus();
    }

    public void ImportWorkflow(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > 4_000_000) throw new InvalidDataException("Workflow file exceeds the supported size.");
            if (input.ReadByte() != 0xef || input.ReadByte() != 0xbb || input.ReadByte() != 0xbf) input.Position = 0;
            using var parsed = JsonDocument.Parse(input);
            LegionConfigurationService.RejectDuplicateProperties(parsed.RootElement);
            if (JsonNode.Parse(parsed.RootElement.GetRawText()) is not JsonObject document ||
                (document.ContainsKey("Workflow") && document["Workflow"] is not JsonObject))
                throw new InvalidDataException("Choose a workflow JSON file or a settings export.");
            LoadPlan(WorkflowPlan.Read(document["Workflow"] as JsonObject ?? document).Normalize());
            Changed(nameof(Preferences));
            Status = "Workflow loaded into the draft. Apply to save it.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or InvalidDataException)
        { Status = e is InvalidDataException ? e.Message : "Could not import the workflow. Check the file and try again."; }
        ReportWorkflowEditorStatus();
    }

    public void ExportWorkflow(string path)
    {
        try
        {
            var revision = session?.Ready == true ? session.Document.StoredWorkflow.Revision : Guid.NewGuid().ToString("N");
            var json = DraftWorkflow(revision).ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(output);
            writer.Write(json);
            Status = "Workflow exported.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        { Status = e is InvalidDataException ? e.Message : "Could not export the workflow. Choose a new file name and try again."; }
        ReportWorkflowEditorStatus();
    }

    private WorkflowPlan DraftWorkflow(string revision)
    {
        var steps = Preferences.Instructions.Select(i =>
        {
            int min = i.MinimumMinutes, max = i.MaximumMinutes;
            if (i.Type == InstructionType.Gathering && (!int.TryParse(i.MinimumText, out min) || !int.TryParse(i.MaximumText, out max)))
                throw new InvalidDataException("Gathering step duration must be numeric.");
            return new WorkflowStep
            {
                Id = i.Id, Action = ToAction(i.Type), Enabled = i.Enabled, MinimumMinutes = min,
                MaximumMinutes = max, SmartPatrol = i.SmartPatrol, ResetTimer = i.ResetTimer, Preserved = i.CompatibleFields ?? new()
            };
        }).ToArray();
        if (!Enum.TryParse<CompletionAction>(Preferences.AfterFinish, out var finish) || !Enum.IsDefined(finish))
            throw new InvalidDataException("Choose what to do after all characters finish.");
        return new WorkflowPlan { Revision = revision, Steps = steps, Finish = finish };
    }

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
            Preferences.GatherMin = min; Preferences.GatherMax = max; if (Save()) Status = "Saved Hexor draft only; choose a valid game configuration to apply."; return;
        }
        if (!session.Ready) { Status = session.Document.Message; return; }
        try
        {
            session.SaveWorkflow(DraftWorkflow(session.Document.StoredWorkflow.Revision));
            LoadPlan(session.Document.EffectiveWorkflow); Changed(nameof(WorkflowNotice)); Changed(nameof(ConfigurationStatus));
            Status = "Workflow settings saved and verified. They apply when the game or the next task starts.";
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
        try { session.SaveSchedule(new(Preferences.ScheduleEnabled, accounts, min, max, rmin, rmax)); Status = "Work Schedule persisted and verified. It applies when the game or the next task starts."; }
        catch (Exception e) { ReportSaveFailure(e); }
    }
    private static bool Range(string a, string b, out int min, out int max) { max = 0; return int.TryParse(a, out min) && int.TryParse(b, out max) && min >= 1 && max >= min && max <= 10080; }
    private void ReportSaveFailure(Exception e) => Status = e is InvalidDataException or ConfigurationConflictException or PartialSettingsWriteException ? e.Message : "Settings were not saved (" + e.GetType().Name + ").";

    public void RetryAutoTaskMirror() { try { if (session == null) throw new InvalidDataException("Select a configuration first."); Status = session.RetryMirror(); } catch (Exception e) { ReportSaveFailure(e); } }
    public void SaveOriginalUiLanguage() { try { if (session == null) throw new InvalidDataException("Select a configuration first."); session.SaveLanguage("UserLanguage", OriginalUiCulture); Status = "Interface language preference saved; it applies the next time the game starts."; } catch (Exception e) { ReportSaveFailure(e); } }
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
    public bool CaptchaNotifications
    {
        get => Preferences.CaptchaNotifications;
        set
        {
            if (Preferences.CaptchaNotifications == value) return;
            var previous = Preferences.CaptchaNotifications; Preferences.CaptchaNotifications = value;
            if (!Save()) Preferences.CaptchaNotifications = previous;
            RefreshCaptchaTexts(captchaSnapshot); Changed();
        }
    }
    // Observation of challenge signals on the game session.
    // The game's own menu can rewrite a group document with stale values; re-assert Hexor's
    // registry truth on startup and once a minute so disabled shop options stay disabled.
    private DateTimeOffset lastSharedReconcile = DateTimeOffset.MinValue;
    private DateTimeOffset lastSettingsSweep = DateTimeOffset.MinValue;
    // The module keeps per-character settings (GatherConfig.targetNames) in its own memory and
    // re-saves them; repairing the file alone is undone by the next save. When a repair lands
    // while the game is connected, the same "apply default config" command the vendor console
    // sends makes the module reload the settings into its running state machines.
    private async Task SweepCharacterSettingsAsync()
    {
        if (!Runtime.CharacterSettingsRepairService.LiveSweepEnabled) return;
        if (DateTimeOffset.UtcNow - lastSettingsSweep < TimeSpan.FromMinutes(2)) return;
        lastSettingsSweep = DateTimeOffset.UtcNow;
        try
        {
            var (repaired, seeded) = Runtime.CharacterSettingsRepairService.Sweep();
            if (repaired > 0 || seeded > 0)
                Diagnostics.Log("settings-repair", $"sweep-completed; repaired={repaired}; seeded={seeded}");
        }
        catch (Exception e) { Diagnostics.Log("settings-repair", "sweep-error; type=" + e.GetType().Name); }
    }

    // Vendor-parity control: the module replaces the current character config with the saved
    // default and reloads its machines. Operator-initiated only — applying makes the module
    // save the reloaded config right away, so Hexor never sends it on its own.
    public async Task ApplyDefaultConfigAsync(int processId)
    {
        try
        {
            await gameIpc.SendCommandAsync(processId, "ApplyDefaultConfig");
            Status = $"Default config applied for the game session on PID {processId}.";
            Diagnostics.Log("settings-repair", $"applied-default; pid={processId}; mode=manual");
        }
        catch (Exception e)
        {
            Status = $"Applying the default config failed ({e.GetType().Name}).";
            Diagnostics.Log("settings-repair", $"apply-default-failed; pid={processId}; type={e.GetType().Name}");
        }
        RebuildGameIpcUi();
    }
    public void ReconcileSharedSettings()
    {
        if (DateTimeOffset.UtcNow - lastSharedReconcile < TimeSpan.FromMinutes(1)) return;
        lastSharedReconcile = DateTimeOffset.UtcNow;
        try { LegionSharedSettings.ReconcileAllGroups(Registry.RegistryRoot); }
        catch (Exception e) { Diagnostics.Log("shared-settings", "reconcile-error; type=" + e.GetType().Name); }
    }

    public async Task PollCaptchaAsync()
    {
        if (pollingCaptcha || lifetime.IsCancellationRequested) return;
        ReconcileSharedSettings();
        await SweepCharacterSettingsAsync();
        pollingCaptcha = true;
        try
        {
            var snapshot = await Task.Run(captchaMonitor.Observe, lifetime.Token);
            if (snapshot.Phase != captchaSnapshot.Phase || snapshot.Detail != captchaSnapshot.Detail)
                Diagnostics.Log("captcha-monitor", $"phase={snapshot.Phase}; observed={(snapshot.LastSignalAt != null ? "yes" : "no")}");
            var previous = captchaSnapshot;
            captchaSnapshot = snapshot;
            RefreshCaptchaTexts(snapshot);
            if (previous.ChallengeActive != snapshot.ChallengeActive) Changed(nameof(CaptchaChallengeActive));
            if (!ReferenceEquals(previous.RecentRecords, snapshot.RecentRecords))
            {
                CaptchaRecordLines = snapshot.RecentRecords.Select(record =>
                    $"{record.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {CaptchaPhaseLabel(record.Phase)}").ToArray();
                Changed(nameof(CaptchaRecordLines));
                Changed(nameof(CaptchaRecordsSummary));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Diagnostics.Log("captcha-monitor", "poll-failed; type=" + e.GetType().Name); }
        finally { pollingCaptcha = false; }
    }
    private void RefreshCaptchaTexts(CaptchaSnapshot snapshot)
    {
        captchaStatusText = Preferences.CaptchaNotifications ? CaptchaTile(snapshot) : "CAPTCHA notifications off";
        captchaDiagnostics = string.Join(Environment.NewLine,
            snapshot.Detail,
            "Signals: " + snapshot.SourcesDescription,
            "Hexor never reads challenge images or answers, never touches the provider's private automation channels, and cannot resolve the challenge for you.");
        Changed(nameof(CaptchaStatusText)); Changed(nameof(CaptchaDiagnostics));
    }
    private static string CaptchaTile(CaptchaSnapshot snapshot) => snapshot.Phase switch
    {
        CaptchaPhase.ChallengeDetected => CaptchaPhaseLabel(snapshot.Phase) + " " + TileTime(snapshot.DetectedAt),
        CaptchaPhase.Resolved => CaptchaPhaseLabel(snapshot.Phase) + " " + TileTime(snapshot.LastSignalAt),
        _ => CaptchaPhaseLabel(snapshot.Phase),
    };
    private static string CaptchaPhaseLabel(CaptchaPhase phase) => phase switch
    {
        CaptchaPhase.ChallengeDetected => "CAPTCHA detected",
        CaptchaPhase.AnswerDispatched => "CAPTCHA active · awaiting result",
        CaptchaPhase.Resolved => "CAPTCHA clearance reported",
        CaptchaPhase.Failed => "CAPTCHA attempt failed · check game",
        CaptchaPhase.OutcomeUnknown => "CAPTCHA outcome unknown",
        _ => "No challenge yet",
    };
    private static string TileTime(DateTimeOffset? time) => time?.ToLocalTime().ToString("HH:mm:ss") ?? "";
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

public sealed class LiveSessionRowViewModel
{
    public RuntimeSessionRow Row { get; }
    private readonly MainViewModel model;
    public string Label => Row.Direct
        ? $"PID {Row.ProcessId} · {Row.ControllerState} · readiness {Row.Readiness} · Lv {Row.CharacterLevel} · map {Row.MapId}"
        : $"#{Row.AccountId} · PID {Row.ProcessId} · {Row.ControllerState} · readiness {Row.Readiness} · Lv {Row.CharacterLevel} · map {Row.MapId}";
    public string Ipc => Row.Direct ? "Connected (Hexor)" : Row.IpcConnected ? "Connected" : "Not connected";
    public System.Windows.Visibility BridgeControls => Row.Direct ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility DirectControls => Row.Direct ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public RelayCommand PauseCommand { get; }
    public RelayCommand ContinueCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RecordsCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public LiveSessionRowViewModel(MainViewModel model, RuntimeSessionRow row)
    {
        this.model = model;
        Row = row;
        PauseCommand = new(() => _ = model.DailyControlAsync(Row.AccountId, "pause"));
        ContinueCommand = new(() => _ = model.DailyControlAsync(Row.AccountId, "continue"));
        StopCommand = new(() =>
        {
            var confirmed = model.LiveStopConfirmation?.Invoke(Row) ?? false;
            if (!confirmed) return;
            _ = Row.Direct ? model.GameStopAsync(Row.ProcessId) : model.DailyControlAsync(Row.AccountId, "stop");
        });
        RecordsCommand = new(() => _ = model.LoadDailyRecordsForAsync(this));
        RefreshCommand = new(() => _ = model.GameRefreshAsync(Row.ProcessId));
    }
}

public sealed class LiveIncidentRowViewModel
{
    public RuntimeIncidentRow Row { get; }
    private readonly MainViewModel model;
    public string Label => $"#{Row.AccountId} · {Row.Status} · {Row.Summary}";
    public RelayCommand ResolveCommand { get; }
    public LiveIncidentRowViewModel(MainViewModel model, RuntimeIncidentRow row)
    {
        this.model = model;
        Row = row;
        ResolveCommand = new(() =>
        {
            var response = model.LiveResolvePrompt?.Invoke(Row);
            if (response is not { } prompt) return;
            _ = model.ResolveIncidentAsync(Row, prompt.Outcome, prompt.Summary);
        });
    }
}
