using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hexor;
using Hexor.Compatibility;
using Hexor.Runtime;

internal static class RuntimeDisplayTests
{
    public static void Run(string root, IRegistrySettingsService registry, Action<bool, string> check)
    {
        var drives = new[] { Path.Combine(root, "drive-D"), Path.Combine(root, "drive-C") };
        foreach (var drive in drives) Directory.CreateDirectory(drive);
        const string machineId = "fixture-machine-id";
        var actual = Path.Combine(drives[0], machineId);
        var stale = Path.Combine(drives[1], machineId);
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(Path.Combine(actual, "config"));
        Directory.CreateDirectory(Path.Combine(actual, "db"));
        var settingsPath = Path.Combine(actual, "config", "settings.json");
        var machinePath = Path.Combine(actual, "config", "machine.json");
        var dbPath = Path.Combine(actual, "db", "accounts.db");
        var processPath = Path.Combine(root, "Steam", "steamapps", "common", "AION2", "Aion2", "Binaries", "Win64", "Aion2.exe");
        JsonObject Envelope(WorkflowPlan plan) => new() { ["FormatVersion"] = 1, ["Values"] = new JsonObject(), ["Workflow"] = plan.ToJson(), ["WorkflowDefaultVersion"] = 2 };
        var portable = Envelope(new WorkflowPlan
        {
            Steps = [new() { Id = "2147", Action = WorkflowAction.Gather }, new() { Id = "4836", Action = WorkflowAction.FloorGold }, new() { Id = "47", Action = WorkflowAction.SwitchCharacter }]
        });
        var machine = Envelope(new()); machine["Values"]!["GamePath_Global"] = processPath;
        File.WriteAllText(settingsPath, portable.ToJsonString()); File.WriteAllText(machinePath, machine.ToJsonString()); File.WriteAllText(dbPath, "availability-only fixture");
        var originalBytes = File.ReadAllBytes(settingsPath);
        var inspector = new ConfigurationInspector();
        var candidates = LegionConfigurationLocator.Candidates(stale, machineId, drives);
        check(candidates.Contains(stale) && candidates.Contains(actual), "Stale saved folder does not suppress verified metadata candidates");
        var resolution = new ConfigurationResolver(inspector).Resolve(candidates, stale);
        check(resolution.Selected?.DataRoot == actual && resolution.Note.Contains("Saved folder"), "Sole portable root is found despite stale C-style character directory");
        var inspection = resolution.Selected!;
        check(inspection.Portable.Availability == ConfigurationAvailability.Ready && inspection.Machine.Availability == ConfigurationAvailability.Ready
            && inspection.Database.Availability == ConfigurationAvailability.Ready, "Discovery checks settings.json, machine.json and accounts.db availability");
        check(inspector.Inspect(Path.Combine(actual, "config") + Path.DirectorySeparatorChar).DataRoot == actual, "Config subfolder with trailing separator resolves to the same data root");
        check(File.ReadAllBytes(settingsPath).SequenceEqual(originalBytes) && Directory.GetFiles(Path.Combine(actual, "config")).Length == 2,
            "Discovery and availability probes do not create, normalize or back up customer files");

        var now = DateTimeOffset.Parse("2026-10-21T01:00:00Z");
        var process = new GameProcessObservation(1234, processPath, now.AddHours(-1));
        var games = new GameInstallationDiscoveryService();
        Detection Detect(ProcessObservationBatch batch, IReadOnlyIdentitySource? identity = null) =>
            new ReadOnlyRuntimeDetector(games, new FakeProcesses(batch), identity ?? new UnavailableIdentitySource(), () => now).DetectAsync().GetAwaiter().GetResult();
        var running = Detect(new([process], []));
        check(running.Process == "Running" && running.ProcessId == 1234 && running.ExecutablePath == processPath && running.Client == "Steam",
            "Mock Steam runtime preserves PID/path and Running status");
        check(running.Character == "Character not detected" && running.Server == "Server not detected" && running.CharacterSource.Contains("no authorized"),
            "Running process and client never fabricate live identity");
        check(inspection.Association(running).StartsWith("Installation path matches") && inspection.Association(running).Contains("active account/character association remains unverified"),
            "Exact configured executable match establishes installation only, not a character profile");
        check(inspection.Association(running with { ExecutablePath = processPath + ".other" }).StartsWith("Not associated"), "Similar executable path cannot establish configuration association");
        check(inspection.Association(running with { ExecutablePath = null }).Contains("inaccessible"), "Unreadable executable metadata is not treated as a path match");
        var notRunning = Detect(new([], []));
        check(notRunning.Process == "Not detected" && inspection.Association(notRunning).StartsWith("No running game"), "Game not running is distinct from configuration not found");
        var inaccessible = Detect(new([process with { ExecutablePath = null, Error = "Executable metadata unavailable." }], []));
        check(inaccessible.Process == "Running" && inaccessible.ProcessId == 1234 && inaccessible.Errors.Count == 1 && inaccessible.Client.StartsWith("Unknown"), "Access-denied metadata preserves process-running observation and diagnostics");
        var identity = new LiveIdentity(process.Id, processPath, process.StartedAt!.Value, now, true, "Fixture character", "Fixture server", 123);
        var unauthorized = new FakeIdentity(identity, false);
        _ = Detect(new([process], []), unauthorized);
        check(unauthorized.Reads == 0, "Unauthorized identity provider is never invoked");
        var approvedMock = new FakeIdentity(identity, true);
        var simulated = Detect(new([process], []), approvedMock);
        check(simulated.Character == "Fixture character" && simulated.Server == "Fixture server", "Mock-only authorized identity requires matching live process metadata");
        var staleIdentity = Detect(new([process], []), new FakeIdentity(identity with { CapturedAt = now.AddSeconds(-21) }, true));
        check(staleIdentity.Character == "Character not detected" && staleIdentity.Errors.Any(e => e.Contains("rejected")), "Stale snapshot cannot populate character/server fields");
        check(Detect(new([process], []), new FakeIdentity(identity with { ProcessId = 999 }, true)).Character == "Character not detected", "Mismatched PID cannot populate identity");
        check(Detect(new([process], []), new FakeIdentity(identity with { ProcessStartedAt = now }, true)).Character == "Character not detected", "Reused PID with different process start time cannot reuse an identity");
        check(Detect(new([process], []), new FakeIdentity(identity with { ExecutablePath = processPath + ".other" }, true)).Server == "Server not detected", "Snapshot from another executable is rejected");
        check(Detect(new([process], []), new FakeIdentity(identity with { InGame = false }, true)).Character == "Character not detected", "Out-of-game snapshot cannot claim an active character");
        var multiProvider = new FakeIdentity(identity, true);
        var multiple = Detect(new([process, process with { Id = 9876 }], []), multiProvider);
        check(multiple.ProcessId == null && multiple.Processes.Count == 2 && multiProvider.Reads == 0 && multiple.Character == "Character not detected", "Multiple clients do not select an arbitrary active character");
        var failure = new ReadOnlyRuntimeDetector(games, new ThrowingProcesses(), new UnavailableIdentitySource()).DetectAsync().GetAwaiter().GetResult();
        check(failure.Process == "Detection unavailable" && !string.Join(" ", failure.Errors).Contains("secret"), "Runtime failures expose error category, not exception contents");
        check(Detect(new([], []), approvedMock).Character == "Character not detected", "Process exit clears earlier identity rather than reusing it");
        check(!running.RuntimeIdentityAvailable && running.LastSuccessfulIdentityUpdate == null && running.IdentityStatus.Contains("no authorized"), "Production provider reports unavailable runtime identity and no fabricated update time");
        var characterOnly = Detect(new([process], []), new FakeIdentity(identity with { ServerName = null, ServerId = null }, true));
        check(characterOnly.Character == "Fixture character" && characterOnly.Server == "Server not detected" && characterOnly.RuntimeIdentityAvailable, "Character can resolve independently of server");
        var serverOnly = Detect(new([process], []), new FakeIdentity(identity with { Character = "\u0001\u0002", ServerName = null }, true));
        check(serverOnly.Character == "Character not detected" && serverOnly.Server.StartsWith("Server ID 123") && serverOnly.RuntimeIdentityAvailable, "Server resolves independently; control-only character is unavailable");
        check(simulated.LastSuccessfulIdentityUpdate == now && !staleIdentity.RuntimeIdentityAvailable && staleIdentity.LastSuccessfulIdentityUpdate == null, "Only accepted current identity records a successful update");
        File.Delete(dbPath);
        check(inspector.Inspect(actual).Association(simulated).StartsWith("Installation path matches") && simulated.Character == "Fixture character", "Account database presence is not required for installation association or provider identity");
        File.WriteAllText(dbPath, "availability-only fixture");
        var movingProcesses = new MutableProcesses(new([process], []));
        var movingIdentity = new MutableIdentity(identity);
        var detector = new ReadOnlyRuntimeDetector(games, movingProcesses, movingIdentity, () => now);
        Detection Poll() => detector.DetectAsync().GetAwaiter().GetResult();
        _ = Poll(); movingIdentity.Data = identity with { InGame = false };
        var loggedOut = Poll();
        check(!loggedOut.RuntimeIdentityAvailable && loggedOut.Character == "Character not detected" && loggedOut.Server == "Server not detected" && loggedOut.LastSuccessfulIdentityUpdate == now, "Logout clears identity while retaining only an explicitly historical success timestamp");
        movingProcesses.Batch = new([process with { Id = 4321, StartedAt = now }], []);
        movingIdentity.Data = identity;
        check(!Poll().RuntimeIdentityAvailable, "Game restart rejects previous PID identity on the same detector");
        movingIdentity.Data = identity with { ProcessId = 4321, ProcessStartedAt = now, Character = "Second fixture" };
        check(Poll().Character == "Second fixture", "New PID and start time resolve dynamically without registered accounts");
        movingIdentity.Data = movingIdentity.Data with { Character = "Changed fixture" };
        check(Poll().Character == "Changed fixture", "Character selection refreshes name rather than keeping previous character");
        movingIdentity.Data = null;
        check(!Poll().RuntimeIdentityAvailable && Poll().Character == "Character not detected", "Unavailable provider data never reuses cached identity");
        movingProcesses.Batch = new([], []);
        check(Poll().Server == "Server not detected", "Process exit clears server on the same detector");
        var emptyIdentity = Detect(new([process], []), new FakeIdentity(identity with { Character = null, ServerName = null, ServerId = 0 }, true));
        check(!emptyIdentity.RuntimeIdentityAvailable && emptyIdentity.LastSuccessfulIdentityUpdate == null, "Empty identity does not count as successful resolution");
        check(!Detect(new([process], []), new FakeIdentity(identity with { CapturedAt = now.AddSeconds(21) }, true)).RuntimeIdentityAvailable, "Future-dated snapshot beyond freshness window is rejected");

        File.WriteAllText(settingsPath, "{broken");
        check(inspector.Inspect(actual).Summary == "Configuration invalid", "Corrupt configuration is distinguished from missing");
        var invalidChoice = new ConfigurationResolver(inspector).Resolve(candidates, actual);
        check(invalidChoice.Selected?.DataRoot == actual && invalidChoice.Selected.Portable.Availability == ConfigurationAvailability.Invalid,
            "Explicit invalid profile is retained for diagnosis rather than silently replaced");
        File.WriteAllText(settingsPath, portable.ToJsonString());
        using (var held = new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            check(new LegionConfigurationService(actual).Read().Availability == ConfigurationAvailability.Unavailable && inspector.Inspect(actual).Summary == "Configuration inaccessible",
                "Locked/inaccessible settings are reported as inaccessible, not absent");
        }
        File.Delete(settingsPath);
        check(inspector.Inspect(actual).Summary == "Configuration not found" && inspector.Inspect(actual).Database.Availability == ConfigurationAvailability.Ready,
            "Database existence alone never claims portable configuration connected");
        File.WriteAllText(settingsPath, portable.ToJsonString());
        var other = Path.Combine(root, "other-profile"); Directory.CreateDirectory(Path.Combine(other, "config"));
        File.WriteAllText(Path.Combine(other, "config", "settings.json"), portable.ToJsonString());
        check(new ConfigurationResolver(inspector).Resolve([actual, other], null).Selected == null, "Multiple valid roots require explicit selection");
        check(new ConfigurationResolver(inspector).Resolve([actual, other], other).Selected?.DataRoot == other, "Explicit valid selection takes precedence among multiple roots");
        var missingMachine = inspector.Inspect(other);
        check(missingMachine.Portable.Availability == ConfigurationAvailability.Ready && missingMachine.Machine.Availability == ConfigurationAvailability.Missing
            && missingMachine.Association(running).StartsWith("Not associated"), "Found portable file with missing machine paths remains distinct from process association");
        var fakeInspector = new DelegatedInspector(path => path == other ? inspection with
        { DataRoot = other, Portable = new(Path.Combine(other, "config", "settings.json"), ConfigurationAvailability.Unavailable, "Permission denied.") } : inspection);
        check(new ConfigurationResolver(fakeInspector).Resolve([actual, other], null).Selected == null,
            "An inaccessible alternative prevents silently picking a potentially wrong profile");

        var local = new JsonConfigurationService(Path.Combine(root, "runtime-preferences.json"), new SettingsBackupService());
        var preferences = local.Load(); preferences.ConfigurationFolder = stale; local.Save(preferences);
        var localBefore = File.ReadAllBytes(Path.Combine(root, "runtime-preferences.json"));
        var runtime = new FakeDetector(running);
        using var vm = new MainViewModel(local, registry, runtimeDetector: runtime, configurationCandidates: () => candidates);
        vm.DiscoverAsync().GetAwaiter().GetResult(); vm.RefreshDetectionAsync().GetAwaiter().GetResult();
        check(vm.ConfigurationStatus.StartsWith("Configuration found") && vm.DiagnosticsDataRoot == actual && vm.OriginalConfigurationReady, "View model recovers a stale preferred folder without lying about file availability");
        check(vm.Preferences.ConfigurationFolder == stale && File.ReadAllBytes(Path.Combine(root, "runtime-preferences.json")).SequenceEqual(localBefore), "Discovery recovery does not rewrite the saved preference automatically");
        check(vm.Preferences.Instructions.Select(i => i.Id).SequenceEqual(new[] { "2147", "4836", "47" }) && vm.Preferences.Instructions.Select(i => i.SequenceNumber).SequenceEqual(new[] { 1, 2, 3 }),
            "Numeric-looking workflow IDs are retained but never used as row ordinals");
        vm.SelectedInstruction = vm.Preferences.Instructions[0]; vm.DuplicateCommand.Execute(null); vm.DownCommand.Execute(null);
        check(vm.Preferences.Instructions.Select(i => i.SequenceNumber).SequenceEqual(Enumerable.Range(1, 4)), "Rows renumber deterministically after duplicate and move");
        var draftOrder = vm.Preferences.Instructions.Select(i => i.Id).ToArray();
        vm.RefreshDetectionAsync().GetAwaiter().GetResult();
        check(vm.Preferences.Instructions.Select(i => i.Id).SequenceEqual(draftOrder) && File.ReadAllBytes(settingsPath).SequenceEqual(originalBytes),
            "Runtime/availability refresh does not discard workflow edits or change stored ordering");
        check(!vm.DiagnosticsErrors.Contains("secret") && vm.DiagnosticsProcess.Contains("1234") && vm.DiagnosticsProcess.Contains(processPath)
            && vm.DiagnosticsFiles.Contains("accounts.db: Found"), "Optional diagnostics expose only explicit process/file fields");
        runtime.Result = running with { ExecutablePath = processPath + ".other" }; vm.RefreshDetectionAsync().GetAwaiter().GetResult();
        check(vm.ConfigurationStatus.Contains("process not associated") && vm.ConfigurationStatus.StartsWith("Configuration found"), "Configuration found and process not associated are separate states");
        var beforeMutation = File.ReadAllBytes(settingsPath); File.WriteAllText(settingsPath, "{invalid"); vm.RefreshDetectionAsync().GetAwaiter().GetResult();
        check(vm.ConfigurationStatus == "Configuration invalid" && !vm.OriginalConfigurationReady, "Background availability refresh invalidates editing readiness without replacing the file");
        File.WriteAllBytes(settingsPath, beforeMutation);
        machine["FutureMachineField"] = new JsonObject { ["keep"] = true };
        machine["Values"]!["GamePath_Global"] = "";
        machine["Values"]!["GamePath_KR"] = "unchanged-korean-path";
        File.WriteAllText(machinePath,machine.ToJsonString());
        var machineBefore = File.ReadAllBytes(machinePath);
        var profileJson = new JsonConfigurationService(Path.Combine(root,"manual-profiles.json"),new SettingsBackupService());
        var profileRuntime = new FakeDetector(running);
        using var profileVm = new MainViewModel(profileJson,registry,new LegionConfigurationService(actual),profileRuntime);
        profileVm.RefreshDetectionAsync().GetAwaiter().GetResult();
        var proposal = profileVm.ProposeDetectedPath("GamePath_Global");
        check(proposal?.ProcessId == 1234 && proposal.ExecutablePath == processPath && File.ReadAllBytes(machinePath).SequenceEqual(machineBefore), "Detected path review stages a proposal without writing machine.json");
        check(profileVm.ConfigurationStatus.Contains("process not associated"), "Unconfigured executable shows association warning before confirmation");
        check(profileVm.ConfirmDetectedPath(proposal!), "Explicit confirmation saves detected process path");
        var savedMachine = new LegionConfigurationService(actual).Read(true);
        check(savedMachine.Value("GamePath_Global") == processPath && savedMachine.Value("GamePath_KR") == "unchanged-korean-path"
            && JsonNode.Parse(File.ReadAllText(machinePath))!["FutureMachineField"]!["keep"]!.GetValue<bool>(), "Confirmed association preserves unrelated paths and unknown machine properties");
        check(File.ReadAllBytes(machinePath+".bak").SequenceEqual(machineBefore), "Detected path association backs up exact previous machine.json bytes");
        check(!profileVm.ConfigurationStatus.Contains("process not associated") && profileVm.DiagnosticsAssociation.StartsWith("Installation path matches")
            && !profileVm.Detection.RuntimeIdentityAvailable, "Confirmed installation clears association warning without claiming live identity");
        check(!profileVm.ConfirmDetectedPath(proposal!), "Previously confirmed proposal cannot overwrite a changed machine snapshot");
        var restartProposal = profileVm.ProposeDetectedPath("GamePath_Global")!;
        profileRuntime.Result = running with { Processes = [process with { Id = 5555 }], ProcessId = 5555 };
        profileVm.RefreshDetectionAsync().GetAwaiter().GetResult();
        check(!profileVm.ConfirmDetectedPath(restartProposal), "Changed process PID invalidates pending confirmation");
        profileRuntime.Result = running; profileVm.RefreshDetectionAsync().GetAwaiter().GetResult();
        var conflictProposal = profileVm.ProposeDetectedPath("GamePath_Global")!;
        File.AppendAllText(machinePath," ");
        check(!profileVm.ConfirmDetectedPath(conflictProposal), "External machine.json edit rejects confirmation rather than overwriting it");
        check(profileVm.ProposeDetectedPath("UnverifiedPath") == null, "Unverified destination keys cannot receive detected paths");
        profileVm.ManualCharacterName = "Manual fixture"; profileVm.ManualServer = "User server";
        check(profileVm.AddManualProfile() && profileVm.ManualProfileStatus.Contains("User configured") && profileVm.ManualProfiles.Count == 1, "Manual profile is added with explicit source labeling");
        check(profileVm.Detection.Character == "Character not detected" && profileVm.Detection.Server == "Server not detected" && !profileVm.Detection.RuntimeIdentityAvailable,
            "Manual profile never populates automatically detected identity");
        using var reloadedProfiles = new MainViewModel(profileJson,registry);
        check(reloadedProfiles.SelectedManualProfile?.Character == "Manual fixture" && reloadedProfiles.SelectedManualProfile.Server == "User server", "Manual profile and selection persist across view model restart");
        reloadedProfiles.SelectedManualProfile = null;
        check(profileJson.Load().SelectedManualProfileId == null && reloadedProfiles.ManualProfiles.Count == 1, "Clear selection persists without deleting profile");
        reloadedProfiles.SelectedManualProfile = reloadedProfiles.ManualProfiles[0]; reloadedProfiles.RemoveManualProfile();
        check(profileJson.Load().ManualProfiles.Count == 0 && reloadedProfiles.SelectedManualProfile == null, "Manual profile removal persists independently of original configuration");
        profileVm.ManualCharacterName = "\u0001";
        check(!profileVm.AddManualProfile(), "Control characters in manual profiles are rejected");
    }

    public static void RunWpf(MainWindow window, Action<bool, string> check)
    {
        var vm=window.Model;
        check(window.WindowStyle==WindowStyle.None && System.Windows.Shell.WindowChrome.GetWindowChrome(window)?.CaptionHeight==46,
            "Supported WindowChrome provides dark custom caption without a native title strip");
        var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var captionPoint=window.PointToScreen(new Point(150,22));
        var location=(nint)(((int)captionPoint.Y<<16)|((int)captionPoint.X&0xffff));
        TestCheckpoint.Write("WPF: native caption hit test");
        check(SendMessage(handle,0x0084,0,location)==2,"Native caption hit testing supports Windows window dragging");
        check(Find<Grid>(window,"HexorTitleBar")!=null && Find<Button>(window,"CloseWindowButton")!=null,"Custom title and accessible close control render");
        var minimize=Find<Button>(window,"MinimizeWindowButton")!;
        check(System.Windows.Automation.AutomationProperties.GetName(minimize)=="Minimize window" && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(minimize),"Caption button is accessible and clickable within window chrome");
        TestCheckpoint.Write("WPF: minimize caption click");minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(WaitFor(()=>window.WindowState==WindowState.Minimized,TimeSpan.FromSeconds(2)),"Custom minimize button minimizes the real WPF window");
        TestCheckpoint.Write("WPF: native restore");check(ShowWindowAsync(handle,9),"Native restore request is accepted for the real WPF window");
        check(WaitFor(()=>window.WindowState==WindowState.Normal,TimeSpan.FromSeconds(2)),"Restoring returns the real WPF window to normal state within the dispatcher timeout");
        check(window.WindowState==WindowState.Normal && window.ResizeMode==ResizeMode.CanResize,"Restoring retains native resizing");
        check(window.ResizeMode==ResizeMode.CanResize && window.WindowStartupLocation==WindowStartupLocation.CenterScreen,
            "Customer window is centered and supports native resize/maximize");
        check(window.MinWidth>=920 && window.MinHeight>=620,"Responsive shell has safe minimum dimensions");
        var nav=Find<StackPanel>(window,"SidebarNavigation")!;
        var expected=new[]{"General","Resource Grab","Auto Dungeon","Auto Task","Character Enhancement","Daily Activities","Work Schedule","Shugo Festival","CAPTCHA","Live Control"};
        check(nav.Children.Count==expected.Length && nav.Children.Cast<Button>().SelectMany(b=>All<TextBlock>(b)).Select(t=>t.Text)
            .SequenceEqual(expected),"Customer navigation includes both LegionCommander editors and live control");
        check(nav.Children.Cast<Button>().SelectMany(b=>All<TextBlock>(b)).All(t=>t.TextWrapping==TextWrapping.NoWrap),"Sidebar navigation labels remain on one line");
        var ids=vm.Preferences.Instructions.Select(i=>i.Id).ToArray();
        var workflow=System.Text.Json.JsonSerializer.Serialize(vm.Preferences.Instructions);
        for(int tab=0;tab<expected.Length;tab++)
        {
            Navigate(window,tab,"customer controls");
            var texts=All<TextBlock>(window).Select(t=>t.Text).ToArray();
            check(!texts.Any(t=>t.Contains("Stored CharacterJob")||t.Contains("Unverified enum")),"No raw identity labels on customer tab "+tab);
            check(!All<Button>(window).Any(b=>Equals(b.Content,"Add account")),"Account creation absent from tab "+tab);
            check(!All<Button>(window).Any(b=>b.Content is string text && (text.StartsWith("Start automation")||text.StartsWith("Stop automation"))),"No remote control buttons on tab "+tab);
            var scroll=Find<ScrollViewer>(window,"PageScroll")!;
            check(scroll.ActualHeight>100 && scroll.HorizontalScrollBarVisibility==ScrollBarVisibility.Disabled && scroll.ExtentWidth<=scroll.ViewportWidth+1,
                "Tab has bounded internal vertical scrolling without horizontal overflow "+tab);
            var controls=All<Control>(window).Where(c=>c.IsVisible && c is (TextBox or ComboBox)).ToArray();
            check(controls.All(c=>c.ActualHeight>=39 && c.ActualWidth>0),"Inputs have consistent usable minimum heights on tab "+tab);
        }
        Navigate(window,5,"Daily Activities");
        var steps = Find<ListBox>(window,"DailyWorkflowSteps")!;
        check(steps.Items.Count==vm.Preferences.Instructions.Count && Find<Button>(window,"ApplyDailyWorkflow")?.Command==vm.SaveDailyCommand,
            "Daily Activities binds the working sequence and verified apply command");
        var previousWidth=window.Width;
        window.Width=window.MinWidth;
        window.UpdateLayout();
        var narrowPage=Find<ScrollViewer>(window,"PageScroll")!;
        check(narrowPage.ExtentWidth<=narrowPage.ViewportWidth+1,
            "The two-column workflow editor fits the minimum supported width");
        CaptureFeature(window,"daily-activities");
        window.Width=previousWidth;
        Navigate(window,6,"Work Schedule");
        check(Find<Button>(window,"SaveWorkSchedule")?.Command==vm.SaveScheduleCommand,
            "Work Schedule binds the verified schedule command");
        CaptureFeature(window,"work-schedule");
        Navigate(window,3,"Auto Task",pump:false);
        check(All<CheckBox>(window).Select(c=>c.DataContext).OfType<RegistryToggle>().Where(t=>t.Section=="AutoTask").Select(t=>t.Name).SequenceEqual(new[]{"ClearSideQuestsBeforeDungeon","ExploreCavesBeforeDungeon","SwitchToGatherAfterTasks","AutoDailyQuests"}),"Auto Task exposes each of the four registry behavior options exactly once");
        var daily=All<CheckBox>(window).Single(c=>c.DataContext is RegistryToggle t&&t.Name=="AutoDailyQuests");
        check(daily.IsEnabled,"Daily quests uses an enabled checkbox control");
        daily.IsChecked=true;window.UpdateLayout();
        check(vm.Registry.Read("AutoTask","AutoDailyQuests")==1,"Actual daily quest control writes DWORD ON");
        daily.IsChecked=false;window.UpdateLayout();
        check(vm.Registry.Read("AutoTask","AutoDailyQuests")==0,"Actual daily quest control writes DWORD OFF");
        var raceSelector = Find<ComboBox>(window,"CharacterRaceSelector")!;
        var classSelector = Find<ComboBox>(window,"CharacterJobSelector")!;
        check(raceSelector.Items.Cast<object>().Select(item=>item.ToString()).SequenceEqual(new[]{"Elyos","Asmodian"})
            && classSelector.Items.Count==9 && classSelector.Items[8].ToString()=="Chanter",
            "Race and class show the installed LegionCommander mappings");
        check(All<ComboBox>(window).Select(c=>c.Name).SequenceEqual(new[]{"CharacterRaceSelector","CharacterJobSelector"}),"Auto Task exposes the verified character-default selectors");

        Navigate(window,1,"Resource Grab",pump:false);
        check(All<CheckBox>(window).Select(c=>c.DataContext).OfType<RegistryToggle>().Where(t=>t.Section=="ResourceGrab").Select(t=>t.Name).SequenceEqual(new[]{"AutoEnterAbyss","AbyssCounterAttack","GatherQualityFilterEnabled","GatherIgnoreAether","GuardianLogoutAvoidance","AuctionAutoSell"}),"Resource Grab exposes each verified Boolean exactly once");
        foreach(var name in new[]{"GatherQualityFilterEnabled","GatherIgnoreAether"})
        {
            var toggle=All<CheckBox>(window).Single(c=>c.DataContext is RegistryToggle t&&t.Name==name);
            toggle.IsChecked=true;window.UpdateLayout();
            check(vm.Registry.Read("ResourceGrab",name)==1,"Actual resource toggle writes exact DWORD: "+name);
            toggle.IsChecked=false;window.UpdateLayout();
            check(vm.Registry.Read("ResourceGrab",name)==0,"Actual resource toggle persists OFF: "+name);
        }
        var gatheringList=Find<ItemsControl>(window,"GatheringResources")!;
        var gatheringScroll=Find<ScrollViewer>(window,"GatheringResourceScroll")!;
        check(gatheringList.Items.Count==24&&gatheringScroll.MaxHeight==320&&gatheringScroll.HorizontalScrollBarVisibility==ScrollBarVisibility.Disabled,"Gathering renders all 24 resources in a bounded scrollable panel");
        var firstResource=All<CheckBox>(window).Single(c=>c.DataContext is GatheringResourceViewModel row&&row.Identifier=="隱藏背包");
        var originalGathering=vm.Registry.ReadGathering();firstResource.IsChecked=false;window.UpdateLayout();
        check(vm.Registry.ReadGathering().Targets.Value=="0"+originalGathering.Targets.Value![1..],"Actual gathering toggle changes only its own original Chinese flag entry");
        firstResource.IsChecked=true;window.UpdateLayout();
        check(vm.Registry.ReadGathering()==originalGathering,"Actual gathering toggle round-trips the saved Unicode REG_SZ exactly");
        var qualityWhite=All<RadioButton>(window).Single(r=>r.Name=="GatherQualityWhite");
        var qualityGreen=All<RadioButton>(window).Single(r=>r.Name=="GatherQualityGreen");
        var qualityBlue=All<RadioButton>(window).Single(r=>r.Name=="GatherQualityBlue");
        check(qualityWhite.Content.ToString()=="White"&&qualityGreen.Content.ToString()=="Green"&&qualityBlue.Content.ToString()=="Blue","Quality labels match the installed mappings");
        qualityGreen.IsChecked=true;window.UpdateLayout();
        Find<Button>(window,"ApplyGatherQualityLevel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        check(vm.Registry.Read("ResourceGrab","GatherQualityFilterLevel")==2,"Actual quality input and Apply button persist a verified numeric DWORD");
        qualityWhite.IsChecked=true;Find<Button>(window,"ApplyGatherQualityLevel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        gatheringScroll.BringIntoView();window.UpdateLayout();Pump();
        check(gatheringScroll.ExtentWidth<=gatheringScroll.ViewportWidth+1&&gatheringScroll.ScrollableHeight>0,"Long English resource names wrap without horizontal overflow and all resources remain reachable");
        CaptureFeature(window,"gathering-resources");
        check(All<CheckBox>(window).Select(c=>c.DataContext).OfType<RegistryToggle>().Any(t=>t.Name=="AbyssCounterAttack"&&t.Label=="Attack Nearby Players")
            && All<CheckBox>(window).Select(c=>c.DataContext).OfType<RegistryToggle>().Any(t=>t.Name=="GuardianLogoutAvoidance"&&t.Label=="Avoid Captcha System by Logging out/in"),"Resource toggle labels match the release wording exactly");
        check(!All<Button>(window).Any(b=>b.Content is "Show Updater")
            && All<Button>(window).Any(b=>b.Content is "Open AION Updater"),"Only Open AION Updater remains available; Show Updater is removed");
        TestCheckpoint.Write("WPF: enhancement fixture cap save");vm.UpgradeMaxBattlePowerK="100";vm.SaveUpgradeMaxBattlePowerK();vm.Status="Ready. Choose a setting to get started.";Navigate(window,4,"enhancement",pump:false);
        var enhancementSliders=All<Slider>(window).ToArray();var enhancementChecks=All<CheckBox>(window).ToArray();
        check(enhancementChecks.Count(c=>c.DataContext is RegistryToggle)==10
            && enhancementSliders.Any(s=>s.Name=="AutoEquipCombatPowerSlider"&&s.Minimum==1&&s.Maximum==100)
            && enhancementSliders.Any(s=>s.Name=="AutoEnchantMaxPowerSlider"&&s.Minimum==1100&&s.Maximum==1500)
            && vm.Toggles.Where(t=>t.Section=="Enhance").Select(t=>t.Name).OrderBy(n=>n).SequenceEqual(new[]{"AutoAwakenEnabled","AutoBuyOdEnergy","AutoBuyReviveStone","AutoDailyWaveEnabled","AutoDungeonQuickJoinEnabled","AutoNightmareEnabled","AutoOdMorphEnabled","CoinShopAbyssStigma","CoinShopFestivalDaevanion","CoinShopFestivalStigma","CoinShopNightmareDaevanion","CoinShopNightmareStigma","DestroyInsteadOfExtract","EnchantBoundKinahOnly","GenusInsightAutoAnalyze"}),
            "Enhancement renders independent verified caps and fifteen supported registry toggles");
        var mappedEnhancementNames=enhancementChecks.Select(c=>c.DataContext).OfType<RegistryToggle>().Select(t=>t.Name).ToArray();
        check(mappedEnhancementNames.Distinct(StringComparer.Ordinal).Count()==10&&mappedEnhancementNames.Count(n=>n=="DestroyInsteadOfExtract")==1,"Character Enhancement contains exactly one Equipment Extraction registry control and no duplicates");
        var extractionToggle=enhancementChecks.Single(c=>c.DataContext is RegistryToggle toggle&&toggle.Name=="DestroyInsteadOfExtract");
        foreach(var feature in LegionCommanderFeatureSettings.Booleans.Where(f=>f.Section=="Enhance"&&f.Name!="AutoDailyWaveEnabled"&&f.Name!="AutoNightmareEnabled"&&f.Name!="AutoAwakenEnabled"&&f.Name!="AutoOdMorphEnabled"&&f.Name!="AutoDungeonQuickJoinEnabled"))
        {
            var toggle=enhancementChecks.Single(c=>c.DataContext is RegistryToggle t&&t.Name==feature.Name);
            toggle.IsChecked=true;window.UpdateLayout();check(vm.Registry.Read("Enhance",feature.Name)==1,"Actual enhancement control writes exact DWORD: "+feature.Name);
            toggle.IsChecked=false;window.UpdateLayout();check(vm.Registry.Read("Enhance",feature.Name)==0,"Actual enhancement control persists OFF: "+feature.Name);
        }
        check(extractionToggle.IsEnabled&&!All<TextBlock>(window).Any(t=>t.Text.Contains("does not identify a verified setting name")),"Equipment Extraction keeps an enabled checkbox control with no outdated warning");

        Navigate(window,2,"auto dungeon",pump:false);
        var dungeonFlowNames=new[]{"AutoDailyWaveEnabled","AutoNightmareEnabled","AutoAwakenEnabled","AutoOdMorphEnabled","AutoDungeonQuickJoinEnabled"};
        check(All<CheckBox>(window).Select(c=>c.DataContext).OfType<RegistryToggle>().Select(t=>t.Name).SequenceEqual(dungeonFlowNames.Append("AutoDungeonUrugugu").ToArray()),"Auto Dungeon exposes the dungeon flow toggles exactly once");
        foreach(var name in dungeonFlowNames.Append("AutoDungeonUrugugu"))
        {
            var toggle=All<CheckBox>(window).Single(c=>c.DataContext is RegistryToggle t&&t.Name==name);
            toggle.IsChecked=true;window.UpdateLayout();
            check(vm.Registry.Read(toggle.DataContext is RegistryToggle t2&&t2.Section=="Enhance"?"Enhance":"ResourceGrab",name)==1,"Actual dungeon flow control writes exact DWORD: "+name);
            toggle.IsChecked=false;window.UpdateLayout();
            check(vm.Registry.Read(toggle.DataContext is RegistryToggle t3&&t3.Section=="Enhance"?"Enhance":"ResourceGrab",name)==0,"Actual dungeon flow control persists OFF: "+name);
        }
        var resetDefault=Find<Button>(window,"ResetDungeonPriorityButton")!;
        check(resetDefault.Content.ToString()=="Reset Default","Auto Dungeon exposes the Reset Default action");
        if(Environment.GetEnvironmentVariable("HEXOR_CAPTURE_RELEASE_SCREENSHOT")=="1"){var enhancementScreenshot=Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),"output","validation-1.0.10","ui-character-enhancement.png"));Directory.CreateDirectory(Path.GetDirectoryName(enhancementScreenshot)!);var enhancementImage=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);enhancementImage.Render(window);var enhancementEncoder=new PngBitmapEncoder();enhancementEncoder.Frames.Add(BitmapFrame.Create(enhancementImage));using(var enhancementStream=File.Create(enhancementScreenshot))enhancementEncoder.Save(enhancementStream);}
        var enhancementScroll=Find<ScrollViewer>(window,"PageScroll")!;enhancementScroll.ScrollToEnd();window.UpdateLayout();Pump();check(enhancementScroll.VerticalOffset>0&&enhancementScroll.ExtentWidth<=enhancementScroll.ViewportWidth+1,"Character Enhancement remains vertically scrollable without horizontal overflow");
        CaptureFeature(window,"enhancement-new-options");
        Navigate(window,7,"festival",pump:false);check(All<CheckBox>(window).Count()==2,"Festival retains both independent settings");
        check(vm.Preferences.Instructions.Select(i=>i.Id).SequenceEqual(ids) && System.Text.Json.JsonSerializer.Serialize(vm.Preferences.Instructions)==workflow,
            "Four-tab navigation never deletes or rewrites retained workflow data");
        check(!workflow.Contains("SequenceNumber"),"Retained workflow row numbering stays presentation-only");
        check(InstructionPresentation.Options.Select(o=>o.Label).SequenceEqual(new[]{"Gathering","Kinah Farming","Auto Quest","Auto Krao Dungeon","Switch Character"}),"Retained workflow labels remain available to existing model consumers");
        Navigate(window,0,"general");
        check(!All<TextBox>(window).Any(box=>BindingOperations.GetBinding(box,TextBox.TextProperty)?.Path.Path is "GamePathTw" or "GamePathKr" or "GamePathGlobal")
            && !All<TextBlock>(window).Any(text=>text.Text is "Taiwan game path" or "Korean game path" or "Global game path")
            && !All<Button>(window).Any(button=>button.Content is string label && label=="Save game locations"),
            "General removes all three manual game path fields and their save button");
        check(Find<Button>(window,"OpenAionUpdaterButton")?.Content is "Open AION Updater"
            && Find<PasswordBox>(window,"VendorLicenseKey")==null && Find<Button>(window,"ActivateVendorLicenseButton")==null
            && !All<TextBlock>(window).Any(text=>text.Text.Contains("AION license") || text.Text.Contains("License key")),
            "General exposes only the on-demand AION updater action without license-entry controls");
        var diagnostics=Find<Expander>(window,"DiagnosticsExpander")!;
        check(!diagnostics.IsExpanded,"Technical diagnostics stay collapsed by default");
        check(Find<Expander>(window,"ManualProfilesExpander")==null && Find<ComboBox>(window,"ManualProfileSelector")==null && !All<TextBlock>(window).Any(t=>t.Text.Contains("Manual profile")),"Manual profiles and related customer bindings are removed");
        TestCheckpoint.Write("WPF: expand diagnostics/layout/pump");diagnostics.IsExpanded=true;window.UpdateLayout();Pump();
        TestCheckpoint.Write("WPF: scroll diagnostics/layout/pump");var pageScroll=Find<ScrollViewer>(window,"PageScroll")!;pageScroll.ScrollToEnd();window.UpdateLayout();Pump();
        check(pageScroll.VerticalOffset>0 && pageScroll.ExtentWidth<=pageScroll.ViewportWidth+1,"Expanded diagnostics remain reachable inside the vertical viewport");
        diagnostics.IsExpanded=false;
        vm.Status="Gear cap saved and read back successfully.";
        check(vm.CustomerStatus=="Settings saved successfully.","Customer save status does not expose technical storage details");
        vm.Status="Saved folder contains no portable configuration. Found the sole verified metadata candidate; saved preferences were not rewritten.";
        check(vm.CustomerStatus=="Settings loaded.","Startup discovery does not incorrectly report a settings write");
        vm.Status="JSON was saved, but the registry mirror failed. The stores are not synchronized; use Retry AutoTask mirror.";
        check(vm.CustomerStatus.Contains("Some settings were saved"),"Partial save remains clearly distinguished from success");
        vm.Status="Ready.";
        for(int tab=0;tab<expected.Length;tab++)
        {
            Navigate(window,tab,"scaled screenshots");
            check(Find<ScrollViewer>(window,"PageScroll")!.VerticalOffset==0,"Tab navigation resets scrolling to show settings headings "+tab);
            foreach(var scale in new[]{1.0,1.25,1.5})
            {
                check(window.MinWidth*scale<=1920 && window.MinHeight*scale<=1080,"Responsive shell minimum fits 1080p at scale "+scale+" tab "+tab);
                TestCheckpoint.Write($"WPF: screenshot tab {tab} scale {scale} render/save");
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*scale),(int)Math.Ceiling(window.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var artifactRoot=Environment.GetEnvironmentVariable("HEXOR_TEST_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(),"HexorTestArtifacts");
                Directory.CreateDirectory(artifactRoot);
                var output=Path.Combine(artifactRoot,$"ui-tab-{tab}-{(int)(scale*100)}.png");
                using var file=File.Create(output);encoder.Save(file);
            }
        }
        Navigate(window,1,"final interface");
    }
    public static void CloseThroughTitleBar(MainWindow window,Action<bool,string> check)
    {
        TestCheckpoint.Write("WPF: close through title bar/pump");Find<Button>(window,"CloseWindowButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
        check(!window.IsVisible,"Custom close button closes the actual WPF window");
    }
    private static void Navigate(MainWindow window,int tab,string stage,bool pump=true)
    {
        TestCheckpoint.Run($"WPF: {stage} tab {tab} ShowPage",()=>window.ShowPage(tab));
        TestCheckpoint.Run($"WPF: {stage} tab {tab} UpdateLayout",window.UpdateLayout);
        if(pump)TestCheckpoint.Run($"WPF: {stage} tab {tab} Pump",Pump);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window,int command);
    private static void Pump()
    {
        var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var frame=new DispatcherFrame();
        using var exitTimer=new System.Threading.Timer(_=>dispatcher.BeginInvoke(DispatcherPriority.Send,
            new Action(()=>frame.Continue=false)),null,20,Timeout.Infinite);
        Dispatcher.PushFrame(frame);
    }
    private static bool WaitFor(Func<bool> condition,TimeSpan timeout)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(!condition()&&timer.Elapsed<timeout){Pump();Thread.Sleep(10);}
        return condition();
    }
    private static void CaptureFeature(Window window, string name)
    {
        var artifacts=Environment.GetEnvironmentVariable("HEXOR_TEST_ARTIFACTS");
        if(string.IsNullOrEmpty(artifacts))return;
        Directory.CreateDirectory(artifacts);
        foreach(var scale in new[]{1.0,1.5})
        {
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*scale),(int)Math.Ceiling(window.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(artifacts,$"{name}-{(int)(scale*100)}.png"));encoder.Save(stream);
        }
    }
    private static T? Find<T>(DependencyObject parent, string name) where T : FrameworkElement => All<T>(parent).FirstOrDefault(e => e.Name == name);
    internal static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t; foreach (var nested in All<T>(child)) yield return nested; }
    }
    private sealed class FakeProcesses(ProcessObservationBatch result) : IGameProcessSource { public ProcessObservationBatch Observe() => result; }
    private sealed class MutableProcesses(ProcessObservationBatch initial) : IGameProcessSource
    { public ProcessObservationBatch Batch { get; set; } = initial; public ProcessObservationBatch Observe() => Batch; }
    private sealed class MutableIdentity(LiveIdentity? initial) : IReadOnlyIdentitySource
    {
        public LiveIdentity? Data { get; set; } = initial; public bool IsAuthorized => true; public string Description => "Synthetic session source only";
        public Task<LiveIdentity?> ReadAsync(GameProcessObservation process, CancellationToken token) => Task.FromResult(Data);
    }
    private sealed class ThrowingProcesses : IGameProcessSource { public ProcessObservationBatch Observe() => throw new InvalidOperationException("secret token must not be surfaced"); }
    private sealed class FakeIdentity(LiveIdentity data, bool authorized) : IReadOnlyIdentitySource
    {
        public bool IsAuthorized => authorized; public string Description => "Synthetic unit-test source only"; public int Reads { get; private set; }
        public Task<LiveIdentity?> ReadAsync(GameProcessObservation process, CancellationToken token) { Reads++; return Task.FromResult<LiveIdentity?>(data); }
    }
    private sealed class FakeDetector(Detection initial) : ICharacterDetectionService
    { public Detection Result { get; set; } = initial; public Task<Detection> DetectAsync(CancellationToken token = default) => Task.FromResult(Result); }
    private sealed class DelegatedInspector(Func<string, ConfigurationInspection> inspect) : IConfigurationInspector
    { public ConfigurationInspection Inspect(string rootOrConfigFolder) => inspect(rootOrConfigFolder); }
}
