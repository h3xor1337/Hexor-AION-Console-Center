using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Hexor;
using Hexor.Compatibility;
using Microsoft.Win32;

internal static class CompatibilityTests
{
    public static void Run(string temporaryRoot, RegistrySettingsService registry, Action<bool, string> check)
    {
        var root = Path.Combine(temporaryRoot, "verified");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var path = Path.Combine(root, "config", "settings.json");
        var machinePath = Path.Combine(root, "config", "machine.json");
        JsonObject Envelope(WorkflowPlan? plan = null, int version = 2) => new()
        {
            ["FormatVersion"] = 1, ["Values"] = new JsonObject { ["UserLanguage"] = "ko-KR", ["FutureScalar"] = "preserve" },
            ["Workflow"] = (plan ?? WorkflowPlan.Default()).ToJson(), ["WorkflowDefaultVersion"] = version,
            ["FutureRoot"] = new JsonObject { ["nested"] = new JsonArray(1, 2, 3) }
        };
        void Write(JsonObject document) => File.WriteAllText(path, document.ToJsonString());
        void Throws<T>(Action action, string label) where T : Exception
        {
            bool threw = false; try { action(); } catch (T) { threw = true; } check(threw, label);
        }
        var adapter = new LegionConfigurationService(root);
        var missingRoot = Path.Combine(temporaryRoot, "not-created");
        var missing = new LegionConfigurationService(missingRoot);
        check(missing.Read().Availability == ConfigurationAvailability.Missing && !Directory.Exists(missingRoot), "Compatibility read creates no missing directory/file");
        Throws<InvalidDataException>(() => missing.SaveScalars(missing.Read(), new Dictionary<string, string> { ["GameLanguage"] = "en-US" }), "Missing documents cannot be silently recreated");

        var original = Envelope();
        var workflow = (JsonObject)original["Workflow"]!;
        workflow["FutureWorkflow"] = "keep";
        ((JsonObject)((JsonArray)workflow["Steps"]!)[0]!)["FutureStep"] = new JsonObject { ["unknown"] = true };
        Write(original);
        var before = File.ReadAllBytes(path);
        var first = adapter.Read();
        check(first.Availability == ConfigurationAvailability.Ready && first.Values["FutureScalar"]!.GetValue<string>() == "preserve", "Verified document loads string scalars and retains future fields");
        check(File.ReadAllBytes(path).SequenceEqual(before) && Directory.GetFiles(adapter.Folder).Length == 1, "Reads do not migrate, back up, normalize on disk, or mirror registry");
        var defaults = ScheduleSettings.Read(first);
        check(defaults == new ScheduleSettings(), "Schedule defaults match false/10/180/300/60/120");
        var saved = adapter.SaveScalars(first, new Dictionary<string, string> { ["GameLanguage"] = "en-US" });
        check(saved.Value("GameLanguage") == "en-US" && saved.Values["GameLanguage"]!.GetValueKind() == System.Text.Json.JsonValueKind.String, "Language save preserves JSON string type");
        check(JsonNode.DeepEquals(saved.Document!["FutureRoot"], original["FutureRoot"]) && saved.Value("FutureScalar") == "preserve", "Root and scalar extensions survive supported edits");
        check(File.ReadAllBytes(path + ".bak").SequenceEqual(before) && Directory.GetFiles(adapter.Folder, "settings.json.*.bak").Any(p => File.ReadAllBytes(p).SequenceEqual(before)), "Replacement backup and unique exact-byte backup precede writes");
        check(new LegionConfigurationService(root).Read().Value("GameLanguage") == "en-US", "Original configuration survives adapter restart");
        var fileCount = Directory.GetFiles(adapter.Folder).Length;
        adapter.SaveScalars(saved, new Dictionary<string, string> { ["GameLanguage"] = "en-US" });
        check(Directory.GetFiles(adapter.Folder).Length == fileCount, "Unchanged scalar makes no write or backup");

        var expected = adapter.Read();
        var external = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); external["Values"]!["UserLanguage"] = "en-US"; Write(external);
        var externalBytes = File.ReadAllBytes(path);
        Throws<ConfigurationConflictException>(() => adapter.SaveScalars(expected, new Dictionary<string, string> { ["GameLanguage"] = "ko-KR" }), "External edit rejects stale snapshot");
        check(File.ReadAllBytes(path).SequenceEqual(externalBytes), "Conflict preserves external edits");
        expected = adapter.Read();
        Throws<InvalidDataException>(() => adapter.SaveScalars(expected, new Dictionary<string, string> { ["UnverifiedCommand"] = "1" }), "Unknown scalar writes rejected");
        Throws<InvalidDataException>(() => adapter.SaveScalars(expected, new Dictionary<string, string> { ["AutoTask_Race"] = "0" }), "Unsupported race numbers are rejected");
        Throws<InvalidDataException>(() => adapter.SaveScalars(expected, new Dictionary<string, string> { ["GameLanguage"] = "English" }), "Display language names cannot leak into original schema");
        Throws<InvalidDataException>(() => adapter.SaveScalars(expected, new Dictionary<string, string> { ["AutoTask_ClearSide"] = "true" }), "AutoTask requires 0/1 strings");

        var session = new VerifiedSettingsSession(adapter, registry);
        var schedule = new ScheduleSettings(true, 16, 90, 90, 5, 10);
        session.SaveSchedule(schedule);
        check(ScheduleSettings.Read(adapter.Read()) == schedule && adapter.Read().Value("WorkSchedule_Enabled") == "true", "Schedule round trip uses invariant numbers and true/false strings; equal bounds allowed");
        Throws<InvalidDataException>(() => session.SaveSchedule(new(true, 17)), "Schedule rejects account maximum above 16");
        Throws<InvalidDataException>(() => session.SaveSchedule(new(true, 2, 500, 100)), "Schedule rejects reversed write range");
        Throws<InvalidDataException>(() => session.SaveSchedule(new(true, 2, 1, 10081)), "Schedule rejects duration above 10080");
        var skewed = Envelope(); skewed["Values"]!["WorkSchedule_MinWorkMinutes"] = "500"; skewed["Values"]!["WorkSchedule_MaxWorkMinutes"] = "20";
        skewed["Values"]!["WorkSchedule_MaxConcurrent"] = "99"; Write(skewed);
        var skewedBytes = File.ReadAllBytes(path); var normalized = ScheduleSettings.Read(adapter.Read());
        check(normalized.WorkMin == 20 && normalized.WorkMax == 500 && normalized.Accounts == 16 && File.ReadAllBytes(path).SequenceEqual(skewedBytes), "Schedule normalizes display values without touching source");

        var duplicateA = new WorkflowStep { Action = WorkflowAction.Gather, MinimumMinutes = 7, MaximumMinutes = 11, ResetTimer = true };
        var duplicateB = new WorkflowStep { Action = WorkflowAction.Gather, MinimumMinutes = 12, MaximumMinutes = 15 };
        var sw1 = new WorkflowStep { Action = WorkflowAction.SwitchCharacter, Enabled = false };
        var sw2 = new WorkflowStep { Action = WorkflowAction.SwitchCharacter };
        var plan = new WorkflowPlan { Steps = [sw1, duplicateA, sw2, duplicateB], Finish = CompletionAction.NextAccount };
        var adjusted = plan.Normalize();
        check(adjusted.Steps.Count == 3 && adjusted.Steps[0].Id == duplicateA.Id && adjusted.Steps[1].Id == duplicateB.Id && adjusted.Steps[^1].Id == sw1.Id && adjusted.Steps[^1].Enabled, "Workflow preserves ordinary duplicates/order and retains first switch enabled last");
        var serialized = adjusted.ToJson(); var roundtrip = WorkflowPlan.Read(serialized);
        check(roundtrip.Steps[0].MinimumMinutes == 7 && roundtrip.Steps[0].ResetTimer && roundtrip.Finish == CompletionAction.NextAccount && serialized["Steps"]![0]!["Kind"]!.GetValue<string>() == "Gather", "Workflow string enums and independent per-step options round trip");
        Throws<InvalidDataException>(() => (new WorkflowPlan { Steps = [duplicateA, duplicateA] }).Normalize(), "Duplicate workflow IDs rejected");
        Throws<InvalidDataException>(() => (new WorkflowPlan { Steps = [sw1] }).Normalize(), "Switch-only workflow rejected");
        Throws<InvalidDataException>(() => (new WorkflowPlan { Steps = [duplicateA with { Enabled = false, MaximumMinutes = 0 }] }).Normalize(), "Invalid disabled Gather duration rejected");
        Throws<InvalidDataException>(() => (new WorkflowPlan { Steps = Enumerable.Range(0, 101).Select(_ => new WorkflowStep()).ToArray() }).Normalize(), "Oversized workflow rejected");

        Write(original);
        expected = adapter.Read();
        var edited = expected.EffectiveWorkflow with { Finish = CompletionAction.NextAccount };
        var afterWorkflow = adapter.SaveWorkflow(expected, edited);
        check(afterWorkflow.StoredWorkflow.Revision != expected.StoredWorkflow.Revision && afterWorkflow.StoredWorkflow.Finish == CompletionAction.NextAccount, "Workflow edits regenerate revision and persist finish kind");
        check(afterWorkflow.Document!["Workflow"]!["FutureWorkflow"]!.GetValue<string>() == "keep" && afterWorkflow.Document["Workflow"]!["Steps"]![0]!["FutureStep"]!["unknown"]!.GetValue<bool>(), "Workflow and step extension properties survive UI-model updates");
        Throws<ConfigurationConflictException>(() => adapter.SaveWorkflow(expected, edited), "Stale workflow revision/fingerprint rejected");
        var unchangedRevision = afterWorkflow.StoredWorkflow.Revision;
        adapter.SaveWorkflow(afterWorkflow, afterWorkflow.EffectiveWorkflow);
        check(adapter.Read().StoredWorkflow.Revision == unchangedRevision, "Unchanged workflow retains revision");

        var oldPlan = new WorkflowPlan { Steps = [] };
        Write(Envelope(oldPlan, 0));
        var legacyBytes = File.ReadAllBytes(path); var legacy = adapter.Read();
        check(legacy.NeedsWorkflowMigration && legacy.EffectiveWorkflow.Steps.Select(s => s.Action).SequenceEqual(new[] { WorkflowAction.AutoTask, WorkflowAction.KraoDungeon, WorkflowAction.FloorGold, WorkflowAction.SwitchCharacter }) && File.ReadAllBytes(path).SequenceEqual(legacyBytes), "Legacy empty workflow migration is staged in memory only");
        var migrated = adapter.SaveWorkflow(legacy, legacy.EffectiveWorkflow);
        check(!migrated.NeedsWorkflowMigration && migrated.Document!["WorkflowDefaultVersion"]!.GetValue<int>() == 2 && File.ReadAllBytes(path + ".bak").SequenceEqual(legacyBytes), "Explicit Apply migrates default version with backup");
        var oldDefault = new WorkflowPlan { Steps = new[] { WorkflowAction.AutoTask, WorkflowAction.FloorGold, WorkflowAction.SwitchCharacter }.Select(a => new WorkflowStep { Action = a }).ToArray() };
        Write(Envelope(oldDefault, 1));
        check(adapter.Read().EffectiveWorkflow.Steps[1].Action == WorkflowAction.KraoDungeon, "Verified legacy default upgrades to Krao-inclusive default");
        Write(Envelope(new() { Steps = [duplicateA, sw1] }, 1));
        var custom = adapter.Read(); adapter.SaveWorkflow(custom, custom.EffectiveWorkflow);
        check(adapter.Read().StoredWorkflow.Steps[0].Id == duplicateA.Id, "Custom legacy workflow retains step identity on migration");

        var bad = Envelope(); bad["Values"]!["GameLanguage"] = 7; Write(bad);
        check(adapter.Read().Availability == ConfigurationAvailability.Invalid, "Non-string scalar configuration rejected without coercion");
        File.WriteAllText(path, "{\"FormatVersion\":1,\"FormatVersion\":1}");
        check(adapter.Read().Availability == ConfigurationAvailability.Invalid, "Duplicate JSON property names rejected");
        File.WriteAllText(path, "{broken");
        var brokenBytes = File.ReadAllBytes(path);
        check(adapter.Read().Availability == ConfigurationAvailability.Invalid && File.ReadAllBytes(path).SequenceEqual(brokenBytes), "Corrupted original JSON is retained without automatic recovery writes");
        Throws<InvalidDataException>(() => adapter.SaveScalars(adapter.Read(), new Dictionary<string, string> { ["GameLanguage"] = "en-US" }), "Corrupted document cannot be overwritten by defaults");
        bad = Envelope(); bad["FormatVersion"] = 2; Write(bad);
        check(adapter.Read().Availability == ConfigurationAvailability.Unsupported, "Future document format is read-only");
        bad = Envelope(); bad["WorkflowDefaultVersion"] = null; Write(bad);
        check(adapter.Read().Availability == ConfigurationAvailability.Invalid, "Null workflow migration version handled gracefully");
        Write(original); File.WriteAllText(path, File.ReadAllText(path), new UTF8Encoding(true));
        check(adapter.Read().Availability == ConfigurationAvailability.Ready, "UTF-8 BOM configuration supported");
        expected = adapter.Read(); File.SetAttributes(path, FileAttributes.ReadOnly);
        try { Throws<UnauthorizedAccessException>(() => adapter.SaveScalars(expected, new Dictionary<string, string> { ["GameLanguage"] = "en-US" }), "Read-only original refuses replacement"); }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        check(adapter.Read().Value("GameLanguage") == null && !Directory.GetFiles(adapter.Folder, "*.tmp").Any(), "Failed replacement leaves original and cleans temporary files");
        var failBackup = new LegionConfigurationService(root, new FailedBackup());
        Throws<IOException>(() => failBackup.SaveScalars(failBackup.Read(), new Dictionary<string, string> { ["GameLanguage"] = "en-US" }), "Backup failure aborts configuration write");
        check(adapter.Read().Value("GameLanguage") == null, "Backup failure preserves original document");

        var machineEnvelope = Envelope(new WorkflowPlan()); ((JsonObject)machineEnvelope["Values"]!).Remove("UserLanguage");
        File.WriteAllText(machinePath, machineEnvelope.ToJsonString());
        var machine = adapter.Read(true); var portableBefore = File.ReadAllBytes(path);
        adapter.SaveScalars(machine, new Dictionary<string, string> { ["GamePath_TW"] = @"X:\mock\AION2.exe" }, true);
        check(adapter.Read(true).Value("GamePath_TW") == @"X:\mock\AION2.exe" && File.ReadAllBytes(path).SequenceEqual(portableBefore), "Machine-path writes stay in machine document");
        Throws<InvalidDataException>(() => adapter.SaveScalars(adapter.Read(true), new Dictionary<string, string> { ["GameLanguage"] = "en-US" }, true), "Machine/portable scalar routing enforced");

        Write(original);
        session = new(adapter, registry);
        session.SaveAutoTask("SwitchToGatherAfterTasks", false);
        check(adapter.Read().Value("AutoTask_GatherAfterComplete") == "0" && registry.Read("AutoTask", "SwitchToGatherAfterTasks") == 0, "AutoTask writes JSON string and matching registry DWORD");
        check(adapter.Read().Value("UserLanguage") == "ko-KR" && registry.Read("AutoTask", "CharacterJob") == 777, "AutoTask mirrors preserve unrelated JSON and unknown class value");
        Throws<InvalidDataException>(() => session.SaveAutoTask("CharacterJob", true), "AutoTask boolean editor refuses class writes");
        registry.WriteVerified("AutoTask", "AutoDailyQuests", 1);
        check(registry.Read("AutoTask", "AutoDailyQuests") == 1, "Daily quests writes and verifies its independent DWORD");
        var failedMirror = new VerifiedSettingsSession(adapter, new DeniedRegistry());
        Throws<PartialSettingsWriteException>(() => failedMirror.SaveAutoTask("ClearSideQuestsBeforeDungeon", true), "Registry failure after JSON success reported as partial save");
        check(adapter.Read().Value("AutoTask_ClearSide") == "1", "Partial-save state is truthful: verified JSON remains persisted");
        session.Reload(); session.RetryMirror();
        check(registry.Read("AutoTask", "ClearSideQuestsBeforeDungeon") == 1, "Explicit mirror retry repairs boolean synchronization");
        check(registry.Read("AutoTask", "CharacterRace") == 2 && registry.Read("AutoTask", "CharacterJob") == 777,
            "Explicit raw mirror copies verified numeric default without interpreting or replacing unknown class values");
        Throws<InvalidDataException>(() => registry.MirrorAutoTaskNumber("CharacterJob", 1), "Numeric mirror refuses to overwrite an unknown existing class number");
        Throws<InvalidDataException>(() => session.SaveAutoTaskNumber("CharacterJob", 8), "Class editor preserves unknown registry encoding");
        session.SaveAutoTaskNumber("CharacterRace", 1);
        check(adapter.Read().Value("AutoTask_Race") == "1" && registry.Read("AutoTask", "CharacterRace") == 1,
            "Verified race choice persists to both stores");
        var verifiedClass = adapter.SaveScalars(adapter.Read(), new Dictionary<string, string> { ["AutoTask_Job"] = "8" });
        check(verifiedClass.Value("AutoTask_Job") == "8", "Verified class uses the installed wire value");
        Throws<InvalidDataException>(() => adapter.SaveScalars(verifiedClass,
            new Dictionary<string, string> { ["AutoTask_Job"] = "9" }), "Unknown class choice is rejected");
        Throws<InvalidDataException>(() => session.SaveAutoTaskNumber("CharacterRace", 0), "Unknown race choice is rejected");
        Throws<InvalidDataException>(() => registry.WriteVerified("MiniGame", "AutoEnter", 1), "Unsupported registry data type cannot be overwritten by boolean writes");
        Throws<InvalidDataException>(() => registry.WriteVerified("Enhance", "AutoEnchantMaxPower", 1099), "Enhancement cap below verified range rejected");
        Throws<InvalidDataException>(() => registry.WriteVerified("Enhance", "AutoEnchantMaxPower", 1501), "Enhancement cap above verified range rejected");

        var own = new JsonConfigurationService(Path.Combine(root, "hexor.json"), new SettingsBackupService());
        var tracker = new TrackingRegistry();
        using (var startup = new MainViewModel(own, tracker, adapter))
        {
            check(tracker.Writes == 0, "Startup does not write when an injected registry provider refuses initialization");
            check(startup.Toggles.Where(t => t.Name is "AutoEnter" or "UseTaiwanVillageRules").All(t => t.Value == false),
                "Mini Game missing-value UI fallbacks are OFF");
            check(startup.Toggles.Where(t => t.Section == "Enhance").All(t => t.Value == false) && startup.EnhanceCap == "1400", "Enhancement Boolean missing-value UI fallbacks are OFF");
        }
        var productionBytes = File.ReadAllBytes(path);
        using var vm = new MainViewModel(own, registry, adapter);
        check(File.ReadAllBytes(path).SequenceEqual(productionBytes), "View-model startup and integration loading never write original settings");
        check(vm.OriginalConfigurationReady && vm.Toggles.Single(t => t.Name == "AutoDailyQuests").Value == true, "Verified UI session exposes the saved daily quests DWORD");
        check(vm.Toggles.Single(t => t.Name == "SwitchToGatherAfterTasks").Value == false, "AutoTask UI binds to the canonical registry state");
        var selected = vm.Preferences.Instructions[0]; vm.SelectedInstruction = selected; vm.DuplicateCommand.Execute(null);
        check(vm.SelectedInstruction!.Id != selected.Id, "Instruction duplicate receives a fresh persistent ID");
        vm.Preferences.AfterFinish = "NextAccount"; vm.SaveDaily();
        check(adapter.Read().StoredWorkflow.Finish == CompletionAction.NextAccount, "Existing Daily UI Apply persists compatible workflow");
        check(adapter.Read().StoredWorkflow.Steps[0].Preserved["FutureStep"]?["unknown"]?.GetValue<bool>() == true
            && adapter.Read().StoredWorkflow.Steps[1].Preserved["FutureStep"]?["unknown"]?.GetValue<bool>() == true,
            "Duplicating an instruction preserves extension fields in both copies");
        var workflowExport = Path.Combine(root, "workflow-export.json");
        var afterApply = File.ReadAllBytes(path);
        vm.ExportWorkflow(workflowExport);
        check(WorkflowPlan.Read(JsonNode.Parse(File.ReadAllText(workflowExport))!.AsObject()).Finish == CompletionAction.NextAccount,
            "Workflow export contains the draft and its completion action");
        vm.LoadDefaultWorkflow();
        check(vm.Preferences.AfterFinish == "GatherOnCharacter", "Default sequence replaces only the draft");
        vm.ImportWorkflow(workflowExport);
        check(vm.Preferences.AfterFinish == "NextAccount" && File.ReadAllBytes(path).SequenceEqual(afterApply),
            "Workflow round trip stages changes without touching provider settings");
        vm.ExportWorkflow(path);
        check(File.ReadAllBytes(path).SequenceEqual(afterApply), "Export refuses to overwrite an existing settings file");
        var badWorkflow = Path.Combine(root, "invalid-workflow.json");
        File.WriteAllText(badWorkflow, "{\"Workflow\":false}");
        vm.ImportWorkflow(badWorkflow);
        check(vm.Preferences.AfterFinish == "NextAccount" && vm.Status.Contains("Choose a workflow"),
            "Invalid workflow import leaves the draft intact");
        vm.MaxAccounts = "4"; vm.WorkMin = "50"; vm.WorkMax = "70"; vm.RestMin = "5"; vm.RestMax = "10"; vm.SaveSchedule();
        check(ScheduleSettings.Read(adapter.Read()).Accounts == 4 && vm.Status.Contains("persisted and verified"), "Schedule UI persists while reporting when the saved schedule applies");
        check(vm.SetGameLanguage("English") && adapter.Read().Value("GameLanguage") == "en-US", "General game-language UI persists verified culture identifier");
        vm.ReloadConfiguration();
        check(File.ReadAllBytes(path).Length > 0 && vm.Preferences.Instructions.All(i => !string.IsNullOrWhiteSpace(i.Id)), "Explicit reload retains persisted instruction IDs");
        var localLegacy = Path.Combine(root, "legacy-hexor.json");
        File.WriteAllText(localLegacy, "{\"SchemaVersion\":1,\"GatherMin\":25,\"GatherMax\":35,\"ResetTimer\":true,\"Instructions\":[{\"Type\":\"Gathering\"}]}");
        var ownLegacy = new JsonConfigurationService(localLegacy, new SettingsBackupService()); var legacyLocalBytes = File.ReadAllBytes(localLegacy);
        var migratedLocal = ownLegacy.Load();
        check(migratedLocal.Instructions[0].MinimumMinutes == 25 && migratedLocal.Instructions[0].MaximumMinutes == 35
            && migratedLocal.Instructions[0].ResetTimer && File.ReadAllBytes(localLegacy).SequenceEqual(legacyLocalBytes), "Legacy Hexor draft durations migrate in memory without rewriting the file");
        ownLegacy.Save(migratedLocal);
        check(ownLegacy.Load().Instructions[0].MaximumMinutes == 35 && Directory.GetFiles(root, "legacy-hexor.json.*.bak").Length == 1,
            "Explicit legacy Hexor draft save persists migration with backup");
    }

    private sealed class FailedBackup : ISettingsBackupService { public string Backup(string path) => throw new IOException("Test backup failure"); }
    private sealed class DeniedRegistry : IRegistrySettingsService
    { public int? Read(string section, string name) => null; public void WriteVerified(string section, string name, int value) => throw new UnauthorizedAccessException(); }
    private sealed class TrackingRegistry : IRegistrySettingsService
    { public int Writes { get; private set; } public int? Read(string section, string name) => null; public void WriteVerified(string section, string name, int value) => Writes++; }
}
