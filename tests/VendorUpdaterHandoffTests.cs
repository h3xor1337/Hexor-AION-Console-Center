using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Hexor;

// Every process, HWND, candidate and UIA response here is synthetic. No executable is run.
internal static class VendorUpdaterHandoffTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        using (var f = new Fixture(root))
        {
            f.Ui.OnProbe = _ => { f.Processes.Exit(f.Original.Id); return Incomplete(); };
            var timer = Stopwatch.StartNew(); var result = f.Observe();
            check(result?.State != VendorUpdaterState.CompletionClosed && f.Processes.Discoveries > 0 && timer.Elapsed >= f.HandoffTimeout,
                "Original updater exit without replacement uses bounded handoff discovery and never reports installation success");
            check(f.Ui.CloseAttempts == 0 && f.Ui.ProbeCount == 1 && timer.Elapsed < TimeSpan.FromSeconds(2),
                "No-replacement discovery ends without closing anything or leaving the observer running");
        }
        using (var f = new Fixture(root))
        {
            var child = f.Child(1001);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, child); return Incomplete(); } return Complete(); };
            var result = f.Observe();
            check(result?.State == VendorUpdaterState.CompletionClosed && f.Ui.ClosedIds.SequenceEqual(new[] { child.Process.Id }),
                "An independent replacement with a different PID, installed path, session creation time and owned window inherits monitoring without ancestry evidence");
            check(f.Processes.Launches == 1 && f.Ui.ProbedIds.SequenceEqual(new[] { f.Original.Id, child.Process.Id }),
                "Verified process handoff transfers observation without relaunching Hexor or the updater");
        }
        using (var f = new Fixture(root))
        {
            f.Ui.OnProbe = _ => { f.Processes.Exit(f.Original.Id); return Incomplete(); };
            f.Observe();
            var late = f.Child(1024); f.Processes.Offer(late); f.Ui.OnProbe = _ => Complete();
            var request = f.Manager.DetectOrStart(); var result = f.Observe();
            check(request.State == VendorUpdaterState.WaitingForUpdater && result?.State == VendorUpdaterState.CompletionLeftOpen
                && f.Ui.ProbeCount == 1 && f.Ui.CloseAttempts == 0 && f.Processes.Launches == 1,
                "A manual click after discovery expires cannot adopt a late existing updater using stale session ownership");
        }
        using (var f = new Fixture(root))
        {
            var second = f.Child(1002);
            var third = f.Child(1003, second.Process);
            f.Ui.OnProbe = p =>
            {
                if (p.Id == f.Original.Id) { f.Processes.Handoff(p, second); return Incomplete(); }
                if (p.Id == second.Process.Id) { f.Processes.Handoff(p, third); return Incomplete(); }
                return Complete();
            };
            var result = f.Observe();
            check(result?.State == VendorUpdaterState.CompletionClosed && f.Ui.ProbedIds.SequenceEqual(new[] { f.Original.Id, second.Process.Id, third.Process.Id })
                && f.Ui.ClosedIds.SequenceEqual(new[] { third.Process.Id }),
                "Repeated independent updater restarts preserve the launched session and close only the final verified instance");
            check(f.Ui.MaximumActive == 1 && f.Processes.Launches == 1,
                "Repeated process handoffs retain a single automation worker and a single explicit launch");
        }
        using (var f = new Fixture(root))
        {
            // Elevation adds no identity exception: accessible path, session time and window owner must verify.
            var elevated = f.Child(1004);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, elevated); return Incomplete(); } return Complete(); };
            check(f.Observe()?.State == VendorUpdaterState.CompletionClosed && f.Ui.ClosedIds.Single() == elevated.Process.Id,
                "A simulated elevated replacement with accessible verified identity follows the normal safe handoff path");
        }
        using (var f = new Fixture(root))
        {
            var late = f.Child(1005) with { Process = f.Child(1005).Process with { MainWindowHandle = IntPtr.Zero }, WindowOwnerProcessId = 0 };
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, late); return Incomplete(); } return Complete(); };
            f.Processes.OnDiscovery = count => { if (count == 3) f.Processes.Offer(f.Child(1005)); };
            check(f.Observe()?.State == VendorUpdaterState.CompletionClosed && f.Processes.Discoveries >= 3 && f.Ui.ClosedIds.Single() == 1005,
                "A uniquely verified replacement can acquire its top-level window later within the handoff deadline");
        }
        foreach (var reason in new[] { "different path", "same name only", "missing creation time", "older creation time", "future creation time", "window owned by another PID", "native inspection denied", "Hexor PID" })
        {
            using var f = new Fixture(root);
            var candidate = f.Child(1006);
            candidate = reason switch
            {
                "different path" => candidate with { Process = candidate.Process with { ExecutablePath = Path.Combine(root, "other vendor", VendorUpdaterManager.ExecutableName) } },
                "same name only" => candidate with { Process = candidate.Process with { ExecutablePath = null } },
                "missing creation time" => candidate with { Process = candidate.Process with { StartTimeUtc = null } },
                "older creation time" => candidate with { Process = candidate.Process with { StartTimeUtc = f.Original.StartTimeUtc!.Value.AddSeconds(-1) } },
                "future creation time" => candidate with { Process = candidate.Process with { StartTimeUtc = DateTime.UtcNow.AddMinutes(1) } },
                "window owned by another PID" => candidate with { WindowOwnerProcessId = 9999 },
                "native inspection denied" => candidate with { RejectionReason = "candidate-inspection-denied" },
                "Hexor PID" => candidate with { Process = candidate.Process with { Id = int.MaxValue }, WindowOwnerProcessId = int.MaxValue },
                _ => throw new InvalidOperationException()
            };
            var rejected = candidate;
            f.Ui.OnProbe = p => { f.Processes.Handoff(p, rejected); return Complete(); };
            var result = f.Observe();
            check(result?.State != VendorUpdaterState.CompletionClosed && f.Ui.CloseAttempts == 0 && f.Ui.ProbeCount == 1
                && f.Processes.Contains(rejected.Process.Id), $"Handoff rejects {reason} and leaves the candidate running despite success-looking old UI evidence");
        }
        using (var f = new Fixture(root))
        {
            var verified = f.Child(1007);
            var unrelated = f.Child(1008) with { Process = f.Child(1008).Process with { StartTimeUtc = f.Original.StartTimeUtc!.Value.AddMinutes(-1) } };
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, verified, unrelated); return Incomplete(); } return Complete(); };
            check(f.Observe()?.State == VendorUpdaterState.CompletionClosed && f.Ui.ClosedIds.Single() == verified.Process.Id && f.Processes.Contains(unrelated.Process.Id),
                "A pre-session updater at the same path alongside a new verified replacement is neither adopted nor closed");
        }
        foreach (var siblingHasWindow in new[] { true, false })
        {
            using var f = new Fixture(root);
            var first = f.Child(1009);
            var sibling = f.Child(1010);
            if (!siblingHasWindow) sibling = sibling with { Process = sibling.Process with { MainWindowHandle = IntPtr.Zero }, WindowOwnerProcessId = 0 };
            var second = sibling;
            f.Ui.OnProbe = p => { f.Processes.Handoff(p, first, second); return Incomplete(); };
            var result = f.Observe();
            check(result?.State != VendorUpdaterState.CompletionClosed && f.Ui.ProbeCount == 1 && f.Ui.CloseAttempts == 0
                && f.Processes.Contains(first.Process.Id) && f.Processes.Contains(second.Process.Id),
                $"Two matching session replacements remain ambiguous when the second window is {(siblingHasWindow ? "present" : "not yet present")}; neither process is closed");
        }
        using (var f = new Fixture(root))
        {
            var first = f.Child(1025); var second = f.Child(1026);
            f.Ui.OnProbe = p => { f.Processes.Handoff(p, first, second); return Incomplete(); };
            f.Observe(); f.Processes.Exit(second.Process.Id); f.Ui.OnProbe = _ => Complete();
            var request = f.Manager.DetectOrStart(); var result = f.Observe();
            check(request.State == VendorUpdaterState.WaitingForUpdater && result?.State == VendorUpdaterState.CompletionLeftOpen
                && f.Ui.ProbeCount == 1 && f.Ui.CloseAttempts == 0 && f.Processes.Contains(first.Process.Id),
                "A terminal ambiguous discovery cannot be reopened by a manual click to auto-close whichever updater remains");
        }
        foreach (var secondHasWindow in new[] { true, false })
        {
            using var f = new Fixture(root);
            using var candidatesOffered = new ManualResetEventSlim();
            using var allowCompletionProbe = new ManualResetEventSlim();
            var first = f.Child(1020);
            var second = f.Child(1021);
            if (!secondHasWindow) second = second with { Process = second.Process with { MainWindowHandle = IntPtr.Zero }, WindowOwnerProcessId = 0 };
            f.Ui.OnProbe = p =>
            {
                if (p.Id == f.Original.Id) { f.Processes.Handoff(p, first); return Incomplete(); }
                // The second candidate must exist before the completion evidence is returned.
                // This removes scheduler timing from the close-revalidation assertion.
                f.Processes.Offer(first, second); candidatesOffered.Set(); allowCompletionProbe.Wait(); return Complete();
            };
            var observer = f.Manager.ObserveCompletionAndCloseAsync();
            WaitUntil(() => candidatesOffered.IsSet);
            allowCompletionProbe.Set();
            var result = observer.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            check(result?.State != VendorUpdaterState.CompletionClosed && f.Ui.ProbeCount == 2 && f.Ui.CloseAttempts == 0
                && f.Processes.Contains(first.Process.Id) && f.Processes.Contains(second.Process.Id),
                $"A second session-matching process appearing after adoption blocks close even when its window is {(secondHasWindow ? "visible" : "not yet visible")}");
        }
        using (var f = new Fixture(root))
        {
            var first = f.Child(1022);
            var inaccessible = f.Child(1023) with { Process = new(1023, IntPtr.Zero), WindowOwnerProcessId = 0, RejectionReason = "candidate-inspection-denied" };
            f.Ui.OnProbe = p =>
            {
                if (p.Id == f.Original.Id) { f.Processes.Handoff(p, first); return Incomplete(); }
                f.Processes.Offer(first, inaccessible); return Complete();
            };
            check(f.Observe()?.State != VendorUpdaterState.CompletionClosed && f.Ui.CloseAttempts == 0 && f.Processes.Contains(first.Process.Id),
                "A same-name process with inaccessible identity appearing after adoption blocks ambiguous closure");
        }
        using (var f = new Fixture(root))
        {
            var reused = f.Child(f.Original.Id);
            f.Ui.OnProbe = p =>
            {
                if (p.StartTimeUtc == f.Original.StartTimeUtc) { f.Processes.Handoff(p, reused); return Complete(); }
                return Complete();
            };
            check(f.Observe()?.State == VendorUpdaterState.CompletionClosed && f.Ui.ProbeCount == 2 && f.Ui.CloseAttempts == 1,
                "A reused numeric PID is accepted only as a new session-consistent process identity and requires a fresh success probe");
        }
        using (var f = new Fixture(root, automationTimeout: TimeSpan.FromSeconds(5)))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var first = f.Child(1027); var second = f.Child(1028);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, first); return Incomplete(); } return Complete(); };
            f.Ui.BeforeFinalGuard = () => { entered.Set(); release.Wait(); };
            var observer = f.Manager.ObserveCompletionAndCloseAsync(); WaitUntil(() => entered.IsSet);
            f.Processes.Offer(first, second); release.Set();
            var result = observer.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            check(result?.State == VendorUpdaterState.CompletionLeftOpen && f.Ui.FinalGuardChecks == 1 && f.Ui.CloseAttempts == 0
                && f.Ui.MaximumActive == 1 && f.Processes.Contains(first.Process.Id) && f.Processes.Contains(second.Process.Id),
                "Final adapter revalidation rejects a matching process that appears while the internal completion re-probe is blocked");
        }
        using (var f = new Fixture(root, automationTimeout: TimeSpan.FromSeconds(5)))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var cancel = new CancellationTokenSource())
        using (var resumedCancel = new CancellationTokenSource())
        {
            var replacement = f.Child(1029);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, replacement); return Incomplete(); } return Complete(); };
            f.Ui.BeforeFinalGuard = () => { entered.Set(); release.Wait(); };
            var first = f.Manager.ObserveCompletionAndCloseAsync(cancel.Token); WaitUntil(() => entered.IsSet); cancel.Cancel();
            first.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var resumed = f.Manager.ObserveCompletionAndCloseAsync(resumedCancel.Token); Thread.Sleep(20);
            check(!resumed.IsCompleted && f.Ui.Active == 1 && f.Ui.ProbeCount == 2 && f.Ui.CloseAttempts == 0 && f.Ui.MaximumActive == 1,
                "Cancellation during the adapter's blocked final revalidation retains one worker across observer restart");
            f.Ui.OnProbe = _ => Incomplete(); release.Set(); WaitUntil(() => f.Ui.ProbeCount >= 3); resumedCancel.Cancel();
            resumed.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(f.Ui.FinalGuardChecks == 1 && f.Ui.CloseAttempts == 0 && f.Ui.MaximumActive == 1 && f.Processes.Contains(replacement.Process.Id),
                "The final adapter callback rejects cancellation after an internal re-probe and cannot issue a delayed graceful close");
        }
        using (var f = new Fixture(root, automationTimeout: TimeSpan.FromMilliseconds(20)))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var replacement = f.Child(1030);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, replacement); return Incomplete(); } return Complete(); };
            f.Ui.BeforeFinalGuard = () => { entered.Set(); release.Wait(); };
            var observer = f.Manager.ObserveCompletionAndCloseAsync(); WaitUntil(() => entered.IsSet);
            var result = observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(result?.State == VendorUpdaterState.CompletionLeftOpen && f.Ui.Active == 1 && f.Ui.CloseAttempts == 0 && f.Ui.MaximumActive == 1,
                "A timed-out internal close re-probe remains the sole outstanding worker while the observer safely leaves the updater open");
            release.Set(); WaitUntil(() => f.Ui.Active == 0);
            check(f.Ui.FinalGuardChecks == 1 && f.Ui.CloseAttempts == 0 && f.Processes.Contains(replacement.Process.Id),
                "Close timeout revokes final authorization so a late internal re-probe cannot issue graceful closure without cancellation");
        }
        using (var f = new Fixture(root))
        {
            var second = f.Child(1018) with { Process = f.Child(1018).Process with { StartTimeUtc = f.Original.StartTimeUtc!.Value.AddSeconds(3) } };
            var third = f.Child(1019) with { Process = f.Child(1019).Process with { StartTimeUtc = f.Original.StartTimeUtc!.Value.AddSeconds(2) } };
            f.Ui.OnProbe = p =>
            {
                if (p.Id == f.Original.Id) { f.Processes.Handoff(p, second); return Incomplete(); }
                if (p.Id == second.Process.Id) { f.Processes.Handoff(p, third); return Incomplete(); }
                return Complete();
            };
            check(f.Observe()?.State == VendorUpdaterState.CompletionClosed && f.Ui.ClosedIds.Single() == third.Process.Id,
                "Rediscovery compares creation with the original session rather than demanding ancestry or a birth after the immediate predecessor");
        }
        using (var f = new Fixture(root))
        {
            var child = f.Child(1011);
            f.Ui.OnProbe = p =>
            {
                if (p.Id == f.Original.Id) { f.Processes.Handoff(p, child); return Incomplete(); }
                if (p.MainWindowHandle == child.Process.MainWindowHandle) f.Processes.Update(p with { MainWindowHandle = (IntPtr)8888 });
                return Complete();
            };
            var result = f.Observe();
            check(result?.State == VendorUpdaterState.CompletionClosed && f.Ui.ProbeCount == 3 && f.Ui.LastClosedWindow == (IntPtr)8888,
                "Replacement window recreation discards stale completion evidence and probes the current owned HWND before closing");
        }
        using (var f = new Fixture(root))
        {
            var child = f.Child(1012);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) f.Processes.Handoff(p, child); return Incomplete(); };
            using var cancel = new CancellationTokenSource(70);
            var result = f.Observe(cancel.Token);
            check(result == null && f.Ui.ProbedIds.Contains(child.Process.Id) && f.Ui.CloseAttempts == 0 && f.Processes.Contains(child.Process.Id),
                "Verified handoff and elapsed time never count as installation success while replacement UI remains incomplete");
        }
        using (var f = new Fixture(root))
        {
            var child = f.Child(1013);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, child); return Incomplete(); } return Complete(); };
            f.Ui.AcceptClose = false;
            var result = f.Observe();
            check(result?.State == VendorUpdaterState.CompletionLeftOpen && f.Ui.CloseAttempts == 1 && f.Processes.Contains(child.Process.Id),
                "A verified replacement that refuses graceful closure remains open after one bounded request without forced termination");
            check(!Process.GetCurrentProcess().HasExited, "Hexor test host remains running after a failed graceful replacement close");
        }
        using (var f = new Fixture(root))
        {
            var child = f.Child(1014);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { f.Processes.Handoff(p, child); return Complete(); } return Complete(); };
            check(f.Observe()?.State == VendorUpdaterState.CompletionClosed && f.Ui.ClosedIds.SequenceEqual(new[] { child.Process.Id }) && f.Ui.ProbeCount == 2,
                "Completion returned by an exited predecessor cannot close either process until the replacement has its own verified completion probe");
        }
        using (var f = new Fixture(root))
        using (var gate = new ManualResetEventSlim())
        {
            var child = f.Child(1015);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { gate.Wait(); return Complete(); } return Complete(); };
            var observer = f.Manager.ObserveCompletionAndCloseAsync(); WaitUntil(() => f.Ui.ProbeCount == 1);
            f.Processes.Handoff(f.Original, child);
            WaitUntil(() => f.Processes.Discoveries > 0);
            Thread.Sleep(30);
            check(!observer.IsCompleted && f.Ui.ProbeCount == 1 && f.Ui.CloseAttempts == 0,
                "Verified handoff while an old UIA call is blocked cannot start a second automation worker");
            gate.Set(); var result = observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(result?.State == VendorUpdaterState.CompletionClosed && f.Ui.MaximumActive == 1 && f.Ui.ProbeCount == 2 && f.Ui.ClosedIds.Single() == child.Process.Id,
                "Draining a blocked predecessor probe discards its success result and resumes fresh replacement monitoring");
        }
        using (var f = new Fixture(root))
        using (var gate = new ManualResetEventSlim())
        {
            var child = f.Child(1016);
            f.Ui.OnProbe = p => { if (p.Id == f.Original.Id) { gate.Wait(); return Complete(); } return Complete(); };
            using var cancel = new CancellationTokenSource();
            var first = f.Manager.ObserveCompletionAndCloseAsync(cancel.Token); WaitUntil(() => f.Ui.ProbeCount == 1);
            f.Processes.Handoff(f.Original, child); WaitUntil(() => f.Processes.Discoveries > 0); cancel.Cancel();
            first.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var resumed = f.Manager.ObserveCompletionAndCloseAsync(); Thread.Sleep(30);
            check(f.Ui.ProbeCount == 1 && f.Ui.MaximumActive == 1 && f.Ui.CloseAttempts == 0,
                "Cancellation and observer restart during handoff retain the single blocked predecessor worker");
            gate.Set(); var result = resumed.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(result?.State == VendorUpdaterState.CompletionClosed && f.Ui.MaximumActive == 1 && f.Ui.ClosedIds.Single() == child.Process.Id,
                "A cancelled handoff observer can resume only after its retained worker drains and the replacement is freshly verified");
        }
        using (var f = new Fixture(root))
        using (var gate = new ManualResetEventSlim())
        {
            f.Ui.OnProbe = _ => { gate.Wait(); return Complete(); };
            var observer = f.Manager.ObserveCompletionAndCloseAsync(); WaitUntil(() => f.Ui.ProbeCount == 1);
            f.Processes.Exit(f.Original.Id);
            var result = observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var child = f.Child(1017); f.Processes.Offer(child);
            check(result?.State != VendorUpdaterState.CompletionClosed && f.Ui.ProbeCount == 1 && f.Ui.CloseAttempts == 0 && f.Processes.Discoveries > 0,
                "Handoff discovery expires while an old automation worker is blocked and does not adopt a candidate offered after the deadline");
            gate.Set(); WaitUntil(() => f.Ui.Active == 0);
            check(f.Ui.CloseAttempts == 0 && f.Processes.Contains(child.Process.Id), "A late blocked-worker completion cannot close an unadopted post-deadline candidate");
        }
        using (var f = new Fixture(root, TimeSpan.FromSeconds(1)))
        {
            f.Processes.Exit(f.Original.Id);
            using var cancel = new CancellationTokenSource();
            var observer = f.Manager.ObserveCompletionAndCloseAsync(cancel.Token); WaitUntil(() => f.Processes.Discoveries > 0); cancel.Cancel();
            var result = observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(result == null && f.Ui.ProbeCount == 0 && f.Ui.CloseAttempts == 0,
                "Cancellation promptly stops handoff discovery without starting UIA work or closing a process");
        }
    }

    public static void StressInvisibleSecondCandidate(int iterations)
    {
        var root = Path.Combine(Path.GetTempPath(), "HexorHandoffStress-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                using var fixture = new Fixture(root);
                using var candidatesOffered = new ManualResetEventSlim();
                using var allowCompletionProbe = new ManualResetEventSlim();
                var first = fixture.Child(6200 + iteration * 2);
                var second = fixture.Child(6201 + iteration * 2) with { Process = fixture.Child(6201 + iteration * 2).Process with { MainWindowHandle = IntPtr.Zero }, WindowOwnerProcessId = 0 };
                fixture.Ui.OnProbe = process =>
                {
                    if (process.Id == fixture.Original.Id) { fixture.Processes.Handoff(process, first); return Incomplete(); }
                    fixture.Processes.Offer(first, second); candidatesOffered.Set(); allowCompletionProbe.Wait(); return Complete();
                };
                var observer = fixture.Manager.ObserveCompletionAndCloseAsync(); WaitUntil(() => candidatesOffered.IsSet);
                allowCompletionProbe.Set(); var result = observer.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                if (result?.State == VendorUpdaterState.CompletionClosed || fixture.Ui.CloseAttempts != 0
                    || !fixture.Processes.Contains(first.Process.Id) || !fixture.Processes.Contains(second.Process.Id))
                    throw new Exception("Invisible-second-candidate stress failure at iteration " + (iteration + 1));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static VendorUpdaterUi Incomplete() => new(true, false);
    private static VendorUpdaterUi Complete() => new(true, false, true, new object(), CompletionControlsVisible: true);
    private static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Handoff fixture condition not reached"); Thread.Sleep(1); }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Processes Processes;
        public readonly Automation Ui;
        public readonly VendorUpdaterManager Manager;
        public VendorUpdaterProcess Original => Processes.Original;
        public TimeSpan HandoffTimeout { get; }
        public Fixture(string root, TimeSpan? handoffTimeout = null, TimeSpan? automationTimeout = null)
        {
            var directory = Path.Combine(root, "handoff-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, VendorUpdaterManager.ExecutableName); File.WriteAllText(path, "synthetic fixture; never executable");
            Processes = new(path); Ui = new(Processes);
            // The production monitor allows a materially longer rediscovery interval. A 70 ms
            // fixture deadline flakes under normal test-host scheduling contention, before the
            // queued replacement probe can execute; retain a bounded but scheduler-safe window.
            HandoffTimeout = handoffTimeout ?? TimeSpan.FromMilliseconds(250);
            // 10 ms can expire after a worker has completed but before its continuation is
            // scheduled. One second is a bounded deadlock safeguard, not test sequencing.
            Manager = new(Processes, Ui, path, timeout: automationTimeout ?? TimeSpan.FromSeconds(1), pollInterval: TimeSpan.FromMilliseconds(1),
                completionPollInterval: TimeSpan.FromMilliseconds(1), hexorProcessId: int.MaxValue,
                hexorMainWindowHandle: () => IntPtr.Zero, handoffTimeout: HandoffTimeout, completionConfirmationSamples: 1);
            if (Manager.DetectOrStart().State != VendorUpdaterState.UpdaterStarting) throw new Exception("Handoff fixture launch failed");
        }
        public VendorUpdaterHandoffCandidate Child(int id, VendorUpdaterProcess? predecessor = null)
        {
            var parent = predecessor ?? Original;
            return new(new(id, (IntPtr)(id + 100), Original.ExecutablePath, parent.StartTimeUtc!.Value.AddSeconds(1), id), id);
        }
        public VendorUpdaterResult? Observe(CancellationToken token = default) => Manager.ObserveCompletionAndCloseAsync(token).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        public void Dispose() { }
    }

    private sealed class Processes(string path) : IVendorUpdaterProcessAdapter
    {
        private readonly object sync = new();
        private readonly Dictionary<int, VendorUpdaterProcess> running = new();
        private VendorUpdaterHandoffCandidate[] candidates = [];
        private int discoveries;
        public VendorUpdaterProcess Original { get; } = new(1000, (IntPtr)1100, path, DateTime.UtcNow.AddMinutes(-1));
        public int Launches { get; private set; }
        public int Discoveries => Volatile.Read(ref discoveries);
        public Action<int>? OnDiscovery;
        public VendorUpdaterProcess? Find(string expectedPath) { lock (sync) return running.Values.FirstOrDefault(p => string.Equals(p.ExecutablePath, expectedPath, StringComparison.OrdinalIgnoreCase)); }
        public VendorUpdaterProcess? Launch(string executablePath) { lock (sync) { Launches++; running[Original.Id] = Original; return Original; } }
        public VendorUpdaterProcess? FindOwned(string expectedPath, VendorUpdaterProcess identity)
        {
            lock (sync) return running.TryGetValue(identity.Id, out var current) && identity.SameInstance(current) ? current : null;
        }
        public IReadOnlyList<VendorUpdaterHandoffCandidate> FindHandoffCandidates(string expectedPath, VendorUpdaterProcess predecessor)
        {
            var discovery = Interlocked.Increment(ref discoveries); OnDiscovery?.Invoke(discovery);
            lock (sync) return candidates.Where(c => running.ContainsKey(c.Process.Id))
                .Select(c => c with { Process = running[c.Process.Id] }).ToArray();
        }
        public void Exit(int id) { lock (sync) running.Remove(id); }
        public void Update(VendorUpdaterProcess process) { lock (sync) running[process.Id] = process; }
        public bool Contains(int id) { lock (sync) return running.ContainsKey(id); }
        public void Handoff(VendorUpdaterProcess predecessor, params VendorUpdaterHandoffCandidate[] replacements)
        {
            lock (sync) { running.Remove(predecessor.Id); Offer(replacements); }
        }
        public void Offer(params VendorUpdaterHandoffCandidate[] replacements)
        {
            lock (sync) { candidates = replacements; foreach (var replacement in replacements) running[replacement.Process.Id] = replacement.Process; }
        }
    }

    private sealed class Automation(Processes processes) : IVendorUpdaterAutomationAdapter
    {
        private readonly object sync = new();
        private readonly List<int> probed = new(), closed = new();
        private int active, maximum, count, closeAttempts, finalGuardChecks;
        public int Active => Volatile.Read(ref active);
        public int MaximumActive => Volatile.Read(ref maximum);
        public int ProbeCount => Volatile.Read(ref count);
        public int CloseAttempts => Volatile.Read(ref closeAttempts);
        public int FinalGuardChecks => Volatile.Read(ref finalGuardChecks);
        public int[] ProbedIds { get { lock (sync) return probed.ToArray(); } }
        public int[] ClosedIds { get { lock (sync) return closed.ToArray(); } }
        public IntPtr LastClosedWindow;
        public bool AcceptClose = true;
        public Action? BeforeFinalGuard;
        public Func<VendorUpdaterProcess, VendorUpdaterUi> OnProbe = _ => Incomplete();
        public VendorUpdaterUi Probe(VendorUpdaterProcess process)
        {
            EnterOperation();
            lock (sync) probed.Add(process.Id);
            Interlocked.Increment(ref count);
            try
            {
                var ui = OnProbe(process);
                return ui with { TargetProcessId = process.Id, TargetProcessStartTimeUtc = process.StartTimeUtc, TargetExecutablePath = process.ExecutablePath,
                    TargetWindowHandle = process.MainWindowHandle, TargetWindowVerified = true };
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public void CloseWindow(VendorUpdaterUi ui)
        {
            Interlocked.Increment(ref closeAttempts); lock (sync) closed.Add(ui.TargetProcessId);
            LastClosedWindow = ui.TargetWindowHandle;
            if (AcceptClose) processes.Exit(ui.TargetProcessId);
        }
        public void CloseWindow(VendorUpdaterUi ui, Func<bool> canClose)
        {
            EnterOperation();
            try
            {
                BeforeFinalGuard?.Invoke(); Interlocked.Increment(ref finalGuardChecks);
                if (!canClose()) throw new ElementNotAvailableException();
                CloseWindow(ui);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        private void EnterOperation()
        {
            var concurrent = Interlocked.Increment(ref active); int previous;
            do { previous = Volatile.Read(ref maximum); } while (concurrent > previous && Interlocked.CompareExchange(ref maximum, concurrent, previous) != previous);
        }
    }
}
