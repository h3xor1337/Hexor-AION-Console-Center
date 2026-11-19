using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hexor;

// These fixtures never execute their dummy updater file or instantiate production windows.
internal static class VendorUpdaterCancellationTests
{
    private static int nextPid = 600000;

    public static void Run(string root, Action<bool, string> check)
    {
        using (var fixture = new Fixture(root, TimeSpan.FromMilliseconds(700)))
        {
            var started = DateTimeOffset.UtcNow;
            var discovery = Stopwatch.StartNew();
            var replacement = fixture.Replacement();
            var privateMessage = "private-com-fixture-" + Guid.NewGuid().ToString("N");
            fixture.Ui.OnProbe = process =>
            {
                if (process.SameInstance(fixture.Processes.Original))
                {
                    fixture.Processes.Exit(process.Id);
                    discovery.Restart();
                    throw new COMException(privateMessage, unchecked((int)0x8000FFFF));
                }
                return Complete();
            };
            fixture.Processes.OnDiscovery = _ =>
            {
                if (discovery.Elapsed >= TimeSpan.FromMilliseconds(220)) fixture.Processes.Offer(replacement);
            };
            var result = fixture.Observe();
            var events = EventsSince(started);
            check(result?.State == VendorUpdaterState.CompletionClosed && fixture.Ui.ClosedPid == replacement.Process.Id
                && fixture.Processes.Discoveries > 1 && discovery.Elapsed >= TimeSpan.FromMilliseconds(220),
                "A transient 0x8000FFFF COM failure and original-process exit leave discovery active until a delayed independent replacement completes and closes");
            check(!events.Any(e => e.StartsWith("cancellation-requested;", StringComparison.Ordinal))
                && events.Any(e => e.StartsWith("discovery-completed;", StringComparison.Ordinal) && e.Contains("outcome=accepted", StringComparison.Ordinal)),
                "COM recovery and process exit do not request session cancellation and record the accepted handoff outcome");
            check(events.All(e => !e.Contains(privateMessage, StringComparison.Ordinal)),
                "COM diagnostics omit exception messages while recording categorical recovery events");
        }

        using (var fixture = new Fixture(root))
        {
            var started = DateTimeOffset.UtcNow;
            fixture.Ui.OnProbe = process => { fixture.Processes.Exit(process.Id); return Incomplete(); };
            var timer = Stopwatch.StartNew();
            var result = fixture.Observe();
            var events = EventsSince(started);
            check(VendorUpdaterManager.ProcessHandoffTimeout == TimeSpan.FromSeconds(10)
                && timer.Elapsed >= VendorUpdaterManager.ProcessHandoffTimeout && timer.Elapsed < TimeSpan.FromSeconds(13)
                && fixture.Processes.Discoveries > 1 && result?.State == VendorUpdaterState.CompletionLeftOpen,
                "Original-process exit allows the actual default ten-second discovery period to finish instead of cancelling it after about 197ms");
            check(fixture.Ui.CloseAttempts == 0 && events.Any(e => e.StartsWith("discovery-completed;", StringComparison.Ordinal)
                && e.Contains("outcome=timeout", StringComparison.Ordinal) && Duration(e, "elapsed-ms=") >= 10000),
                "A full discovery timeout logs its measured complete outcome and never claims installation completion or requests close");
        }

        using (var fixture = new Fixture(root))
        using (var cancel = new CancellationTokenSource())
        {
            var started = DateTimeOffset.UtcNow;
            fixture.Ui.OnProbe = process => { fixture.Processes.Exit(process.Id); return Incomplete(); };
            var observer = fixture.Manager.ObserveCompletionAndCloseAsync(cancel.Token, () => "explicit-user");
            WaitUntil(() => fixture.Processes.Discoveries > 0);
            Thread.Sleep(200);
            cancel.Cancel();
            var result = observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var events = EventsSince(started);
            check(result == null && fixture.Ui.CloseAttempts == 0
                && events.Any(e => e.StartsWith("cancellation-requested;", StringComparison.Ordinal) && e.Contains("source=explicit-user", StringComparison.Ordinal)),
                "Explicit cancellation promptly stops discovery and records its user origin without closing a process");
            check(events.Any(e => e.StartsWith("discovery-completed;", StringComparison.Ordinal)
                && e.Contains("outcome=cancelled", StringComparison.Ordinal) && Duration(e, "elapsed-ms=") < 10000),
                "An explicitly cancelled discovery has a distinct measured outcome instead of being reported as a discovery timeout");
        }

        RunProbeDiagnostics(root, check);

    }

    public static void RunProbeDiagnostics(string root, Action<bool, string> check)
    {
        const int timeoutMs = 500;
        using (var fixture = new Fixture(root, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(timeoutMs)))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var secondEntered = new ManualResetEventSlim())
        using (var secondRelease = new ManualResetEventSlim())
        using (var cancel = new CancellationTokenSource())
        {
            var started = DateTimeOffset.UtcNow;
            var firstExecution = new Stopwatch();
            var secondExecution = new Stopwatch();
            var observation = Stopwatch.StartNew();
            fixture.Ui.OnProbe = _ =>
            {
                if (fixture.Ui.ProbeCount == 1)
                {
                    firstExecution.Start(); entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("First probe release was not signalled");
                    firstExecution.Stop(); return Complete();
                }
                secondExecution.Start(); secondEntered.Set();
                if (!secondRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Second probe release was not signalled");
                secondExecution.Stop();
                return Incomplete();
            };
            string[] Snapshot() => EventsSince(started);
            void Verify(bool condition, string message) => check(condition, message + (condition ? "" : "; actual=" + JsonSerializer.Serialize(Snapshot())));
            string AwaitEvent(string name, long operation = -1)
            {
                string? found = null;
                try
                {
                    WaitUntil(() => (found = Snapshot().FirstOrDefault(e => e.StartsWith(name + ";", StringComparison.Ordinal)
                        && (operation < 0 || Duration(e, "operation=") == operation)
                        && (!name.StartsWith("uia-", StringComparison.Ordinal) || Duration(e, "pid=") == fixture.Processes.Original.Id))) != null);
                }
                catch (TimeoutException e) { throw new TimeoutException($"Missing {name}, operation={operation}; actual={JsonSerializer.Serialize(Snapshot())}", e); }
                return found!;
            }
            var observer = fixture.Manager.ObserveCompletionAndCloseAsync(cancel.Token, () => "explicit-user");
            try
            {
                WaitUntil(() => entered.IsSet);
                var start = AwaitEvent("uia-probe-start");
                var operation = Duration(start, "operation=");
                var workerStart = AwaitEvent("uia-worker-started", operation);
                var timeout = AwaitEvent("uia-probe-timeout", operation);
                var timeoutObservedMs = observation.ElapsedMilliseconds;
                var retained = AwaitEvent("uia-worker-still-running", operation);
                Verify(!observer.IsCompleted && fixture.Ui.ProbeCount == 1 && fixture.Ui.Active == 1 && fixture.Ui.MaximumActive == 1
                    && retained.Contains("replacement-worker-started=false", StringComparison.Ordinal),
                    "A timed-out native probe reports its still-running worker while retaining exactly one automation operation");
                release.Set();
                var completed = AwaitEvent("uia-probe-completed", operation);
                var recovered = AwaitEvent("uia-worker-recovered", operation);
                WaitUntil(() => secondEntered.IsSet);
                var secondStart = Snapshot().Single(e => e.StartsWith("uia-probe-start;", StringComparison.Ordinal) && Duration(e, "operation=") != operation);
                var secondOperation = Duration(secondStart, "operation=");
                var cancellationBefore = observation.ElapsedMilliseconds;
                cancel.Cancel(); observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                var cancellation = AwaitEvent("cancellation-requested");
                var cancelled = AwaitEvent("observer-cancelled");
                var stopped = AwaitEvent("observer-stopped");
                var cancelledWorker = AwaitEvent("uia-worker-retained", secondOperation);
                var cancellationAfter = observation.ElapsedMilliseconds;
                // The observer can stop while its native worker remains blocked. Release it only
                // after retention is recorded, then await the synchronous completion write itself.
                secondRelease.Set();
                var secondCompleted = AwaitEvent("uia-probe-completed", secondOperation);
                var events = Snapshot();
                Verify(fixture.Ui.CloseAttempts == 0 && fixture.Ui.MaximumActive == 1
                    && Duration(recovered, "duration-ms=") >= 30 && recovered.Contains("stale-result-discarded=true", StringComparison.Ordinal),
                    "Worker recovery records the full native duration, discards its stale success, and cannot authorize a late close");
                Verify(events.Any(e => e.StartsWith("uia-probe-start;", StringComparison.Ordinal))
                    && events.Any(e => e.StartsWith("uia-probe-completed;", StringComparison.Ordinal) && Duration(e, "duration-ms=") >= 0)
                    && events.Any(e => e.StartsWith("uia-probe-timeout;", StringComparison.Ordinal) && Duration(e, "duration-ms=") >= 30),
                    "Probe start, completion and deadline diagnostics expose measured durations instead of inferring delay from window-discovery timestamps");
                Verify(Duration(start, "timeout-ms=") == timeoutMs && Duration(timeout, "timeout-ms=") == timeoutMs
                    && Duration(timeout, "duration-ms=") >= timeoutMs - 20 && Duration(timeout, "duration-ms=") <= timeoutObservedMs
                    && Duration(workerStart, "queue-ms=") >= 0 && Duration(workerStart, "queue-ms=") <= Duration(completed, "duration-ms=")
                    && Duration(retained, "duration-ms=") >= 1000 && Duration(retained, "duration-ms=") >= Duration(timeout, "duration-ms=")
                    && Duration(completed, "execution-ms=") >= firstExecution.ElapsedMilliseconds
                    && Duration(completed, "execution-ms=") <= Duration(completed, "duration-ms=")
                    && Duration(completed, "duration-ms=") >= Duration(retained, "duration-ms=")
                    && Duration(recovered, "duration-ms=") == Duration(completed, "duration-ms="),
                    "Operation-correlated queue, timeout, native execution and recovery durations agree with independent monotonic measurements");
                Verify(cancellation.Contains("source=explicit-user", StringComparison.Ordinal)
                    && events.Count(e => e.StartsWith("cancellation-requested;", StringComparison.Ordinal)) == 1
                    && Array.IndexOf(events, cancellation) < Array.IndexOf(events, cancelled)
                    && Array.IndexOf(events, cancelled) < Array.IndexOf(events, stopped)
                    && Duration(cancellation, "elapsed-ms=") >= cancellationBefore - 20
                    && Duration(cancellation, "elapsed-ms=") <= cancellationAfter
                    && Duration(cancelled, "elapsed-ms=") >= Duration(cancellation, "elapsed-ms=")
                    && Duration(stopped, "elapsed-ms=") >= Duration(cancelled, "elapsed-ms=")
                    && cancelledWorker.Contains("worker-still-running=True", StringComparison.Ordinal)
                    && Duration(cancelledWorker, "duration-ms=") >= 0
                    && Duration(secondCompleted, "duration-ms=") >= Duration(cancelledWorker, "duration-ms=")
                    && Duration(secondCompleted, "execution-ms=") >= secondExecution.ElapsedMilliseconds,
                    "Cancellation and retained-worker diagnostics finish before log inspection and preserve measured lifecycle ordering");
                Console.WriteLine("UIA-DIAGNOSTICS " + JsonSerializer.Serialize(new[] { start, workerStart, timeout, retained, completed, recovered, secondStart, cancellation, cancelledWorker, cancelled, stopped, secondCompleted }));
            }
            finally
            {
                release.Set(); secondRelease.Set(); cancel.Cancel();
                observer.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                WaitUntil(() => fixture.Ui.Active == 0);
            }
        }
    }

    private static VendorUpdaterUi Incomplete() => new(true, false);
    private static VendorUpdaterUi Complete() => new(true, false, true, new object(), CompletionControlsVisible: true);

    private static long Duration(string value, string field)
    {
        var index = value.IndexOf(field, StringComparison.Ordinal);
        if (index < 0) return -1;
        var text = value[(index + field.Length)..].Split(';', 2)[0].Trim();
        return long.TryParse(text, out var duration) ? duration : -1;
    }

    private static string[] EventsSince(DateTimeOffset since)
    {
        var events = new List<string>();
        if (!Directory.Exists(Diagnostics.Root)) return [];
        foreach (var file in new[] { Diagnostics.ActiveLogPath }.Where(File.Exists).Concat(Directory.EnumerateFiles(Diagnostics.Root, "hexor-*.log")))
        {
            string[] lines;
            try
            {
                // A File.ReadAllLines reader denies writes and can make the best-effort logger
                // lose a lifecycle event. Share with writers; retry incomplete final records.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var snapshot = reader.ReadToEnd();
                var complete = snapshot.LastIndexOf('\n');
                lines = complete < 0 ? [] : snapshot[..complete].Split('\n');
            }
            catch (IOException) { continue; }
            foreach (var line in lines)
            {
                try
                {
                    // Plain-text record: 2026-11-19 17:32:41.123 +08:00 [INFO] [sessionId] [pid 1234] area: message
                    var record = VendorUpdaterLogRecord.Parse(line);
                    if (record == null || DateTimeOffset.Parse(record.Timestamp) < since || record.SessionId != Diagnostics.SessionId) continue;
                    if (record.Component is "updater-monitor" or "updater-handoff") events.Add(record.Message);
                }
                catch (FormatException) { }
            }
        }
        return events.ToArray();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Cancellation fixture condition not reached"); Thread.Sleep(5); }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Processes Processes;
        public readonly Automation Ui;
        public readonly VendorUpdaterManager Manager;
        public Fixture(string root, TimeSpan? handoff = null, TimeSpan? timeout = null)
        {
            var directory = Path.Combine(root, "cancellation-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, VendorUpdaterManager.ExecutableName); File.WriteAllText(path, "synthetic updater; never executed");
            Processes = new(path); Ui = new(Processes);
            Manager = new(Processes, Ui, path, timeout: timeout ?? TimeSpan.FromMilliseconds(30), pollInterval: TimeSpan.FromMilliseconds(3),
                completionPollInterval: TimeSpan.FromMilliseconds(3), hexorProcessId: int.MaxValue, hexorMainWindowHandle: () => IntPtr.Zero,
                handoffTimeout: handoff, completionConfirmationSamples: 1);
            if (Manager.DetectOrStart().State != VendorUpdaterState.UpdaterStarting) throw new Exception("Cancellation fixture launch failed");
        }
        public VendorUpdaterHandoffCandidate Replacement()
        {
            var pid = Interlocked.Increment(ref nextPid);
            return new(new(pid, (IntPtr)800001, Processes.Original.ExecutablePath,
                Processes.Original.StartTimeUtc!.Value.AddSeconds(1), pid), pid);
        }
        public VendorUpdaterResult? Observe() => Manager.ObserveCompletionAndCloseAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        public void Dispose() { }
    }

    private sealed class Processes(string path) : IVendorUpdaterProcessAdapter
    {
        private readonly object sync = new();
        private readonly Dictionary<int, VendorUpdaterProcess> running = new();
        private VendorUpdaterHandoffCandidate[] candidates = [];
        private int discoveries;
        public VendorUpdaterProcess Original { get; } = new(Interlocked.Increment(ref nextPid), (IntPtr)800000, path, DateTime.UtcNow.AddMinutes(-1));
        public int Discoveries => Volatile.Read(ref discoveries);
        public Action<int>? OnDiscovery;
        public VendorUpdaterProcess? Find(string expectedPath) { lock (sync) return running.Values.FirstOrDefault(); }
        public VendorUpdaterProcess? Launch(string executablePath) { lock (sync) { running[Original.Id] = Original; return Original; } }
        public VendorUpdaterProcess? FindOwned(string expectedPath, VendorUpdaterProcess identity)
        {
            lock (sync) return running.TryGetValue(identity.Id, out var current) && current.SameInstance(identity) ? current : null;
        }
        public IReadOnlyList<VendorUpdaterHandoffCandidate> FindHandoffCandidates(string expectedPath, VendorUpdaterProcess predecessor)
        {
            var count = Interlocked.Increment(ref discoveries); OnDiscovery?.Invoke(count);
            lock (sync) return candidates.Where(candidate => running.ContainsKey(candidate.Process.Id)).ToArray();
        }
        public void Offer(VendorUpdaterHandoffCandidate candidate) { lock (sync) { candidates = [candidate]; running[candidate.Process.Id] = candidate.Process; } }
        public void Exit(int pid) { lock (sync) running.Remove(pid); }
    }

    private sealed class Automation(Processes processes) : IVendorUpdaterAutomationAdapter
    {
        private int active, maximum, probes, closeAttempts;
        public int Active => Volatile.Read(ref active);
        public int MaximumActive => Volatile.Read(ref maximum);
        public int ProbeCount => Volatile.Read(ref probes);
        public int CloseAttempts => Volatile.Read(ref closeAttempts);
        public int ClosedPid;
        public Func<VendorUpdaterProcess, VendorUpdaterUi> OnProbe = _ => Incomplete();
        public VendorUpdaterUi Probe(VendorUpdaterProcess process)
        {
            var concurrent = Interlocked.Increment(ref active); int previous;
            do { previous = Volatile.Read(ref maximum); } while (concurrent > previous && Interlocked.CompareExchange(ref maximum, concurrent, previous) != previous);
            Interlocked.Increment(ref probes);
            try
            {
                return OnProbe(process) with { TargetProcessId = process.Id, TargetProcessStartTimeUtc = process.StartTimeUtc,
                    TargetExecutablePath = process.ExecutablePath, TargetWindowHandle = process.MainWindowHandle, TargetWindowVerified = true };
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public void CloseWindow(VendorUpdaterUi ui)
        {
            ClosedPid = ui.TargetProcessId; Interlocked.Increment(ref closeAttempts); processes.Exit(ui.TargetProcessId);
        }
    }

    private sealed class FakeRegistry : IRegistrySettingsService
    {
        private readonly Dictionary<(string, string), int> values = new();
        public int? Read(string section, string name) => values.TryGetValue((section, name), out var value) ? value : null;
        public void WriteVerified(string section, string name, int value) => values[(section, name)] = value;
    }
}

// Parser for the plain-text diagnostic record format used by Diagnostics:
// 2026-11-19 17:32:41.123 +08:00 [INFO] [sessionId] [pid 1234] component: message
internal sealed record VendorUpdaterLogRecord(string Timestamp, string Severity, string SessionId, int ProcessId, string Component, string Message)
{
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[(?<severity>INFO|WARN|ERROR)\] \[(?<sessionId>[0-9a-f]{32})\] \[pid (?<processId>\d+)\] (?<component>[^:]+): (?<message>.*)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static VendorUpdaterLogRecord? Parse(string line)
    {
        var match = Pattern.Match(line);
        return match.Success ? new VendorUpdaterLogRecord(
            match.Groups["timestamp"].Value,
            match.Groups["severity"].Value,
            match.Groups["sessionId"].Value,
            int.Parse(match.Groups["processId"].Value),
            match.Groups["component"].Value,
            match.Groups["message"].Value) : null;
    }
}
