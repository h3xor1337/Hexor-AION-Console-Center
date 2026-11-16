using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Hexor;

// Retained low-level consent/security contracts; the launch-only UI never invokes them.
// Synthetic processes/windows only: the fixture executable is never launched.
internal static class VendorUpdaterConsentTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        using (var fixture = new Fixture(root))
        {
            var premature = Close(fixture.Manager);
            check(premature.State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.Probes == 0 && fixture.Ui.CloseRequests == 0,
                "A Close Updater click without verified completion never inspects or closes an updater");
            var observed = Observe(fixture.Manager);
            check(observed?.State == VendorUpdaterState.CompletionVerified && fixture.Ui.Probes == 3 && fixture.Ui.CloseRequests == 0,
                "Three stable completion observations offer customer consent without automatic closure");
            var closed = Close(fixture.Manager);
            check(closed.State == VendorUpdaterState.CompletionClosed && fixture.Ui.Probes == 4 && fixture.Ui.CloseRequests == 1,
                "The customer click freshly probes completion and requests one graceful close of the verified session");
            check(Close(fixture.Manager).State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.CloseRequests == 1,
                "Repeated customer clicks cannot reuse consumed completion authorization or duplicate graceful closure");
        }

        foreach (var change in new[] { "PID reuse", "path", "window", "unknown creation" })
        {
            using var fixture = new Fixture(root);
            Observe(fixture.Manager);
            var current = fixture.Processes.Current!;
            fixture.Processes.Current = change switch
            {
                "PID reuse" => current with { StartTimeUtc = current.StartTimeUtc!.Value.AddSeconds(1) },
                "path" => current with { ExecutablePath = Path.Combine(root, "unrelated.exe") },
                "window" => current with { MainWindowHandle = current.MainWindowHandle + 1 },
                _ => current with { StartTimeUtc = null }
            };
            check(Close(fixture.Manager).State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.CloseRequests == 0,
                $"Customer closure rejects a changed {change} after the completion popup was offered");
        }

        using (var fixture = new Fixture(root))
        {
            Observe(fixture.Manager);
            fixture.Ui.OnProbe = process => Complete(process) with { CompletionReady = false, RequiresAttention = true };
            check(Close(fixture.Manager).State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.CloseRequests == 0,
                "A login, retry or incomplete state appearing after the popup revokes customer close authorization");
        }
        using (var fixture = new Fixture(root))
        {
            Observe(fixture.Manager);
            fixture.Ui.BeforeFinalGuard = () => fixture.Processes.Current = fixture.Processes.Current! with { MainWindowHandle = (IntPtr)99123 };
            check(Close(fixture.Manager).State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.CloseRequests == 0,
                "The final graceful-close guard rejects a window recreated after the fresh completion probe");
        }
        using (var fixture = new Fixture(root))
        {
            fixture.Processes.Current = null;
            check(Observe(fixture.Manager)?.State == VendorUpdaterState.CompletionLeftOpen && fixture.Ui.CloseRequests == 0,
                "Updater exit and expired handoff discovery are never treated as verified completion in the consent workflow");
        }

        foreach (var state in new[] { "unknown", "attention", "updating" })
        {
            using var fixture = new Fixture(root);
            fixture.Ui.OnProbe = process => Complete(process) with
            {
                CompletionReady = false,
                CompletionControlsVisible = false,
                UpdateInProgress = state == "updating",
                RequiresAttention = state == "attention"
            };
            using var cancellation = new CancellationTokenSource();
            var progress = new ConcurrentQueue<VendorUpdaterResult>();
            var observation = fixture.Manager.ObserveCompletionAsync(cancellation.Token, progress: progress.Enqueue);
            WaitUntil(() => fixture.Ui.Probes >= 3 && !progress.IsEmpty);
            var expected = state == "updating" ? VendorUpdaterState.WaitingForUpdater : VendorUpdaterState.CheckUpdaterStatus;
            check(!observation.IsCompleted && progress.Last().State == expected && fixture.Ui.CloseRequests == 0,
                $"The consent workflow reports {state} UI honestly without using elapsed time or idle polling as completion proof");
            cancellation.Cancel();
            observation.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }

        using (var fixture = new Fixture(root))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            Observe(fixture.Manager);
            fixture.Ui.OnProbe = process => { entered.Set(); release.Wait(); return Complete(process); };
            var first = fixture.Manager.CloseVerifiedUpdaterAsync();
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Consent probe did not enter");
                var duplicate = Close(fixture.Manager);
                var duplicateObservation = Observe(fixture.Manager);
                check(duplicate.State == VendorUpdaterState.CheckUpdaterStatus && duplicateObservation == null && fixture.Ui.MaximumActive == 1,
                    "Concurrent Close Updater clicks and observer restarts cannot create another UI Automation worker");
            }
            finally { release.Set(); }
            check(first.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().State == VendorUpdaterState.CompletionClosed && fixture.Ui.CloseRequests == 1,
                "The single authorized customer close completes once after its fresh probe returns");
        }

        using (var fixture = new Fixture(root, TimeSpan.FromMilliseconds(250)))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            Observe(fixture.Manager);
            fixture.Ui.OnProbe = process => { entered.Set(); release.Wait(); return Complete(process); };
            var closing = fixture.Manager.CloseVerifiedUpdaterAsync();
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Blocked consent probe did not enter");
                var result = closing.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                check(result.State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.CloseRequests == 0 && fixture.Ui.MaximumActive == 1,
                    "A bounded customer close probe timeout leaves the updater open and retains its one native worker");
                using var cancellation = new CancellationTokenSource();
                var resumed = fixture.Manager.ObserveCompletionAsync(cancellation.Token);
                Thread.Sleep(20);
                check(fixture.Ui.Probes == 4 && fixture.Ui.MaximumActive == 1 && fixture.Ui.CloseRequests == 0,
                    "A timed-out consent probe cannot authorize closure or overlap a replacement inspection");
                cancellation.Cancel();
                resumed.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            finally { release.Set(); }
            WaitUntil(() => fixture.Ui.Active == 0);
            check(fixture.Ui.CloseRequests == 0, "A late completed consent probe has no authority to close the vendor window");
        }

        foreach (var budget in new[] { TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(2) })
        using (var fixture = new Fixture(root, handoffTimeout: budget))
        {
            var original = fixture.Processes.Current!;
            var replacement = original with { Id = original.Id + 1, MainWindowHandle = original.MainWindowHandle + 1,
                StartTimeUtc = original.StartTimeUtc!.Value.AddSeconds(1), WindowOwnerProcessId = original.Id + 1 };
            fixture.Processes.Current = replacement;
            fixture.Processes.Candidates = [new(replacement, replacement.Id)];
            // A controlled scheduling delay demonstrates why 30 ms is not a valid
            // positive-discovery budget. The negative timeout remains covered.
            fixture.Processes.OnDiscovery = attempt => { if (attempt == 1) Thread.Sleep(60); };
            var observed = Observe(fixture.Manager);
            Console.WriteLine($"HANDOFF-EVIDENCE budget-ms={budget.TotalMilliseconds}; discovery-attempts={fixture.Processes.Discoveries}; probes={fixture.Ui.Probes}; result={observed?.State}; close-requests={fixture.Ui.CloseRequests}");
            if (budget.TotalMilliseconds == 30)
            {
                check(observed?.State == VendorUpdaterState.CompletionLeftOpen && fixture.Ui.Probes == 0 && fixture.Ui.CloseRequests == 0,
                    "An expired synthetic handoff budget leaves even a valid delayed replacement uninspected and open");
                continue;
            }
            check(observed?.State == VendorUpdaterState.CompletionVerified && fixture.Ui.CloseRequests == 0
                && fixture.Manager.GetOwnedProcess()?.SameInstance(replacement) == true,
                "A unique verified updater handoff can offer completion consent without closing either process");
            fixture.Processes.Candidates = [new(replacement, replacement.Id), new(replacement with { Id = replacement.Id + 1 }, replacement.Id + 1)];
            check(Close(fixture.Manager).State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.CloseRequests == 0,
                "A newly ambiguous replacement set invalidates the earlier completion popup before customer closure");
        }

        using (var fixture = new Fixture(root, preexisting: true))
        {
            check(fixture.Manager.GetOwnedProcess() == null
                && Observe(fixture.Manager)?.State == VendorUpdaterState.CompletionLeftOpen && fixture.Ui.Probes == 0,
                "A preexisting updater is never adopted or inspected for completion consent");
            check(Close(fixture.Manager).State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.CloseRequests == 0,
                "Customer closure cannot target an updater that Hexor did not launch");
        }

        using (var fixture = new Fixture(root))
        {
            Observe(fixture.Manager);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            check(Close(fixture.Manager, cancellation.Token).State == VendorUpdaterState.CheckUpdaterStatus && fixture.Ui.CloseRequests == 0,
                "Application shutdown cancellation prevents an otherwise verified customer close request");
        }
    }

    private static VendorUpdaterResult? Observe(VendorUpdaterManager manager)
        => manager.ObserveCompletionAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    private static VendorUpdaterResult Close(VendorUpdaterManager manager, CancellationToken token = default)
        => manager.CloseVerifiedUpdaterAsync(token).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    private static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Consent fixture condition not reached"); Thread.Sleep(1); }
    }
    private static VendorUpdaterUi Complete(VendorUpdaterProcess process) => new(true, false, true, new object(),
        TargetProcessId: process.Id, TargetWindowHandle: process.MainWindowHandle, TargetWindowVerified: true,
        CompletionControlsVisible: true, TargetProcessStartTimeUtc: process.StartTimeUtc, TargetExecutablePath: process.ExecutablePath);

    private sealed class Fixture : IDisposable
    {
        public Processes Processes { get; }
        public Automation Ui { get; }
        public VendorUpdaterManager Manager { get; }
        public Fixture(string root, TimeSpan? timeout = null, bool preexisting = false, TimeSpan? handoffTimeout = null)
        {
            var path = Path.Combine(root, "consent-" + Guid.NewGuid().ToString("N"), VendorUpdaterManager.ExecutableName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Synthetic fixture only; never executed.");
            Processes = new(path, preexisting);
            Ui = new(Processes);
            Manager = new(Processes, Ui, path, timeout: timeout ?? TimeSpan.FromSeconds(1), pollInterval: TimeSpan.FromMilliseconds(1),
                completionPollInterval: TimeSpan.FromMilliseconds(1), handoffTimeout: handoffTimeout ?? TimeSpan.FromMilliseconds(30),
                hexorProcessId: int.MaxValue, hexorMainWindowHandle: () => IntPtr.Zero);
            var result = Manager.DetectOrStart();
            if (result.State != (preexisting ? VendorUpdaterState.WaitingForUpdater : VendorUpdaterState.UpdaterStarting))
                throw new InvalidOperationException("Consent fixture could not establish its synthetic session");
        }
        public void Dispose() { }
    }
    private sealed class Processes : IVendorUpdaterProcessAdapter
    {
        private VendorUpdaterProcess? current;
        private readonly VendorUpdaterProcess initial;
        public VendorUpdaterProcess? Current { get => Volatile.Read(ref current); set => Volatile.Write(ref current, value); }
        public VendorUpdaterHandoffCandidate[] Candidates = [];
        private int discoveries;
        public int Discoveries => Volatile.Read(ref discoveries);
        public Action<int>? OnDiscovery;
        public Processes(string path, bool preexisting)
        {
            initial = new(710001, (IntPtr)710101, path, DateTime.UtcNow.AddMinutes(-1), 710001);
            if (preexisting) Current = initial;
        }
        public VendorUpdaterProcess? Find(string expectedPath) => Current;
        public VendorUpdaterProcess? Launch(string executablePath) { Current = initial; return initial; }
        public VendorUpdaterProcess? FindOwned(string expectedPath, VendorUpdaterProcess identity)
            => Current is { } value && identity.SameInstance(value) ? value : null;
        public IReadOnlyList<VendorUpdaterHandoffCandidate> FindHandoffCandidates(string expectedPath, VendorUpdaterProcess predecessor)
        {
            var attempt = Interlocked.Increment(ref discoveries);
            OnDiscovery?.Invoke(attempt);
            return attempt == 1 && OnDiscovery != null ? [] : Candidates;
        }
    }
    private sealed class Automation(Processes processes) : IVendorUpdaterAutomationAdapter
    {
        private int probes, closeRequests, active, maximum;
        public int Probes => Volatile.Read(ref probes);
        public int CloseRequests => Volatile.Read(ref closeRequests);
        public int Active => Volatile.Read(ref active);
        public int MaximumActive => Volatile.Read(ref maximum);
        public Func<VendorUpdaterProcess, VendorUpdaterUi> OnProbe = Complete;
        public Action? BeforeFinalGuard;
        public VendorUpdaterUi Probe(VendorUpdaterProcess process)
        {
            var concurrent = Interlocked.Increment(ref active);
            int prior;
            do { prior = Volatile.Read(ref maximum); }
            while (concurrent > prior && Interlocked.CompareExchange(ref maximum, concurrent, prior) != prior);
            Interlocked.Increment(ref probes);
            try { return OnProbe(process); }
            finally { Interlocked.Decrement(ref active); }
        }
        public void CloseWindow(VendorUpdaterUi ui) => CloseWindow(ui, () => true);
        public void CloseWindow(VendorUpdaterUi ui, Func<bool> canClose)
        {
            BeforeFinalGuard?.Invoke();
            if (!canClose()) throw new ElementNotAvailableException();
            Interlocked.Increment(ref closeRequests);
            processes.Current = null;
        }
    }
}
