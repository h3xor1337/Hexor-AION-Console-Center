using System.Security.Cryptography;
using System.IO;
using Hexor;

internal static class UpdaterCacheCleanupTests
{
    public static void Run(string root, Action<bool, string> assert)
    {
        var cache = Path.Combine(root, "Aion2Updater", "Cache", "V6", "fixture"); Directory.CreateDirectory(cache);
        var known = Path.Combine(cache, "AionDll.zip"); File.WriteAllText(known, "known fixture");
        var unknown = Path.Combine(cache, "customer.zip"); File.WriteAllText(unknown, "retain");
        var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Hash(known) };
        var service = new UpdaterCacheCleanupService(_ => false, approved);
        var plan = service.Inspect(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cache))!)!);
        assert(plan.Any(x => x.TargetPath == unknown && x.State == VendorOverlayCleanupState.CouldNotVerify), "Unknown updater cache archives are retained");
        var verified = plan.Single(x => x.TargetPath == known); var result = service.Execute(verified);
        assert(result.State == VendorOverlayCleanupState.Removed && !File.Exists(known), "Known disposable cache archive is removed and verified");
        File.WriteAllText(known, "changed fixture"); result = service.Execute(verified);
        assert(result.State == VendorOverlayCleanupState.CouldNotVerify && File.Exists(known), "Hash mismatch prevents cache deletion");
        var junctionTarget = Path.Combine(root, "outside"); Directory.CreateDirectory(junctionTarget); File.WriteAllText(Path.Combine(junctionTarget, "x.zip"), "outside");
        var link = Path.Combine(Path.GetDirectoryName(cache)!, "junction"); Directory.CreateSymbolicLink(link, junctionTarget);
        var linked = service.Inspect(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cache))!)!);
        assert(!linked.Any(x => x.TargetPath.Contains("junction", StringComparison.OrdinalIgnoreCase)), "Cache inspection does not traverse symlinks or junctions");
        assert(service.Inspect(Path.Combine(root, "missing-cache")).Single().State == VendorOverlayCleanupState.Skipped, "Missing cache directory is handled safely");
        var resetRoot=Path.Combine(root,"Aion2Updater","Cache","V7");Directory.CreateDirectory(resetRoot);var resetOne=Path.Combine(resetRoot,"one.zip");var resetTwo=Path.Combine(resetRoot,"two.bin");File.WriteAllText(resetOne,"one");File.WriteAllText(resetTwo,"two");
        var reset=service.InspectReset(resetRoot);assert(reset.Count==2&&reset.All(x=>x.Kind==CleanupItemKind.CacheReset&&x.ScopeRoot!=null),"Explicit cache reset previews all files only inside the verified cache root");
        var resetResults=reset.Select(service.ExecuteReset).ToArray();assert(resetResults.All(x=>x.State==VendorOverlayCleanupState.Removed)&&!File.Exists(resetOne)&&!File.Exists(resetTwo),"Explicit cache reset removes independently planned cache files");
        assert(service.ExecuteReset(reset[0]).State==VendorOverlayCleanupState.Skipped,"Cache reset is idempotent when a planned file is already absent");
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
