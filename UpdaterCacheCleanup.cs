using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Hexor;

// Intentionally narrow: a cache file is never removable merely because it is below Cache.
public sealed class UpdaterCacheCleanupService
{
    public const string CacheFolderName = "Aion2Updater";
    private static readonly HashSet<string> ApprovedZipHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        "B4A2B74DD2ABC9D3AF464F879E8B6313E19B1827F603E8214AC3CDACFCA5072B",
        "267C805682DDB72D2FF147DF8BB6C0C1C47599DD36E8F104E8F21CA965F405C3"
    };
    private readonly Func<string, bool> processRunning;
    private readonly ISet<string> approvedHashes;
    public UpdaterCacheCleanupService(Func<string, bool>? processRunning = null, ISet<string>? approvedHashes = null)
    { this.processRunning = processRunning ?? IsProcessRunning; this.approvedHashes = approvedHashes ?? ApprovedZipHashes; }

    public IReadOnlyList<CleanupPlanItem> Inspect(string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot))
            return [new(CleanupItemKind.Cache, cacheRoot ?? "", VendorOverlayCleanupState.Skipped, "Updater cache directory was not found.")];
        if (HasReparsePoint(cacheRoot)) return [new(CleanupItemKind.Cache, cacheRoot, VendorOverlayCleanupState.CouldNotVerify, "Updater cache root is a reparse point; nothing was changed.")];
        var items = new List<CleanupPlanItem>();
        try
        {
            foreach (var file in EnumerateFilesWithoutReparseTraversal(cacheRoot))
            {
                if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var hash = Hash(file);
                items.Add(approvedHashes.Contains(hash)
                    ? new(CleanupItemKind.Cache, file, VendorOverlayCleanupState.Skipped, "Verified disposable updater cache archive.")
                    : new(CleanupItemKind.Cache, file, VendorOverlayCleanupState.CouldNotVerify, "Unknown updater cache archive was retained."));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { items.Add(new(CleanupItemKind.Cache, cacheRoot, VendorOverlayCleanupState.Failed, "Updater cache could not be inspected: " + e.GetType().Name + ".")); }
        return items.Count == 0 ? [new(CleanupItemKind.Cache, cacheRoot, VendorOverlayCleanupState.Skipped, "No verified disposable updater cache files were found.")] : items;
    }

    public CleanupPlanItem Execute(CleanupPlanItem item)
    {
        if (item.State != VendorOverlayCleanupState.Skipped || !item.Message.StartsWith("Verified disposable", StringComparison.Ordinal)) return item;
        if (new[] { "Aion2", "Aion2.Updater", "Aion2Updater.Launcher", "LegionCommander" }.Any(processRunning))
            return item with { State = VendorOverlayCleanupState.BlockedByRunningProcess, Message = "Close AION2 and the updater before cleanup." };
        try
        {
            if (!File.Exists(item.TargetPath) || HasReparsePoint(item.TargetPath) || !approvedHashes.Contains(Hash(item.TargetPath)))
                return item with { State = VendorOverlayCleanupState.CouldNotVerify, Message = "Cache file changed or could not be verified; it was retained." };
            File.Delete(item.TargetPath);
            return File.Exists(item.TargetPath)
                ? item with { State = VendorOverlayCleanupState.Failed, Message = "Verified cache file could not be removed." }
                : item with { State = VendorOverlayCleanupState.Removed, Message = "Verified updater cache archive was removed." };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return item with { State = VendorOverlayCleanupState.Failed, Message = "Verified cache file could not be removed: " + e.GetType().Name + "." }; }
    }

    // Reset is opt-in and may remove unknown archives, but never anything outside the exact Cache root.
    public IReadOnlyList<CleanupPlanItem> InspectReset(string cacheRoot)
    {
        if (!TryGetSafeRoot(cacheRoot, out var root, out var reason)) return [new(CleanupItemKind.CacheReset, cacheRoot ?? "", VendorOverlayCleanupState.CouldNotVerify, reason)];
        var items = new List<CleanupPlanItem>();
        try
        {
            foreach (var file in EnumerateFilesWithoutReparseTraversal(root))
                items.Add(new(CleanupItemKind.CacheReset, file, VendorOverlayCleanupState.Skipped, "Approved for explicit updater download-cache reset; downloads may be required again.", ScopeRoot: root));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { items.Add(new(CleanupItemKind.CacheReset, root, VendorOverlayCleanupState.Failed, "Updater cache reset preview failed: " + e.GetType().Name + ".", ScopeRoot: root)); }
        return items.Count == 0 ? [new(CleanupItemKind.CacheReset, root, VendorOverlayCleanupState.Skipped, "Verified updater cache is already empty.", ScopeRoot: root)] : items;
    }

    public CleanupPlanItem ExecuteReset(CleanupPlanItem item)
    {
        if (item.State != VendorOverlayCleanupState.Skipped || string.IsNullOrWhiteSpace(item.ScopeRoot) || item.Message.StartsWith("Verified updater cache is already", StringComparison.Ordinal)) return item;
        if (new[] { "Aion2", "Aion2.Updater", "Aion2Updater.Launcher", "LegionCommander" }.Any(processRunning)) return item with { State = VendorOverlayCleanupState.BlockedByRunningProcess, Message = "Locked or active: close AION2 and the updater before resetting its download cache." };
        try
        {
            if (!TryGetSafeRoot(item.ScopeRoot, out var root, out var reason) || !IsInsideRoot(root, item.TargetPath)) return item with { State = VendorOverlayCleanupState.CouldNotVerify, Message = "Unsafe path: " + (reason ?? "cache target was retained.") };
            if (!File.Exists(item.TargetPath)) return item with { State = VendorOverlayCleanupState.Skipped, Message = "Already absent; cache reset is idempotent." };
            if (HasReparsePoint(item.TargetPath)) return item with { State = VendorOverlayCleanupState.CouldNotVerify, Message = "Unsafe path: cache target is a reparse point." };
            File.Delete(item.TargetPath);
            return File.Exists(item.TargetPath) ? item with { State = VendorOverlayCleanupState.Failed, Message = "Failed to remove approved cache-reset file." } : item with { State = VendorOverlayCleanupState.Removed, Message = "Successfully removed from the explicitly requested updater download cache." };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return item with { State = VendorOverlayCleanupState.Failed, Message = "Locked or failed cache-reset file: " + e.GetType().Name + "." }; }
    }

    public static string DefaultCacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CacheFolderName, "Cache");
    private static IEnumerable<string> EnumerateFilesWithoutReparseTraversal(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (HasReparsePoint(entry)) continue;
                if (Directory.Exists(entry)) pending.Push(entry); else yield return entry;
            }
        }
    }
    private static bool TryGetSafeRoot(string? path, out string root, out string reason)
    {
        root = ""; reason = "Unsafe path: updater cache root was not found.";
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        root = Path.TrimEndingDirectorySeparator(new DirectoryInfo(path).FullName);
        if (HasReparsePoint(root)) { reason = "Unsafe path: updater cache root is a reparse point."; return false; }
        return true;
    }
    private static bool IsInsideRoot(string root, string target)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(target));
        return relative.Length > 0 && !Path.IsPathRooted(relative) && !relative.Equals("..", StringComparison.Ordinal) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static bool HasReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool IsProcessRunning(string name) { var processes = Process.GetProcessesByName(name); try { return processes.Length != 0; } finally { foreach (var process in processes) process.Dispose(); } }
}
