using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hexor;
using Hexor.Compatibility;

// Optional read-only validation command, separate from fixture-based automated tests.
internal static class LocalDetectionProbe
{
    public static int Run()
    {
        string? selected = null;
        var preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HexorAion", "preferences.json");
        if (File.Exists(preferences)) selected = JsonNode.Parse(File.ReadAllText(preferences))?["ConfigurationFolder"]?.GetValue<string>();
        var resolution = new ConfigurationResolver(new ConfigurationInspector()).Resolve(LegionConfigurationLocator.Candidates(selected), selected);
        var detected = new CharacterDetectionService(new GameInstallationDiscoveryService()).DetectAsync().GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Mode = "Read-only local inspection; no writes or IPC",
            SavedFolder = selected, ResolvedDataRoot = resolution.Selected?.DataRoot,
            Configuration = resolution.Selected?.Summary ?? resolution.Note,
            Resolution = resolution.Note, Files = resolution.Selected?.FileAvailability,
            ProcessCount = detected.Processes.Count, detected.ProcessId, detected.ExecutablePath, detected.Client, detected.Process,
            detected.Character, detected.Server, detected.CharacterSource, detected.ServerSource, detected.Errors,
            detected.RuntimeIdentityAvailable, detected.IdentityStatus, detected.LastSuccessfulIdentityUpdate, detected.ObservedAt,
            Association = resolution.Selected?.Association(detected), LiveCharacterServerValidated = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
