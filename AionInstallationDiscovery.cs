using Microsoft.Win32;
using System.IO;
using System.Linq;

namespace Hexor;

public enum AionInstallationClient { Steam, UserSelected, Unknown }
public sealed record AionInstallation(string InstallationDirectory, string ExecutablePath, AionInstallationClient Client, string Evidence)
{
    public string OverlayPath => Path.Combine(InstallationDirectory, "Aion2", "Binaries", "Win64", "dxgi.dll");
}

// Discovery is intentionally evidence-led: no guessed Steam app ID, PURPLE registry key, or launcher path is used.
public sealed class AionInstallationDiscoveryService
{
    public IReadOnlyList<AionInstallation> DiscoverOnFixedDrives(IEnumerable<string>? roots=null)
    {
        var scanRoots=roots??DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed&&d.IsReady).Select(d=>d.RootDirectory.FullName);var found=new List<AionInstallation>();
        // Directory-only, depth-bounded scan: enumerating every file on every fixed drive takes
        // minutes on large drives and made the removal button look frozen. Game folders are found
        // by their AION2 folder shape (any layout), then validated by the executable path.
        foreach(var root in scanRoots.Where(Directory.Exists))foreach(var candidate in FindGameFolders(root)) { var install=VerifyUserSelectedInstallation(candidate); if(install is { } valid) found.Add(valid with { Client=AionInstallationClient.Unknown, Evidence="Validated fixed-drive scan"}); }
        return found.DistinctBy(x=>x.InstallationDirectory,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IEnumerable<string> FindGameFolders(string root){var found=new List<string>();var pending=new Stack<(string Path,int Depth)>();pending.Push((root,0));while(pending.Count>0){var(current,depth)=pending.Pop();IEnumerable<string> entries;try{entries=Directory.EnumerateDirectories(current);}catch{continue;}foreach(var entry in entries){try{if((File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0)continue;var name=Path.GetFileName(entry);if(name.Equals("AION2",StringComparison.OrdinalIgnoreCase)){var exe=Path.Combine(entry,"Aion2","Binaries","Win64","Aion2.exe");if(File.Exists(exe)){found.Add(entry);continue;}}if(depth<6)pending.Push((entry,depth+1));}catch{}}}return found;}
    public IReadOnlyList<AionInstallation> DiscoverFromSteamLibraries(IEnumerable<string> steamLibraries)
    {
        var found = new List<AionInstallation>();
        foreach (var library in steamLibraries.Where(Directory.Exists).Select(p => Path.GetFullPath(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var installation = Path.Combine(library, "steamapps", "common", "AION2");
            var executable = Path.Combine(installation, "Aion2", "Binaries", "Win64", "Aion2.exe");
            if (File.Exists(executable)) found.Add(new(installation, executable, AionInstallationClient.Steam, "Steam library folder plus Aion2\\Binaries\\Win64\\Aion2.exe"));
        }
        return found;
    }

    public AionInstallation? VerifyUserSelectedInstallation(string selectedInstallationDirectory)
    {
        if (string.IsNullOrWhiteSpace(selectedInstallationDirectory) || !Directory.Exists(selectedInstallationDirectory)) return null;
        var root = Path.GetFullPath(selectedInstallationDirectory);
        var executable = Path.Combine(root, "Aion2", "Binaries", "Win64", "Aion2.exe");
        return File.Exists(executable) ? new(root, executable, AionInstallationClient.UserSelected, "Customer-selected path contains the verified AION2 Win64 executable") : null;
    }

    public IReadOnlyList<string> ReadSteamLibraries()
    {
        var roots = new List<string>();
        using var steam = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam");
        // SteamPath is written with forward slashes on some installs; normalize so the same
        // library is not discovered twice under two spellings (false MultipleInstallations).
        if (steam?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath)) roots.Add(Path.GetFullPath(steamPath));
        foreach (var root in roots.ToArray())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try { roots.AddRange(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"").Select(m => m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\'))); }
            catch (IOException) { }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
