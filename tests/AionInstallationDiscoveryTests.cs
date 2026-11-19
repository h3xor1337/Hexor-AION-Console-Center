using Hexor;
using System.IO;

internal static class AionInstallationDiscoveryTests
{
    public static void Run(string root, Action<bool, string> assert)
    {
        var library = Path.Combine(root, "steam-library");
        var executable = Path.Combine(library, "steamapps", "common", "AION2", "Aion2", "Binaries", "Win64", "Aion2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); File.WriteAllText(executable, "fixture");
        var service = new AionInstallationDiscoveryService();
        var discovered = service.DiscoverFromSteamLibraries([library]);
        assert(discovered.Count == 1 && discovered[0].Client == AionInstallationClient.Steam && discovered[0].ExecutablePath == executable, "Steam discovery verifies installed metadata without a guessed app ID");
        var selected = service.VerifyUserSelectedInstallation(Path.Combine(library, "steamapps", "common", "AION2"));
        assert(selected?.Client == AionInstallationClient.UserSelected && selected.OverlayPath.EndsWith("Binaries\\Win64\\dxgi.dll"), "User-selected non-Steam locations require the exact verified executable layout");
        assert(service.VerifyUserSelectedInstallation(Path.Combine(root, "unverified-purple")) == null, "Unknown launcher paths are not fabricated as PURPLE installations");
        // SteamPath is stored with forward slashes on some installs; the same library must not
        // be discovered twice (it produced a false MultipleInstallations status).
        var forwardSlashLibrary = Path.Combine(root, "forward-slash-library").Replace('\\', '/');
        var executable2 = Path.Combine(forwardSlashLibrary, "steamapps", "common", "AION2", "Aion2", "Binaries", "Win64", "Aion2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable2)!); File.WriteAllText(executable2, "fixture");
        var duplicated = service.DiscoverFromSteamLibraries([forwardSlashLibrary, Path.Combine(root, "forward-slash-library")]);
        assert(duplicated.Count == 1, "A forward-slashed Steam library and its backslashed spelling resolve to one installation");
    }
}
