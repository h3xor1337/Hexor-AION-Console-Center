using System.IO;
using System.Linq;

namespace Hexor;

public enum CleanupItemKind { Overlay, Cache, CacheReset, PathMatchedDxgi, UpdaterData, UpdaterRegistry }
public sealed record CleanupPlanItem(CleanupItemKind Kind, string TargetPath, VendorOverlayCleanupState State, string Message, string? GameExecutablePath = null, string? ScopeRoot = null, string? ExpectedFingerprint = null);
public sealed record CleanupPlan(IReadOnlyList<CleanupPlanItem> Items)
{
    public int RemovableCount => Items.Count(x => x.State == VendorOverlayCleanupState.Skipped && x.Message.StartsWith("Verified", StringComparison.Ordinal));
    public string CustomerSummary => RemovableCount == 0 ? "No verified components are available to remove." : $"{RemovableCount} verified component(s) are ready for cleanup.";
}

// Orchestrates existing verification; it does not infer ownership or scan arbitrary drives.
public sealed class CleanupWorkflow
{
    private readonly AionInstallationDiscoveryService installations;
    private readonly VendorOverlayCleanupService overlays;
    private readonly UpdaterCacheCleanupService cache;
    private readonly PathMatchedDxgiCleanupService pathDxgi;
    private readonly UpdaterDataCleanupService updaterData;
    private readonly Func<string> manifestPath;
    private readonly Func<string> cacheRoot;
    public CleanupWorkflow(AionInstallationDiscoveryService? installations = null, VendorOverlayCleanupService? overlays = null, Func<string>? manifestPath = null, UpdaterCacheCleanupService? cache = null, Func<string>? cacheRoot = null, PathMatchedDxgiCleanupService? pathDxgi = null,UpdaterDataCleanupService? updaterData=null)
    { this.installations = installations ?? new(); this.overlays = overlays ?? new(); this.cache = cache ?? new(); this.pathDxgi=pathDxgi??new();this.updaterData=updaterData??new(); this.manifestPath = manifestPath ?? new Func<string>(() => Path.Combine(AppLayout.InstallParent() ?? "", "AION Auto Updater", VendorOverlayCleanupService.ManifestName)); this.cacheRoot = cacheRoot ?? new Func<string>(() => UpdaterCacheCleanupService.DefaultCacheRoot); }

    public Task<CleanupPlan> PrepareAsync(Action<string>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        progress?.Invoke("Finding installations"); cancellationToken.ThrowIfCancellationRequested();
        var found = installations.DiscoverFromSteamLibraries(installations.ReadSteamLibraries()).DistinctBy(x => x.InstallationDirectory, StringComparer.OrdinalIgnoreCase).ToArray();
        progress?.Invoke("Checking installed components");
        var items = found.Select(x => { cancellationToken.ThrowIfCancellationRequested(); var result = overlays.Inspect(x.ExecutablePath, manifestPath()); return new CleanupPlanItem(CleanupItemKind.Overlay, result.TargetPath, result.State, result.Message, x.ExecutablePath); }).ToList();
        items.AddRange(cache.Inspect(cacheRoot()));
        progress?.Invoke("Preparing cleanup"); return new CleanupPlan(NormalizeDistinct(items));
    }, cancellationToken);

    public Task<IReadOnlyList<CleanupPlanItem>> ExecuteAsync(CleanupPlan plan, Action<string>? progress = null, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<CleanupPlanItem>>(() =>
    {
        progress?.Invoke("Removing verified components"); var results = new List<CleanupPlanItem>();
        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Kind == CleanupItemKind.Cache) { results.Add(cache.Execute(item)); continue; }
            if (item.Kind == CleanupItemKind.CacheReset) { results.Add(cache.ExecuteReset(item)); continue; }
            if (item.Kind == CleanupItemKind.PathMatchedDxgi) { results.Add(pathDxgi.Execute(item)); continue; }
            if (item.Kind is CleanupItemKind.UpdaterData or CleanupItemKind.UpdaterRegistry) { results.Add(updaterData.Execute(item)); continue; }
            if (item.Kind != CleanupItemKind.Overlay || item.GameExecutablePath == null || item.State != VendorOverlayCleanupState.Skipped || !item.Message.StartsWith("Verified", StringComparison.Ordinal)) { results.Add(item); continue; }
            var result = overlays.Execute(item.GameExecutablePath, manifestPath()); results.Add(new(item.Kind, result.TargetPath, result.State, result.Message, item.GameExecutablePath));
        }
        progress?.Invoke("Finishing"); return results;
    }, cancellationToken);

    public Task<CleanupPlan> PreparePathMatchedDxgiAsync(Action<string>? progress=null,CancellationToken cancellationToken=default)=>Task.Run(()=>{progress?.Invoke("Scanning fixed drives for validated AION2 installations");var found=installations.DiscoverOnFixedDrives().DistinctBy(x=>x.InstallationDirectory,StringComparer.OrdinalIgnoreCase);return new CleanupPlan(NormalizeDistinct(found.Select(pathDxgi.Inspect)));},cancellationToken);
    public Task<CleanupPlan> PrepareUnifiedAsync(Action<string>? progress=null,CancellationToken cancellationToken=default)=>Task.Run(()=>{progress?.Invoke("Scanning fixed drives and updater-owned data");var found=installations.DiscoverOnFixedDrives();var items=found.Select(pathDxgi.Inspect).Concat(updaterData.Inspect());return new CleanupPlan(NormalizeDistinct(items));},cancellationToken);

    // Deliberately separate from PrepareAsync: callers must explicitly request this broader cache reset.
    public Task<CleanupPlan> PrepareCacheResetAsync(Action<string>? progress = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        progress?.Invoke("Preparing updater download-cache reset"); cancellationToken.ThrowIfCancellationRequested();
        return new CleanupPlan(NormalizeDistinct(cache.InspectReset(cacheRoot())));
    }, cancellationToken);

    private static IReadOnlyList<CleanupPlanItem> NormalizeDistinct(IEnumerable<CleanupPlanItem> items) => items
        .GroupBy(item => item.Kind + "|" + (string.IsNullOrWhiteSpace(item.TargetPath) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.TargetPath))), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToArray();
}
