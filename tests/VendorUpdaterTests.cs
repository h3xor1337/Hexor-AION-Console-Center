using Hexor;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

internal static class VendorUpdaterTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static void Run(string root, Action<bool, string> assert)
    {
        var customRoot = Path.Combine(root, "Alternate Drive With Spaces", "Hexor");
        var customApp = CreateLayout(customRoot, true);
        var workingDirectory = Environment.CurrentDirectory;
        var unrelated = Path.Combine(root, "unrelated working directory"); Directory.CreateDirectory(unrelated);
        Directory.SetCurrentDirectory(unrelated);
        VendorUpdaterLocation custom;
        try { custom = VendorUpdaterInstallation.Resolve(customApp, Path.Combine(root, "unused-local-app-data")); }
        finally { Directory.SetCurrentDirectory(workingDirectory); }
        assert(custom.ExecutablePath == Path.Combine(customRoot, "AION Auto Updater", VendorUpdaterManager.ExecutableName), "Sibling updater resolution supports alternate installation roots and spaces");
        var local = Path.Combine(root, "Profiles", "Customer Two", "AppData", "Local");
        var standardRoot = Path.Combine(local, "Programs", "Hexor"); CreateLayout(standardRoot, true);
        var otherApp = Path.Combine(root, "Other Drive", "Hexor Control Center"); Directory.CreateDirectory(otherApp);
        assert(VendorUpdaterInstallation.Resolve(otherApp, local).ExecutablePath == Path.Combine(standardRoot, "AION Auto Updater", VendorUpdaterManager.ExecutableName), "Per-user LocalAppData fallback is independent of Windows username");
        var missingApp = CreateLayout(Path.Combine(root, "Missing Vendor", "Hexor"), false);
        assert(VendorUpdaterInstallation.Resolve(missingApp, Path.Combine(root, "no-fallback")).ExecutablePath == null, "Missing updater and incomplete layout return unavailable");
        var path = Path.Combine(root, "AION Auto Updater", VendorUpdaterManager.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture only; never executed");

        var preexisting = new FakeProcesses { Existing = Identity(7) };
        var preexistingUi = new FakeAutomation(_ => Completed());
        var manager = NewManager(preexisting, preexistingUi, path);
        assert(manager.DetectOrStart().State == VendorUpdaterState.WaitingForUpdater && preexisting.Launches == 0, "Existing vendor updater is reused without duplicate launch");
        var result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && preexistingUi.Probes == 0 && preexistingUi.CloseAttempts == 0, "An updater not launched by Hexor is never adopted for automatic closure");

        var completed = new FakeProcesses { LaunchResult = Identity(8) };
        var completedUi = new FakeAutomation(_ => Completed()) { OnClose = _ => completed.Existing = null };
        manager = NewManager(completed, completedUi, path);
        assert(manager.DetectOrStart().State == VendorUpdaterState.UpdaterStarting && completed.Launches == 1 && completed.LastPath == path, "Vendor updater startup uses the fixed installed executable path");
        result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && completedUi.CloseAttempts == 1, "Successful verified installation closes only the launched updater gracefully");
        completed.LaunchResult = Identity(9, 2);
        manager.DetectOrStart(); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && completedUi.CloseAttempts == 2, "A second explicitly launched session resets completion close state independently");

        var exact = WindowsVendorUpdaterAutomationAdapter.VerifiedCompletionMessage;
        assert(WindowsVendorUpdaterAutomationAdapter.IsCompletionReady(exact, true, false, false, false), "Exact verified Chinese completion message plus completion controls establishes installed state");
        assert(WindowsVendorUpdaterAutomationAdapter.IsCompletionReady("安装完成", true, false, false, false)
            && WindowsVendorUpdaterAutomationAdapter.IsCompletionReady("Installation completed successfully", true, false, false, false),
            "Alternate vendor/localized success text plus completed-state controls establishes installation completion");
        assert(WindowsVendorUpdaterAutomationAdapter.IsCompletionReady("アップデートを適用しました", true, false, false, false),
            "Localized nonempty completion text is accepted only with the reviewed completion controls");
        assert(!WindowsVendorUpdaterAutomationAdapter.IsCompletionReady("Updater is waiting for activation", false, false, false, false), "Arbitrary idle text without completion controls never establishes installation completion");
        assert(!WindowsVendorUpdaterAutomationAdapter.IsCompletionReady(exact, false, false, false, false), "A completion message without visible enabled invokable completion controls is insufficient");
        assert(!WindowsVendorUpdaterAutomationAdapter.IsCompletionReady(exact, true, true, false, false), "Downloading, extraction and installation activity block automatic close even with success text");
        assert(!WindowsVendorUpdaterAutomationAdapter.IsCompletionReady(exact, true, false, true, false), "Incomplete license activation blocks automatic close even with success text");
        assert(!WindowsVendorUpdaterAutomationAdapter.IsCompletionReady(exact, true, false, false, true), "Installation failure or retry state blocks automatic close even with success text");
        var stableProcesses = new FakeProcesses { LaunchResult = Identity(19) };
        var stableUi = new FakeAutomation(_ => Completed(), _ => new(true, false), _ => Completed(), _ => Completed(), _ => Completed())
            { OnClose = _ => stableProcesses.Existing = null };
        manager = new(stableProcesses, stableUi, path, pollInterval: TimeSpan.FromMilliseconds(1), completionPollInterval: TimeSpan.FromMilliseconds(1),
            hexorMainWindowHandle: () => IntPtr.Zero, handoffTimeout: TimeSpan.FromMilliseconds(20), completionConfirmationSamples: 3);
        if (manager.DetectOrStart().State != VendorUpdaterState.UpdaterStarting) throw new Exception("Stable completion fixture launch failed");
        result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && stableUi.Probes == 5 && stableUi.CloseAttempts == 1,
            "Three consecutive verified completion observations are required and an intervening incomplete state resets evidence");
        foreach (var phase in new[] { "download", "extraction", "activation", "installation", "failure" })
        {
            var processes = new FakeProcesses { LaunchResult = Identity(20) };
            var ui = new FakeAutomation(_ => new(true, false));
            manager = Started(processes, ui, path);
            using var cancel = new CancellationTokenSource(50);
            Observe(manager, cancel.Token);
            assert(ui.Probes > 0 && ui.CloseAttempts == 0, $"Observer keeps {phase} state open until cancellation");
        }

        var delayed = new FakeProcesses { LaunchResult = Identity(30, 0) };
        var delayedUi = new FakeAutomation(p => { delayed.Existing = Identity(30, 1); return new(false, false); }, _ => Completed()) { OnClose = _ => delayed.Existing = null };
        manager = Started(delayed, delayedUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && delayedUi.Probes == 2, "Delayed window discovery keeps observing and reacquires the eventual window");

        foreach (var kind in new[] { "COM", "stale", "repeated" })
        {
            var processes = new FakeProcesses { LaunchResult = Identity(40) };
            var failures = kind == "repeated" ? 4 : 1;
            var ui = new FakeAutomation(_ => throw (kind == "stale" ? new ElementNotAvailableException() : new COMException("synthetic private data must not be logged", unchecked((int)0x80040201))))
            { FailureCount = failures, AfterFailures = _ => Completed(), OnClose = _ => processes.Existing = null };
            manager = Started(processes, ui, path); result = Observe(manager);
            assert(result?.State == VendorUpdaterState.CompletionClosed && ui.Probes == failures + 1 && ui.CloseAttempts == 1, $"Observer recovers from {kind} UI Automation errors using fresh probes and bounded retries");
        }
        var persistent = new FakeProcesses { LaunchResult = Identity(41) };
        var persistentUi = new FakeAutomation(_ => throw new COMException());
        manager = Started(persistent, persistentUi, path);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            var observation = manager.ObserveCompletionAndCloseAsync(cancel.Token);
            try { WaitUntil(() => persistentUi.Probes >= 2); }
            finally { cancel.Cancel(); observation.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        }
        assert(persistentUi.Probes >= 2 && persistentUi.CloseAttempts == 0 && persistentUi.MaxConcurrentProbes == 1, "Repeated recoverable failures continue monitoring without concurrent probes or forced closure");

        var recreated = new FakeProcesses { LaunchResult = Identity(45, 1) };
        var recreatedUi = new FakeAutomation(p => { recreated.Existing = Identity(45, 2); return Completed(); }, _ => Completed()) { OnClose = _ => recreated.Existing = null };
        manager = Started(recreated, recreatedUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && recreatedUi.Probes == 2 && recreatedUi.LastClosedHandle == (IntPtr)2, "Window recreation discards the old window and revalidates its replacement before closing");
        var closeStale = new FakeProcesses { LaunchResult = Identity(46) };
        var closeStaleUi = new FakeAutomation(_ => Completed()) { OnClose = attempt => { if (attempt == 1) throw new ElementNotAvailableException(); closeStale.Existing = null; } };
        manager = Started(closeStale, closeStaleUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && closeStaleUi.Probes == 2 && closeStaleUi.CloseAttempts == 2, "A stale close-time reference triggers a fresh completion probe before another graceful request");

        using (var gate = new ManualResetEventSlim())
        {
            var timeout = new FakeProcesses { LaunchResult = Identity(50) };
            var ui = new FakeAutomation(_ => { gate.Wait(); return Completed(); }, _ => new(true, false));
            manager = Started(timeout, ui, path, timeout: null);
            using var cancel = new CancellationTokenSource();
            var timer = Stopwatch.StartNew(); var observer = manager.ObserveCompletionAndCloseAsync(cancel.Token);
            WaitUntil(() => ui.Probes == 1);
            Thread.Sleep(5150);
            assert(VendorUpdaterManager.AutomationProbeTimeout == TimeSpan.FromSeconds(5) && !observer.IsCompleted && ui.Probes == 1 && ui.CloseAttempts == 0 && timer.Elapsed >= TimeSpan.FromSeconds(5), "Five-second probe timeout retains one blocked worker and continues observation without closing");
            gate.Set(); WaitUntil(() => ui.Probes >= 2); cancel.Cancel(); observer.GetAwaiter().GetResult();
            assert(ui.CloseAttempts == 0 && ui.MaxConcurrentProbes == 1, "Late completion from a timed-out probe is discarded and a fresh noncomplete snapshot cannot close");
        }
        using (var gate = new ManualResetEventSlim())
        {
            var processes = new FakeProcesses { LaunchResult = Identity(51) };
            var ui = new FakeAutomation(_ => { gate.Wait(); return Completed(); }, _ => Completed()) { OnClose = _ => processes.Existing = null };
            manager = Started(processes, ui, path, TimeSpan.FromSeconds(1));
            using var cancel = new CancellationTokenSource();
            var first = manager.ObserveCompletionAndCloseAsync(cancel.Token); WaitUntil(() => ui.Probes == 1); cancel.Cancel(); first.GetAwaiter().GetResult();
            using var secondCancel = new CancellationTokenSource();
            var second = manager.ObserveCompletionAndCloseAsync(secondCancel.Token);
            var duplicate = manager.ObserveCompletionAndCloseAsync(); duplicate.GetAwaiter().GetResult(); secondCancel.Cancel(); second.GetAwaiter().GetResult();
            assert(ui.Probes == 1 && ui.MaxConcurrentProbes == 1 && ui.CloseAttempts == 0, "Cancellation and concurrent observer attempts cannot accumulate blocked UI Automation workers");
            gate.Set();
            var recovered = manager.ObserveCompletionAndCloseAsync();
            WaitUntil(() => ui.Probes == 2);
            result = recovered.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            assert(result?.State == VendorUpdaterState.CompletionClosed && ui.Probes == 2 && ui.MaxConcurrentProbes == 1,
                "Observer can recover after cancellation once the retained blocked worker drains");
        }
        using (var gate = new ManualResetEventSlim())
        {
            var independent = new FakeProcesses { LaunchResult = Identity(52) };
            var ui = new FakeAutomation(_ => { gate.Wait(); return Completed(); });
            manager = Started(independent, ui, path, TimeSpan.FromSeconds(1));
            var observer = manager.ObserveCompletionAndCloseAsync(); WaitUntil(() => ui.Probes == 1); independent.Existing = null;
            result = observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); gate.Set(); WaitUntil(() => ui.ActiveProbes == 0);
            assert(result?.State == VendorUpdaterState.CompletionLeftOpen && ui.CloseAttempts == 0,
                "Independent process exit ends bounded rediscovery safely even while a UI Automation worker is blocked");
        }
        using (var gate = new ManualResetEventSlim())
        {
            var processes = new FakeProcesses { LaunchResult = Identity(53) };
            var ui = new FakeAutomation(_ => { gate.Wait(); return Completed(); }, _ => Completed()) { OnClose = _ => processes.Existing = null };
            manager = Started(processes, ui, path);
            using (var cancel = new CancellationTokenSource())
            {
                var observer = manager.ObserveCompletionAndCloseAsync(cancel.Token);
                WaitUntil(() => ui.Probes == 1); cancel.Cancel();
                observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            assert(ui.Probes == 1 && ui.ActiveProbes == 1 && ui.CloseAttempts == 0, "Cancellation before the five-second deadline retains the outstanding automation worker");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var cancel = new CancellationTokenSource(25);
                Observe(manager, cancel.Token);
            }
            assert(ui.Probes == 1 && ui.MaxConcurrentProbes == 1, "Repeated observer restarts cannot replace a still-blocked worker cancelled before its timeout");
            processes.Existing = null; processes.LaunchResult = Identity(54);
            assert(manager.DetectOrStart().State == VendorUpdaterState.UpdaterStarting, "A new explicitly launched updater session can be recorded while an old automation worker remains blocked");
            var next = manager.ObserveCompletionAndCloseAsync();
            Thread.Sleep(30);
            assert(!next.IsCompleted && ui.Probes == 1 && ui.CloseAttempts == 0, "A replacement updater session also waits for the retained worker without starting another probe");
            gate.Set(); result = next.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            assert(result?.State == VendorUpdaterState.CompletionClosed && ui.Probes == 2 && ui.LastClosedProcess == 54 && ui.MaxConcurrentProbes == 1,
                "A drained old-session result is discarded before fresh completion evidence can close the replacement session");
        }
        using (var gate = new ManualResetEventSlim())
        {
            var processes = new FakeProcesses { LaunchResult = Identity(55) };
            var ui = new FakeAutomation(_ => { gate.Wait(); throw new COMException("late private fixture data"); }, _ => Completed())
            { OnClose = _ => processes.Existing = null };
            manager = Started(processes, ui, path, TimeSpan.FromSeconds(1));
            var observer = manager.ObserveCompletionAndCloseAsync(); WaitUntil(() => ui.Probes == 1); gate.Set();
            result = observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            assert(result?.State == VendorUpdaterState.CompletionClosed && ui.Probes == 2 && ui.MaxConcurrentProbes == 1,
                "A released blocked COM fault is observed and drained before a successful fresh probe");
        }
        using (var gate = new ManualResetEventSlim())
        {
            var processes = new FakeProcesses { LaunchResult = Identity(56) };
            var ui = new FakeAutomation(_ => Completed()) { OnClose = _ => { gate.Wait(); processes.Existing = null; } };
            manager = Started(processes, ui, path);
            using var cancel = new CancellationTokenSource();
            var observer = manager.ObserveCompletionAndCloseAsync(cancel.Token); WaitUntil(() => ui.CloseAttempts == 1); cancel.Cancel();
            observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var restarted = manager.ObserveCompletionAndCloseAsync(); Thread.Sleep(30);
            assert(!restarted.IsCompleted && ui.Probes == 1 && ui.CloseAttempts == 1, "Cancellation during a blocked graceful close does not start another automation operation");
            gate.Set(); result = restarted.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            assert(result?.State == VendorUpdaterState.CompletionLeftOpen && ui.Probes == 1 && ui.CloseAttempts == 1,
                "An eventual graceful exit after cancellation ends rediscovery without duplicating the close request");
        }

        foreach (var fixtureCompleted in new[] { false, true })
        {
            var processes = new FakeProcesses { LaunchResult = Identity(57) };
            var ui = new FakeAutomation(_ => fixtureCompleted ? Completed() : new(true, false));
            var preferences = new JsonConfigurationService(Path.Combine(root, "launch-only-" + fixtureCompleted + ".json"), new SettingsBackupService());
            using (var vm = new MainViewModel(preferences, new FakeRegistry(), vendorUpdater: NewManager(processes, ui, path)))
            {
                assert(processes.Launches == 0 && ui.Probes == 0, "View-model startup leaves the vendor updater untouched");
                vm.OpenAionUpdaterAsync().GetAwaiter().GetResult();
                vm.OpenAionUpdaterAsync().GetAwaiter().GetResult();
                Thread.Sleep(100);
                assert(processes.Launches == 1 && ui.Probes == 0 && ui.CloseAttempts == 0 && processes.Existing != null,
                    "Repeated Open AION Updater launches once without automatic completion probes, popups or closure: " + fixtureCompleted);
                processes.Existing = Identity(58) with { ExecutablePath = path };
                vm.OpenAionUpdaterAsync().GetAwaiter().GetResult();
                assert(processes.Launches == 1 && ui.Probes == 0 && ui.CloseAttempts == 0 && processes.Existing.Id == 58,
                    "Launch-only UI leaves a replacement updater open without adoption, duplicate launch or completion inspection: " + fixtureCompleted);
            }
            assert(ui.Probes == 0 && ui.CloseAttempts == 0 && processes.Existing != null,
                "Disposing Hexor leaves the vendor updater running for manual closure: " + fixtureCompleted);
            using var restarted = new MainViewModel(preferences, new FakeRegistry(), vendorUpdater: NewManager(processes, ui, path));
            restarted.OpenAionUpdaterAsync().GetAwaiter().GetResult();
            assert(processes.Launches == 1 && ui.Probes == 0 && ui.CloseAttempts == 0,
                "Restarted Hexor leaves an already-running updater open without monitoring: " + fixtureCompleted);
        }

        var multiple = new FakeProcesses { LaunchResult = Identity(60) };
        var multipleUi = new FakeAutomation(_ => Completed()) { OnClose = _ => multiple.Existing = null };
        manager = Started(multiple, multipleUi, path); multiple.Other = Identity(61) with { ExecutablePath = path }; result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && multiple.Other != null && multipleUi.LastClosedProcess == 60, "Multiple updater instances never redirect close or exit verification away from the Hexor-launched identity");
        var reused = new FakeProcesses { LaunchResult = Identity(62) };
        var reusedUi = new FakeAutomation(_ => { reused.Existing = Identity(62) with { StartTimeUtc = Start.AddMinutes(1), ExecutablePath = path }; return Completed(); });
        manager = Started(reused, reusedUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && reusedUi.CloseAttempts == 0,
            "PID reuse with a different start time cannot inherit automatic close ownership without verified rediscovery");

        var replaced = new FakeProcesses { LaunchResult = Identity(63) };
        VendorUpdaterResult? duringObservation = null;
        var replacedUi = new FakeAutomation(_ =>
        {
            replaced.Existing = null; replaced.LaunchResult = Identity(64); duringObservation = manager.DetectOrStart(); return Completed();
        }, _ => Completed()) { OnClose = _ => replaced.Existing = null };
        manager = Started(replaced, replacedUi, path); result = Observe(manager);
        assert(result?.State != VendorUpdaterState.CompletionClosed && duringObservation?.State == VendorUpdaterState.WaitingForUpdater
            && replaced.Launches == 1 && replacedUi.CloseAttempts == 0, "An active observer reserves handoff ownership and repeated clicks cannot launch or close a replacement using old completion evidence");
        manager.DetectOrStart();
        result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && replacedUi.LastClosedProcess == 64, "A restarted observer independently monitors the replacement Hexor-launched session");
        var clicked = new FakeProcesses { LaunchResult = Identity(65) };
        var clickedUi = new FakeAutomation(_ => { manager.DetectOrStart(); return Completed(); }) { OnClose = _ => clicked.Existing = null };
        manager = Started(clicked, clickedUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionClosed && clicked.Launches == 1 && clickedUi.CloseAttempts == 1, "Repeated updater clicks preserve live launch ownership and do not duplicate launches or close requests");

        var mismatched = new FakeProcesses { LaunchResult = Identity(70) };
        var mismatchUi = new FakeAutomation(_ => Completed() with { TargetProcessId = 999, TargetWindowHandle = (IntPtr)1, TargetWindowVerified = true });
        manager = Started(mismatched, mismatchUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && mismatchUi.CloseAttempts == 0, "Mismatched UI target identity is a safe terminal condition without closure");
        foreach (var mismatch in new[] { "start-time", "executable", "missing metadata" })
        {
            var processes = new FakeProcesses { LaunchResult = Identity(77) };
            var ui = new FakeAutomation(_ => mismatch switch
            {
                "start-time" => Completed() with { TargetProcessStartTimeUtc = Start.AddMinutes(1) },
                "executable" => Completed() with { TargetExecutablePath = path + ".other" },
                _ => Completed() with { TargetProcessId = 77, TargetWindowHandle = (IntPtr)1, TargetWindowVerified = true }
            }) { FillIdentity = mismatch != "missing metadata" };
            manager = Started(processes, ui, path); result = Observe(manager);
            assert(result?.State == VendorUpdaterState.CompletionLeftOpen && ui.CloseAttempts == 0,
                $"UI Automation snapshot with {mismatch} mismatch cannot authorize graceful closure");
        }
        var self = new FakeProcesses { LaunchResult = Identity(Environment.ProcessId) };
        var selfUi = new FakeAutomation(_ => Completed()); manager = Started(self, selfUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && selfUi.CloseAttempts == 0 && !Process.GetCurrentProcess().HasExited, "Hexor remains running and its own PID can never be closed");
        var selfHandle = new FakeProcesses { LaunchResult = Identity(71, 81) };
        var selfHandleUi = new FakeAutomation(_ => Completed()); manager = Started(selfHandle, selfHandleUi, path, hexorHandle: () => (IntPtr)81); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && selfHandleUi.CloseAttempts == 0, "Hexor own window handle is never a graceful close target");
        var wrongPath = new FakeProcesses { LaunchResult = Identity(72) };
        var wrongPathUi = new FakeAutomation(_ => Completed()); manager = Started(wrongPath, wrongPathUi, path); wrongPath.ReportPath = Path.Combine(root, "not-vendor.exe"); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && wrongPathUi.CloseAttempts == 0, "Executable-path mismatch refuses closure");
        var unknown = new FakeProcesses { LaunchResult = Identity(73) };
        var unknownUi = new FakeAutomation(_ => throw new InvalidOperationException()); manager = Started(unknown, unknownUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && unknownUi.CloseAttempts == 0, "Unsupported automation failure is a defined safe terminal condition");
        var denied = new FakeProcesses { LaunchResult = Identity(74) };
        var deniedUi = new FakeAutomation(_ => new(false, true)); manager = Started(denied, deniedUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.PermissionMismatch && deniedUi.CloseAttempts == 0, "Observer respects UI Automation permission boundaries");
        var refused = new FakeProcesses { LaunchResult = Identity(75) };
        var refusedUi = new FakeAutomation(_ => Completed()); manager = Started(refused, refusedUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && refusedUi.CloseAttempts == 1, "Graceful close refusal leaves updater open after one request without force killing");
        var threwClose = new FakeProcesses { LaunchResult = Identity(76) };
        var threwCloseUi = new FakeAutomation(_ => Completed()) { OnClose = _ => throw new InvalidOperationException() }; manager = Started(threwClose, threwCloseUi, path); result = Observe(manager);
        assert(result?.State == VendorUpdaterState.CompletionLeftOpen && threwCloseUi.CloseAttempts == 1, "Nonrecoverable graceful close failure leaves the updater and Hexor running");
    }

    public static void StressRetainedWorkerRecovery(int iterations)
    {
        var root = Path.Combine(Path.GetTempPath(), "HexorRetainedWorkerStress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, VendorUpdaterManager.ExecutableName); File.WriteAllText(path, "synthetic fixture");
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                using var gate = new ManualResetEventSlim();
                var processes = new FakeProcesses { LaunchResult = Identity(5100 + iteration) };
                var ui = new FakeAutomation(_ => { gate.Wait(); return Completed(); }, _ => Completed()) { OnClose = _ => processes.Existing = null };
                var manager = Started(processes, ui, path, TimeSpan.FromSeconds(1));
                using var cancel = new CancellationTokenSource();
                var first = manager.ObserveCompletionAndCloseAsync(cancel.Token); WaitUntil(() => ui.Probes == 1); cancel.Cancel(); first.GetAwaiter().GetResult();
                gate.Set();
                var recovered = manager.ObserveCompletionAndCloseAsync(); WaitUntil(() => ui.Probes == 2);
                var result = recovered.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (result?.State != VendorUpdaterState.CompletionClosed || ui.Probes != 2 || ui.MaxConcurrentProbes != 1)
                    throw new Exception("Retained-worker recovery stress failure at iteration " + (iteration + 1));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static VendorUpdaterProcess Identity(int id, int handle = 1) => new(id, (IntPtr)handle, StartTimeUtc: Start);
    private static VendorUpdaterManager NewManager(FakeProcesses processes, FakeAutomation ui, string path, TimeSpan? timeout = null, Func<IntPtr>? hexorHandle = null)
        => new(processes, ui, path, timeout, pollInterval: TimeSpan.FromMilliseconds(1), completionPollInterval: TimeSpan.FromMilliseconds(1), hexorMainWindowHandle: hexorHandle, handoffTimeout: TimeSpan.FromMilliseconds(20), completionConfirmationSamples: 1);
    private static VendorUpdaterManager Started(FakeProcesses processes, FakeAutomation ui, string path, TimeSpan? timeout = null, Func<IntPtr>? hexorHandle = null)
    {
        var manager = NewManager(processes, ui, path, timeout, hexorHandle);
        if (manager.DetectOrStart().State != VendorUpdaterState.UpdaterStarting) throw new Exception("Fixture launch failed");
        return manager;
    }
    private static VendorUpdaterResult? Observe(VendorUpdaterManager manager, CancellationToken token = default)
        => manager.ObserveCompletionAndCloseAsync(token).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    private static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Fixture condition not reached"); Thread.Sleep(1); }
    }
    private static string CreateLayout(string root, bool includeUpdater)
    {
        var app = Path.Combine(root, VendorUpdaterInstallation.ControlCenterFolder); Directory.CreateDirectory(app);
        var updater = Path.Combine(root, VendorUpdaterInstallation.UpdaterFolder); Directory.CreateDirectory(updater);
        if (includeUpdater) File.WriteAllText(Path.Combine(updater, VendorUpdaterManager.ExecutableName), "fixture only");
        return app;
    }
    private static VendorUpdaterUi Completed() => new(true, false, true, new object(), CompletionControlsVisible: true);

    private sealed class FakeRegistry : IRegistrySettingsService
    {
        private readonly Dictionary<(string, string), int> values = new();
        public int? Read(string section, string name) => values.TryGetValue((section, name), out var value) ? value : null;
        public void WriteVerified(string section, string name, int value) => values[(section, name)] = value;
    }

    private sealed class FakeProcesses : IVendorUpdaterProcessAdapter
    {
        public volatile VendorUpdaterProcess? Existing;
        public VendorUpdaterProcess? Other;
        public VendorUpdaterProcess? LaunchResult;
        public int Launches;
        public string? LastPath;
        public string? ReportPath;
        public VendorUpdaterProcess? Find(string expectedPath) => Other ?? (Existing is { } value ? value with { ExecutablePath = ReportPath ?? expectedPath } : null);
        public VendorUpdaterProcess? FindOwned(string expectedPath, VendorUpdaterProcess identity) => Existing is { } value && identity.SameInstance(value)
            ? value with { ExecutablePath = ReportPath ?? expectedPath } : null;
        public VendorUpdaterProcess? Launch(string path)
        {
            Launches++; LastPath = path; Existing = LaunchResult is { } value ? value with { ExecutablePath = path } : null; return Existing;
        }
    }
    private sealed class FakeAutomation(params Func<VendorUpdaterProcess, VendorUpdaterUi>[] probes) : IVendorUpdaterAutomationAdapter
    {
        private int index, active, maximum, closeAttempts;
        public int Probes => Volatile.Read(ref index);
        public int ActiveProbes => Volatile.Read(ref active);
        public int MaxConcurrentProbes => Volatile.Read(ref maximum);
        public int CloseAttempts => Volatile.Read(ref closeAttempts);
        public int LastClosedProcess;
        public IntPtr LastClosedHandle;
        public int FailureCount { get; init; }
        public bool FillIdentity { get; init; } = true;
        public Func<VendorUpdaterProcess, VendorUpdaterUi>? AfterFailures { get; init; }
        public Action<int>? OnClose { get; init; }
        public VendorUpdaterUi Probe(VendorUpdaterProcess process)
        {
            var concurrent = Interlocked.Increment(ref active);
            int priorMaximum;
            do { priorMaximum = Volatile.Read(ref maximum); }
            while (concurrent > priorMaximum && Interlocked.CompareExchange(ref maximum, concurrent, priorMaximum) != priorMaximum);
            var number = Interlocked.Increment(ref index);
            try
            {
                var ui = AfterFailures != null && number > FailureCount ? AfterFailures(process) : probes[Math.Min(number - 1, probes.Length - 1)](process);
                return !ui.CompletionReady || !FillIdentity ? ui : ui with
                {
                    TargetProcessId = ui.TargetProcessId == 0 ? process.Id : ui.TargetProcessId,
                    TargetWindowHandle = ui.TargetProcessId == 0 ? process.MainWindowHandle : ui.TargetWindowHandle,
                    TargetWindowVerified = ui.TargetProcessId == 0 || ui.TargetWindowVerified,
                    TargetProcessStartTimeUtc = ui.TargetProcessStartTimeUtc ?? process.StartTimeUtc,
                    TargetExecutablePath = ui.TargetExecutablePath ?? process.ExecutablePath
                };
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public void CloseWindow(VendorUpdaterUi ui)
        {
            LastClosedProcess = ui.TargetProcessId; LastClosedHandle = ui.TargetWindowHandle;
            var attempt = Interlocked.Increment(ref closeAttempts); OnClose?.Invoke(attempt);
        }
    }
}
