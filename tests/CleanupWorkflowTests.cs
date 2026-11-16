using System.IO;
using Hexor;

internal static class CleanupWorkflowTests
{
    public static void Run(string root, Action<bool, string> assert)
    {
        var service = new AionInstallationDiscoveryService();
        var empty = new CleanupWorkflow(service, new VendorOverlayCleanupService(_ => false), () => Path.Combine(root, "missing.json"), cacheRoot: () => Path.Combine(root, "missing-cache"));
        var plan = empty.PrepareAsync().GetAwaiter().GetResult();
        assert(plan.Items.Any(x => x.Kind == CleanupItemKind.Cache && x.State == VendorOverlayCleanupState.Skipped), "Cleanup plan without a cache directory is a safe no-op");
        var execution = empty.ExecuteAsync(plan).GetAwaiter().GetResult();
        assert(execution.Any(x => x.Kind == CleanupItemKind.Cache && x.State == VendorOverlayCleanupState.Skipped), "One-confirmation execution never deletes an unverified cache item");
    }
}
