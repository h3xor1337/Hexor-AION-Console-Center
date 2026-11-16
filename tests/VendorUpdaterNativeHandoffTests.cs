using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Hexor;

// These modes run only this test executable, with a tiny offscreen WPF fixture window.
// They do not construct Hexor.App/MainWindow, run production startup, or launch a vendor EXE.
internal static class VendorUpdaterNativeHandoffTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "native-handoff-fixture"); Directory.CreateDirectory(directory);
        var host = TestHost();
        using var parent = Process.Start(Start(host, "--native-handoff-parent", directory)) ?? throw new Exception("Native parent fixture could not start");
        using var adapter = new WindowsVendorUpdaterProcessAdapter();
        Process? replacement = null;
        int? childId = null;
        try
        {
            WaitUntil(() => File.Exists(Path.Combine(directory, "parent-ready")));
            var identity = new VendorUpdaterProcess(parent.Id, IntPtr.Zero, host, parent.StartTime.ToUniversalTime());
            var original = adapter.FindOwned(host, identity);
            check(original != null && identity.SameInstance(original) && string.Equals(original.ExecutablePath, host, StringComparison.OrdinalIgnoreCase),
                "Native limited-query tracking resolves the harmless parent test-host PID, executable path and exact creation time");
            File.WriteAllText(Path.Combine(directory, "launch-child"), "fixture signal");
            if (!parent.WaitForExit(10000)) throw new TimeoutException("Harmless parent fixture failed to exit");
            check(parent.ExitCode == 0 && adapter.FindOwned(host, identity) == null,
                "Native tracking observes the harmless original test-host exit without confusing another same-name process");
            replacement = Process.Start(Start(host, "--native-handoff-child", directory)) ?? throw new Exception("Independent replacement fixture could not start");
            WaitUntil(() => TryReadPid(Path.Combine(directory, "child-ready"), out _));
            TryReadPid(Path.Combine(directory, "child-ready"), out var parsed); childId = parsed;
            VendorUpdaterHandoffCandidate? child = null;
            WaitUntil(() =>
            {
                child = adapter.FindHandoffCandidates(host, identity).SingleOrDefault(c => c.Process.Id == childId && c.Process.MainWindowHandle != IntPtr.Zero);
                return child != null;
            });
            check(child!.RejectionReason == null && child.Process.Id != identity.Id
                && child.WindowOwnerProcessId == child.Process.Id && child.Process.StartTimeUtc > identity.StartTimeUtc
                && string.Equals(child.Process.ExecutablePath, host, StringComparison.OrdinalIgnoreCase),
                "Native rediscovery resolves an independent harmless replacement by executable path, creation time and HWND ownership without an original-parent relationship");
            var changedPredecessor = identity with { Id = int.MaxValue, StartTimeUtc = identity.StartTimeUtc!.Value.AddSeconds(-1) };
            var rediscovered = adapter.FindHandoffCandidates(host, changedPredecessor).Single(c => c.Process.Id == childId);
            check(rediscovered.RejectionReason == null && rediscovered.Process.SameInstance(child.Process),
                "Native candidate discovery does not require the replacement to inherit the predecessor PID or ancestry");
            var current = adapter.FindOwned(host, child.Process);
            check(current != null && child.Process.SameInstance(current) && current.MainWindowHandle == child.Process.MainWindowHandle,
                "Native direct-PID lookup resolves the verified replacement's current owned top-level window");
            File.WriteAllText(Path.Combine(directory, "close-child"), "fixture signal");
            check(replacement.WaitForExit(10000) && replacement.ExitCode == 0 && adapter.FindOwned(host, child.Process) == null,
                "Harmless replacement fixture exits through its WPF window close while the parent regression host remains running");
        }
        finally
        {
            // Fixture signals request normal shutdown; these test modes also have their own deadline.
            File.WriteAllText(Path.Combine(directory, "close-child"), "fixture cleanup");
            File.WriteAllText(Path.Combine(directory, "launch-child"), "fixture cleanup");
            parent.WaitForExit(1000);
            if (childId != null)
            {
                try { using var child = Process.GetProcessById(childId.Value); child.WaitForExit(2000); }
                catch (ArgumentException) { }
            }
            replacement?.Dispose();
        }
    }

    public static int RunParent(string directory)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "parent-ready"), Environment.ProcessId.ToString());
            WaitUntil(() => File.Exists(Path.Combine(directory, "launch-child")));
            return 0;
        }
        catch { return 1; }
    }

    public static int RunChild(string directory)
    {
        try
        {
            var app = new Application();
            var window = new Window { Title = "Hexor native handoff test fixture", Width = 80, Height = 80,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.ToolWindow };
            var deadline = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                if (window.IsVisible && !File.Exists(Path.Combine(directory, "child-ready")))
                    File.WriteAllText(Path.Combine(directory, "child-ready"), Environment.ProcessId.ToString());
                if (File.Exists(Path.Combine(directory, "close-child")) || deadline.Elapsed > TimeSpan.FromSeconds(15)) { timer.Stop(); window.Close(); }
            };
            timer.Start(); return app.Run(window);
        }
        catch { return 1; }
    }

    private static string TestHost()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Hexor.Tests.exe");
        return File.Exists(executable) ? executable : throw new FileNotFoundException("Native handoff tests require their harmless test apphost", executable);
    }
    private static ProcessStartInfo Start(string executable, string mode, string directory)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        start.ArgumentList.Add(mode); start.ArgumentList.Add(directory); return start;
    }
    private static bool TryReadPid(string path, out int pid)
    {
        pid = 0;
        try { return File.Exists(path) && int.TryParse(File.ReadAllText(path), out pid); }
        catch (IOException) { return false; }
    }
    private static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Native handoff fixture deadline elapsed"); Thread.Sleep(10); }
    }
}
