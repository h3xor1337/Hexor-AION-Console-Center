using Hexor.Runtime;

// Launcher provenance checks for the Purple-aware game process detection.
// Pure mapping functions are verified here; the Toolhelp32 snapshot path is exercised
// end-to-end through the live detection tests and the visual monitor's provenance summary.
internal static class LauncherProvenanceTests
{
    public static void Run(Action<bool, string> check)
    {
        Attribution(check);
        GameNameMatching(check);
        SnapshotSafety(check);
    }

    private static void Attribution(Action<bool, string> check)
    {
        check(LauncherProvenance.ForParentProcessName("Purple.exe") == LauncherProvenance.Purple,
            "A Purple.exe parent attributes the game process to the Purple launcher");
        check(LauncherProvenance.ForParentProcessName("purple") == LauncherProvenance.Purple,
            "Parent attribution is case-insensitive for the Purple launcher");
        check(LauncherProvenance.ForParentProcessName("PurpleLauncher") == LauncherProvenance.Purple,
            "Any image name starting with Purple attributes the Purple launcher (PurpleLauncher.ini verified in provider binary)");
        check(LauncherProvenance.ForParentProcessName("steam.exe") == null,
            "A Steam parent does not claim Purple provenance");
        check(LauncherProvenance.ForParentProcessName(null) == null,
            "A missing parent name is reported as unidentified instead of guessed");
        check(LauncherProvenance.ForParentProcessName("explorer") == null,
            "An unrelated parent process stays unidentified");
    }

    private static void GameNameMatching(Action<bool, string> check)
    {
        check(LauncherProvenance.IsGameProcessName("AION2"), "Steam-cased AION2 matches the game process name");
        check(LauncherProvenance.IsGameProcessName("Aion2"), "Purple-cased Aion2 matches the game process name");
        check(LauncherProvenance.IsGameProcessName("aion2"), "Lower-case aion2 matches the game process name");
        check(!LauncherProvenance.IsGameProcessName("Aion2Updater"), "The updater image name is not the game client");
        check(!LauncherProvenance.IsGameProcessName("Aion2Updater.Launcher"), "The launcher image name is not the game client");
        check(!LauncherProvenance.IsGameProcessName(null), "A null image name is never a game process");
        check(!LauncherProvenance.IsGameProcessName("LegionCommander"), "The provider binary itself is not a game process");
    }

    private static void SnapshotSafety(Action<bool, string> check)
    {
        // The live snapshot path must never throw and must attribute the current process's own
        // parent only when that parent genuinely starts with "Purple" (it will not in tests).
        var map = LauncherProvenance.BuildParentMap();
        check(map.Count > 0, "The Toolhelp32 parent snapshot enumerates the running system");
        var self = map.TryGetValue(Environment.ProcessId, out var parent) ? parent : 0;
        var resolved = LauncherProvenance.Resolve([Environment.ProcessId]);
        check(resolved.Count == 1, "Provenance resolution covers every requested pid");
        check(resolved[Environment.ProcessId] == null || map.ContainsKey(self),
            "Self-attribution is either unidentified or backed by a real parent entry");
        check(LauncherProvenance.Resolve([]).Count == 0, "An empty pid set resolves without a snapshot");
    }
}
