using System.Text.Json;
using System.IO;

namespace Hexor;

// Used by the customer CMD wrapper. It shares CleanupWorkflow with WPF and writes a report
// instead of presenting WPF UI, so the wrapper never implements deletion rules itself.
// Exit codes: 0 completed report, 1 operational failure, 2 invalid usage.
public static class CleanupCommandLine
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        var dxgiDiagnostics=args.Any(x=>x.Equals("--dxgi-diagnostics",StringComparison.OrdinalIgnoreCase));
        if (!dxgiDiagnostics&&!args.Any(x => x.Equals("--cleanup-report", StringComparison.OrdinalIgnoreCase))) return false;
        var outputIndex = Array.FindIndex(args, x => x.Equals("--output", StringComparison.OrdinalIgnoreCase));
        if (outputIndex < 0 || outputIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[outputIndex + 1]) || args.Count(x => x.Equals("--output", StringComparison.OrdinalIgnoreCase)) != 1) { exitCode = 2; return true; }
        string output;
        try { output = Path.GetFullPath(args[outputIndex + 1]); if (Directory.Exists(output)) { exitCode = 2; return true; } }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { exitCode = 2; return true; }
        try
        {
            if(dxgiDiagnostics)
            {
                var rootIndex=Array.FindIndex(args,x=>x.Equals("--scan-root",StringComparison.OrdinalIgnoreCase));
                IEnumerable<string>? roots=rootIndex>=0&&rootIndex+1<args.Length?new[]{args[rootIndex+1]}:null;
                var discovery=new AionInstallationDiscoveryService();var found=discovery.DiscoverOnFixedDrives(roots);
                var service=new PathMatchedDxgiCleanupService();var diagnosticItems=found.Select(service.Inspect).ToArray();
                WriteAtomically(output,new { status="completed",mode="dxgi-diagnostics",scannedRoots=roots??DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed&&d.IsReady).Select(d=>d.RootDirectory.FullName).ToArray(),installations=found,items=diagnosticItems });return true;
            }
            var reset = args.Any(x => x.Equals("--reset-updater-cache", StringComparison.OrdinalIgnoreCase));
            var execute = args.Any(x => x.Equals("--execute-cleanup", StringComparison.OrdinalIgnoreCase));
            var workflow = new CleanupWorkflow();
            var plan = reset ? workflow.PrepareCacheResetAsync().GetAwaiter().GetResult() : workflow.PrepareAsync().GetAwaiter().GetResult();
            var items = execute ? workflow.ExecuteAsync(plan).GetAwaiter().GetResult() : plan.Items;
            WriteAtomically(output, new CleanupCliReport("completed", execute, items, items.Count(x => x.State == VendorOverlayCleanupState.Removed), items.Count(x => x.State == VendorOverlayCleanupState.Restored), items.Count(x => x.State is VendorOverlayCleanupState.Skipped or VendorOverlayCleanupState.CouldNotVerify), items.Count(x => x.State is VendorOverlayCleanupState.Failed or VendorOverlayCleanupState.BlockedByRunningProcess), null));
            return true;
        }
        catch (Exception e)
        {
            try { WriteAtomically(output, new CleanupCliReport("failed", false, [], 0, 0, 0, 1, "Cleanup reporting failed: " + e.GetType().Name)); } catch { }
            exitCode = 1; return true;
        }
    }

    private static void WriteAtomically(string output, object report)
    {
        var directory = Path.GetDirectoryName(output); if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Output directory is invalid.");
        Directory.CreateDirectory(directory);
        var temporary = output + ".partial." + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); using var document = JsonDocument.Parse(File.ReadAllText(temporary)); if (File.Exists(output)) File.Move(temporary, output, true); else File.Move(temporary, output); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public sealed record CleanupCliReport(string Status, bool Executed, IReadOnlyList<CleanupPlanItem> Items, int Removed, int Restored, int Skipped, int Failed, string? Error);
