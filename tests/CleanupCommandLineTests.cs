using System.IO;
using System.Text.Json;
using Hexor;

internal static class CleanupCommandLineTests
{
    public static void Run(string root, Action<bool, string> assert)
    {
        assert(CleanupCommandLine.TryRun(["--cleanup-report"], out var usage) && usage == 2, "Cleanup report requires an explicit --output argument");
        var report = Path.Combine(root, "nested", "cleanup-preview.json");
        assert(CleanupCommandLine.TryRun(["--cleanup-report", "--output", report], out var success) && success == 0 && File.Exists(report), "Cleanup report writes a preview to an explicit output path");
        var parsed = JsonSerializer.Deserialize<CleanupCliReport>(File.ReadAllText(report));
        assert(parsed?.Status == "completed" && !parsed.Executed && parsed.Items != null, "Cleanup preview report is valid JSON and never executes cleanup");
        assert(!Directory.EnumerateFiles(Path.GetDirectoryName(report)!, "*.partial.*").Any(), "Cleanup report write leaves no partial file");
        assert(CleanupCommandLine.TryRun(["--cleanup-report", "--output", report, "--output", report], out var duplicate) && duplicate == 2, "Duplicate cleanup output arguments are rejected");
        var directory = Path.Combine(root, "report-directory"); Directory.CreateDirectory(directory);
        assert(CleanupCommandLine.TryRun(["--cleanup-report", "--output", directory], out var invalid) && invalid == 2, "Directory output paths are rejected without cleanup");
        var diagnostic=Path.Combine(root,"dxgi-diagnostic.json");
        assert(CleanupCommandLine.TryRun(["--dxgi-diagnostics","--output",diagnostic,"--scan-root",Path.Combine(root,"no-game")],out var diagnosticExit)&&diagnosticExit==0&&File.Exists(diagnostic),"dxgi diagnostics produce a read-only report for an explicit scan root");
    }
}
