using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hexor.Runtime;

public sealed record GameProcessObservation(int Id, string? ExecutablePath, DateTimeOffset? StartedAt, string? Error = null, string? Launcher = null);
public sealed record ProcessObservationBatch(IReadOnlyList<GameProcessObservation> Processes, IReadOnlyList<string> Errors);
public interface IGameProcessSource { ProcessObservationBatch Observe(); }

// Launcher provenance for a discovered game process, resolved from the parent process name in a
// single read-only kernel32 process snapshot (CreateToolhelp32Snapshot; a documented enumeration
// API). The Purple launcher spawns Aion2.exe directly, so a parent process whose image name starts
// with "Purple" (the NCSoft Purple launcher, verified in the provider binary: PurpleLauncher.ini,
// purple:// protocol handlers, PurpleGameInfo launch tables) attributes that process to Purple.
// Anything else is reported as unidentified rather than guessed.
public static class LauncherProvenance
{
    public const string Purple = "Purple launcher";
    private const string GameProcessName = "aion2";
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint Size; public uint Usage; public uint ProcessId; public UIntPtr DefaultHeapId; public uint ModuleId;
        public uint Threads; public uint ParentProcessId; public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const int ERROR_NO_MORE_FILES = 18;

    // True for any casing of the game client image name (AION2.exe as shipped by Steam installs,
    // Aion2.exe as launched by the Purple launcher): the match is intentionally case-insensitive.
    public static bool IsGameProcessName(string? name) =>
        name is not null && name.Equals(GameProcessName, StringComparison.OrdinalIgnoreCase);

    public static string? ForParentProcessName(string? parentName) =>
        parentName is not null && parentName.StartsWith("purple", StringComparison.OrdinalIgnoreCase) ? Purple : null;

    // pid → parent pid, from one snapshot. Never reads another process's memory; only its identity.
    public static IReadOnlyDictionary<int, int> BuildParentMap()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return parents;
        try
        {
            var entry = new PROCESSENTRY32W { Size = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                    entry = new PROCESSENTRY32W { Size = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                }
                while (Process32NextW(snapshot, ref entry));
            }
            else if (Marshal.GetLastWin32Error() is not (ERROR_NO_MORE_FILES or 0))
            { return parents; }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        finally { CloseSnapshot(snapshot); }
        return parents;
    }

    // Provenance for a set of game pids, resolved from a single snapshot: pid → "Purple launcher"
    // or null (parent exited, snapshot unavailable, or parent is not the Purple launcher).
    public static IReadOnlyDictionary<int, string?> Resolve(IReadOnlyCollection<int> processIds)
    {
        var result = new Dictionary<int, string?>();
        if (processIds.Count == 0) return result;
        var parents = BuildParentMap();
        if (parents.Count == 0) return result;
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        var names = new Dictionary<int, string>();
        if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
        {
            try
            {
                var entry = new PROCESSENTRY32W { Size = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        names[(int)entry.ProcessId] = Path.GetFileNameWithoutExtension(entry.ExeFile);
                        entry = new PROCESSENTRY32W { Size = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                    }
                    while (Process32NextW(snapshot, ref entry));
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
            finally { CloseSnapshot(snapshot); }
        }
        foreach (var pid in processIds)
            result[pid] = parents.TryGetValue(pid, out var parent) && names.TryGetValue(parent, out var name)
                ? ForParentProcessName(name) : null;
        return result;
    }
    private static void CloseSnapshot(IntPtr snapshot) { try { CloseHandle(snapshot); } catch (Exception e) when (e is DllNotFoundException or InvalidOperationException) { } }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}

public sealed class WindowsGameProcessSource : IGameProcessSource
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    private static string? ReadImagePath(int processId)
    {
        // PROCESS_QUERY_LIMITED_INFORMATION works without reading process modules or memory.
        using var handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(32768);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
    }
    public ProcessObservationBatch Observe()
    {
        var found = new List<GameProcessObservation>();
        var errors = new List<string>();
        // Case-insensitive name matching on the full process list covers both launch routes verified
        // in the provider binary: AION2.exe (Steam-installed clients) and Aion2.exe (Purple-launched
        // clients). GetProcessesByName pins one casing and is fragile against launcher differences.
        List<Process>? candidates;
        try { candidates = [.. Process.GetProcesses()]; }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { return new([], ["Windows process enumeration unavailable (" + e.GetType().Name + ")."]); }
        List<Process>? games = null;
        try
        {
            games = candidates.Where(p =>
            {
                try { return LauncherProvenance.IsGameProcessName(p.ProcessName); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or SystemException) { return false; }
            }).ToList();
        }
        finally { foreach (var candidate in candidates) { if (games == null || !games.Contains(candidate)) candidate.Dispose(); } }
        try
        {
            var launcherMap = LauncherProvenance.Resolve(games.Select(p => p.Id).ToArray());
            foreach (var process in games)
            {
                string? path = null;
                DateTimeOffset? started = null;
                string? error = null;
                try { path = ReadImagePath(process.Id) ?? process.MainModule?.FileName; }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                { error = "Executable metadata unavailable (" + e.GetType().Name + ")."; }
                try { started = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                { error ??= "Process start time unavailable (" + e.GetType().Name + ")."; }
                launcherMap.TryGetValue(process.Id, out var launcher);
                try { found.Add(new(process.Id, path, started, error, launcher)); }
                catch (InvalidOperationException) { errors.Add("A game process exited during enumeration."); }
            }
        }
        finally { foreach (var process in games) process.Dispose(); }
        return new(found.OrderBy(p => p.Id).ToArray(), errors);
    }
}

// An explicitly authorized provider may be added later. The shipped provider makes no IPC/network/file requests.
public sealed record LiveIdentity(int ProcessId, string ExecutablePath, DateTimeOffset ProcessStartedAt,
    DateTimeOffset CapturedAt, bool InGame, string? Character, string? ServerName, int? ServerId);
public interface IReadOnlyIdentitySource
{
    bool IsAuthorized { get; }
    string Description { get; }
    Task<LiveIdentity?> ReadAsync(GameProcessObservation process, CancellationToken token);
}
public sealed class UnavailableIdentitySource : IReadOnlyIdentitySource
{
    public bool IsAuthorized => false;
    public string Description => "Unavailable: no authorized live status source. Reference status uses private IPC, which Hexor does not access.";
    public Task<LiveIdentity?> ReadAsync(GameProcessObservation process, CancellationToken token) => Task.FromResult<LiveIdentity?>(null);
}

public static class ExecutablePaths
{
    public static bool Equal(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right)) return false;
            return StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

public sealed class ReadOnlyRuntimeDetector(IGameInstallationDiscoveryService installations, IGameProcessSource processes,
    IReadOnlyIdentitySource identity, Func<DateTimeOffset>? clock = null) : ICharacterDetectionService
{
    // Historical timestamp only; identities themselves are never cached between observations.
    private DateTimeOffset? lastSuccessfulIdentityUpdate;
    public async Task<Detection> DetectAsync(CancellationToken token = default)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        ProcessObservationBatch batch;
        try { batch = await Task.Run(processes.Observe, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { batch = new([], ["Process source unavailable (" + e.GetType().Name + ")."]); }
        var errors = new List<string>(batch.Errors);
        errors.AddRange(batch.Processes.Where(p => p.Error != null).Select(p => $"PID {p.Id}: {p.Error}"));
        var selected = batch.Processes.Count == 1 ? batch.Processes[0] : null;
        var clientNames = batch.Processes.Select(p => Client(p.ExecutablePath)).Distinct().ToArray();
        var result = new Detection("Character not detected", "Server not detected",
            clientNames.Length == 0 ? "Not detected" : clientNames.Length == 1 ? clientNames[0] : "Multiple clients",
            batch.Processes.Count == 0 ? batch.Errors.Count == 0 ? "Not detected" : "Detection unavailable"
                : batch.Processes.Count == 1 ? "Running" : $"Running ({batch.Processes.Count} processes)",
            Installation(selected?.ExecutablePath))
        {
            ProcessId = selected?.Id, ExecutablePath = selected?.ExecutablePath, Processes = batch.Processes,
            Launcher = selected?.Launcher,
            ObservedAt = now, CharacterSource = identity.Description, ServerSource = identity.Description,
            LastSuccessfulIdentityUpdate = lastSuccessfulIdentityUpdate,
            IdentityStatus = !identity.IsAuthorized ? "Unavailable: no authorized live identity source configured. Configuration/account records cannot supply current identity."
                : selected == null ? "Unavailable: no unique running process selected." : "Waiting for a verified live identity."
        };
        if (batch.Processes.Count > 1) errors.Add("Multiple game processes; no active character/process is selected automatically.");
        if (selected != null && identity.IsAuthorized)
        {
            try
            {
                var observed = await identity.ReadAsync(selected, token);
                now = (clock ?? (() => DateTimeOffset.UtcNow))();
                result = result with { ObservedAt = now };
                if (observed == null)
                {
                    errors.Add("Authorized source returned no live identity.");
                    result = result with { IdentityStatus = "Unavailable: source returned no current identity." };
                }
                else if (observed.ProcessId != selected.Id || !ExecutablePaths.Equal(observed.ExecutablePath, selected.ExecutablePath)
                    || selected.StartedAt == null || selected.StartedAt != observed.ProcessStartedAt
                    || Math.Abs((now - observed.CapturedAt).TotalSeconds) > 20 || !observed.InGame)
                {
                    errors.Add("Live identity rejected: process association, freshness, or in-game state was not verified.");
                    result = result with { IdentityStatus = "Unavailable: snapshot rejected (process, age, or out-of-game state)." };
                }
                else
                {
                    var character = Clean(observed.Character);
                    var server = Clean(observed.ServerName) ?? (observed.ServerId > 0 ? $"Server ID {observed.ServerId} (name unavailable)" : null);
                    var available = character != null || server != null;
                    if (available) lastSuccessfulIdentityUpdate = observed.CapturedAt;
                    result = result with
                    {
                        Character = character ?? "Character not detected", Server = server ?? "Server not detected",
                        RuntimeIdentityAvailable = available, LastSuccessfulIdentityUpdate = lastSuccessfulIdentityUpdate,
                        IdentityStatus = available ? "Verified live snapshot; character and server resolved independently."
                            : "Unavailable: verified snapshot contains no character or server identity."
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { errors.Add("Live status source failed (" + e.GetType().Name + ")."); result = result with { IdentityStatus = "Unavailable: live source failed (" + e.GetType().Name + ")." }; }
        }
        return result with { Errors = errors.ToArray() };
    }
    private string Client(string? path)
    {
        if (path == null) return "Unknown (executable metadata unavailable)";
        try { return installations.IdentifyClient(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return "Unknown (invalid executable path)"; }
    }
    private static string? Installation(string? path)
    {
        if (path == null) return null;
        try { return Path.GetDirectoryName(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
    private static string? Clean(string? value)
    {
        if (value == null) return null;
        var clean = new string(value.Where(c => !char.IsControl(c)).Take(100).ToArray()).Trim();
        return clean.Length == 0 ? null : clean;
    }
}
