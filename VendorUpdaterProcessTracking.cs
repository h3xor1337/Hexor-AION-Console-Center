using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hexor;

// Limited-query handles support permitted elevation boundaries without reading command lines,
// loading vendor code, or requesting termination rights. At most one owned handle is retained.
public sealed class WindowsVendorUpdaterProcessAdapter : IVendorUpdaterProcessAdapter, IDisposable
{
    private const uint QueryLimitedInformation = 0x1000;
    private readonly object trackingLock = new();
    private TrackedProcess? tracked;

    public VendorUpdaterProcess? Find(string expectedPath)
    {
        foreach (var pid in ProcessIds(expectedPath))
        {
            using var handle = OpenForQuery(pid);
            if (handle == null) continue;
            var snapshot = Snapshot(pid, handle);
            if (snapshot != null && PathsEqual(snapshot.ExecutablePath, expectedPath)) return snapshot;
        }
        return null;
    }

    public VendorUpdaterProcess? FindOwned(string expectedPath, VendorUpdaterProcess identity)
    {
        lock (trackingLock)
        {
            if (tracked != null && tracked.Identity.SameInstance(identity))
            {
                var snapshot = Snapshot(identity.Id, tracked.Handle, tracked.Identity.MainWindowHandle);
                if (snapshot == null || !identity.SameInstance(snapshot) || !PathsEqual(snapshot.ExecutablePath, expectedPath)) return null;
                tracked = tracked with { Identity = snapshot };
                return snapshot;
            }
            var handle = OpenForQuery(identity.Id);
            if (handle == null) return null;
            try
            {
                var snapshot = Snapshot(identity.Id, handle, identity.MainWindowHandle);
                if (snapshot == null || !identity.SameInstance(snapshot) || !PathsEqual(snapshot.ExecutablePath, expectedPath)) return null;
                ReplaceTracked(snapshot, handle);
                handle = null;
                return snapshot;
            }
            finally { handle?.Dispose(); }
        }
    }

    public VendorUpdaterProcess? Launch(string executablePath)
    {
        // No arguments: license material must never reach the command line.
        using var process = Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executablePath)!
        });
        if (process == null) return null;
        lock (trackingLock)
        {
            var handle = OpenForQuery(process.Id);
            if (handle == null) return null;
            try
            {
                var snapshot = Snapshot(process.Id, handle);
                if (snapshot == null) return null;
                ReplaceTracked(snapshot, handle);
                handle = null;
                return snapshot;
            }
            finally { handle?.Dispose(); }
        }
    }

    public IReadOnlyList<VendorUpdaterHandoffCandidate> FindHandoffCandidates(string expectedPath, VendorUpdaterProcess predecessor)
    {
        // Discovery is independent of the previous PID and its parent relationship.
        // Production expectedPath ends in Aion2.Updater.exe; the name is only the first filter.
        // The manager separately verifies installed path, original-session creation time,
        // visible window ownership and uniqueness before transferring monitoring.
        var candidates = new List<VendorUpdaterHandoffCandidate>();
        foreach (var pid in ProcessIds(expectedPath))
        {
            try
            {
                using var handle = OpenForQuery(pid);
                if (handle == null) continue;
                var snapshot = Snapshot(pid, handle);
                if (snapshot == null) continue;
                var owner = WindowOwner(snapshot.MainWindowHandle);
                candidates.Add(new(snapshot, owner));
            }
            catch (UnauthorizedAccessException)
            {
                candidates.Add(new(new(pid, IntPtr.Zero), 0, "candidate-inspection-denied"));
            }
        }
        return candidates;
    }

    private void ReplaceTracked(VendorUpdaterProcess identity, SafeProcessHandle handle)
    {
        var previous = tracked;
        tracked = new(identity, handle);
        previous?.Handle.Dispose();
    }

    public void Dispose()
    {
        lock (trackingLock)
        {
            tracked?.Handle.Dispose();
            tracked = null;
        }
    }

    private static VendorUpdaterProcess? Snapshot(int pid, SafeProcessHandle handle, IntPtr knownWindow = default)
    {
        if (!GetProcessTimes(handle, out var creation, out var exit, out _, out _)) throw InspectionDenied();
        if (exit != 0) return null;
        var path = new StringBuilder(32768); var length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw InspectionDenied();
        var window = MainWindow(pid, knownWindow);
        // A process can exit while the path and HWND are being inspected. A retained handle
        // still refers to that exact creation identity even if Windows reuses its numeric PID.
        if (!GetProcessTimes(handle, out _, out exit, out _, out _)) throw InspectionDenied();
        if (exit != 0) return null;
        return new(pid, window, Path.GetFullPath(path.ToString()), DateTime.FromFileTimeUtc(creation), WindowOwner(window));
    }

    private static SafeProcessHandle? OpenForQuery(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error == 87) return null; // The process no longer exists.
        throw InspectionDenied();
    }

    private static IReadOnlyList<int> ProcessIds(string expectedPath)
    {
        var matches = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expectedPath));
        try
        {
            return matches.Select(process => process.Id).ToArray();
        }
        finally { foreach (var process in matches) process.Dispose(); }
    }

    private static IntPtr MainWindow(int pid, IntPtr knownWindow = default)
    {
        // A window deliberately minimized or hidden after verified discovery is still the
        // same top-level target. Never enumerate arbitrary hidden WPF owner/helper windows.
        if (knownWindow != IntPtr.Zero && IsWindow(knownWindow) && WindowOwner(knownWindow) == pid
            && GetAncestor(knownWindow, 2) == knownWindow && (!IsWindowVisible(knownWindow) || IsIconic(knownWindow))) return knownWindow;
        try
        {
            using var process = Process.GetProcessById(pid);
            var main = process.MainWindowHandle;
            if (main != IntPtr.Zero && WindowOwner(main) == pid && IsWindowVisible(main)) return main;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            // EnumWindows enumerates top-level windows, including owned WPF windows whose
            // hidden owner is used for ShowInTaskbar=false. Ownership here means the actual
            // window-thread PID; the UIA adapter subsequently verifies the exact updater UI.
            if (WindowOwner(window) != pid || !IsWindowVisible(window)) return true;
            result = window; return false;
        }, IntPtr.Zero);
        return result;
    }

    private static int WindowOwner(IntPtr window)
    {
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out var pid);
        return (int)pid;
    }

    private static bool PathsEqual(string? left, string right) => string.Equals(left, Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static UnauthorizedAccessException InspectionDenied() => new("Updater process identity could not be inspected with limited query access.");
    private sealed record TrackedProcess(VendorUpdaterProcess Identity, SafeProcessHandle Handle);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int length);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
