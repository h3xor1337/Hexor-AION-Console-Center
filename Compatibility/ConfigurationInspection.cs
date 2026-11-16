using System.IO;
using Hexor.Runtime;

namespace Hexor.Compatibility;

public sealed record ConfigurationFileInspection(string Path, ConfigurationAvailability Availability, string Detail);
public sealed record ConfigurationInspection(string DataRoot, ConfigurationFileInspection Portable,
    ConfigurationFileInspection Machine, ConfigurationFileInspection Database, ConfigurationSnapshot? MachineDocument)
{
    public bool HasEvidence => new[] { Portable, Machine, Database }.Any(f => f.Availability != ConfigurationAvailability.Missing);
    public string Summary => Portable.Availability switch
    {
        ConfigurationAvailability.Ready => "Configuration found",
        ConfigurationAvailability.Invalid => "Configuration invalid",
        ConfigurationAvailability.Unavailable => "Configuration inaccessible",
        ConfigurationAvailability.Unsupported => "Configuration unsupported",
        _ => "Configuration not found"
    };
    public string FileAvailability => string.Join(Environment.NewLine, new[] { Portable, Machine, Database }
        .Select(f => $"{System.IO.Path.GetFileName(f.Path)}: {State(f.Availability)}"));
    public IReadOnlyList<string> Errors => new[] { Portable, Machine, Database }
        .Where(f => f.Availability is ConfigurationAvailability.Invalid or ConfigurationAvailability.Unavailable or ConfigurationAvailability.Unsupported)
        .Select(f => $"{System.IO.Path.GetFileName(f.Path)}: {f.Detail}").ToArray();
    private static string State(ConfigurationAvailability value) => value switch
    {
        ConfigurationAvailability.Ready => "Found", ConfigurationAvailability.Missing => "Not found",
        ConfigurationAvailability.Invalid => "Invalid", ConfigurationAvailability.Unavailable => "Inaccessible", _ => "Unsupported format"
    };
    public string Association(Detection runtime)
    {
        if (runtime.Processes.Count == 0) return "No running game to associate. Configuration availability is independent of process status.";
        if (runtime.Processes.Count != 1) return "Not associated: multiple game processes; active process is unverified.";
        if (runtime.ExecutablePath == null) return "Not associated: executable metadata is inaccessible.";
        if (MachineDocument?.Availability != ConfigurationAvailability.Ready)
            return "Not associated: machine.json is not readable and valid. This installation-path check does not gate live identity detection.";
        if (MachineDocument?.Availability == ConfigurationAvailability.Ready)
        {
            foreach (var key in LegionConfigurationService.MachineKeys)
                if (ExecutablePaths.Equal(MachineDocument.Value(key), runtime.ExecutablePath))
                    return "Installation path matches machine.json; active account/character association remains unverified.";
        }
        var configured = LegionConfigurationService.MachineKeys.Where(key => !string.IsNullOrWhiteSpace(MachineDocument!.Value(key))).ToArray();
        return configured.Length == 0
            ? "Not associated: machine.json has no configured game executable paths. This installation-path check does not gate live identity detection."
            : "Not associated: configured machine.json game paths do not match this executable. This installation-path check does not gate live identity detection.";
    }
}

public interface IConfigurationInspector { ConfigurationInspection Inspect(string rootOrConfigFolder); }
public sealed class ConfigurationInspector : IConfigurationInspector
{
    public ConfigurationInspection Inspect(string rootOrConfigFolder)
    {
        var store = new LegionConfigurationService(rootOrConfigFolder);
        var portable = store.Read(); var machine = store.Read(true);
        var root = Directory.GetParent(store.Folder.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var database = Path.Combine(root, "db", "accounts.db");
        return new(root, new(portable.Path, portable.Availability, portable.Message),
            new(machine.Path, machine.Availability, machine.Message), ProbeDatabase(database), machine);
    }
    private static ConfigurationFileInspection ProbeDatabase(string path)
    {
        try
        {
            // Availability only: do not parse the database or read account records.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new(path, ConfigurationAvailability.Ready, "File can be opened read-only; database schema/content not queried.");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { return new(path, ConfigurationAvailability.Missing, "File not found."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(path, ConfigurationAvailability.Unavailable, "File access unavailable (" + e.GetType().Name + ")."); }
    }
}

public sealed record ConfigurationResolution(ConfigurationInspection? Selected, IReadOnlyList<ConfigurationInspection> Candidates, string Note);
public sealed class ConfigurationResolver(IConfigurationInspector inspector)
{
    public ConfigurationResolution Resolve(IReadOnlyList<string> candidates, string? preferred)
    {
        var inspected = candidates.Select(inspector.Inspect).GroupBy(i => i.DataRoot, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        ConfigurationInspection? chosen = null;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try
            {
                var preferredFolder = new LegionConfigurationService(preferred).Folder;
                chosen = inspected.FirstOrDefault(i => StringComparer.OrdinalIgnoreCase.Equals(Path.Combine(i.DataRoot, "config"), preferredFolder));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
            if (chosen?.HasEvidence == true) return new(chosen, inspected, "Selected folder retained; errors are reported without switching to another profile.");
        }
        var ready = inspected.Where(i => i.Portable.Availability == ConfigurationAvailability.Ready).ToArray();
        // An inaccessible candidate might be another valid profile. Do not choose around it silently.
        var uncertain = inspected.Any(i => i.Portable.Availability == ConfigurationAvailability.Unavailable);
        if (ready.Length == 1 && !uncertain)
            return new(ready[0], inspected, chosen != null
                ? "Saved folder contains no portable configuration. Found the sole verified metadata candidate; saved preferences were not rewritten."
                : "Found the sole verified portable configuration candidate.");
        if (ready.Length > 1 || (ready.Length == 1 && uncertain))
            return new(null, inspected, "Multiple or inaccessible candidate roots; select the intended data/config folder explicitly.");
        var existing = inspected.Where(i => i.HasEvidence).ToArray();
        if (existing.Length == 1) return new(existing[0], inspected, "Existing configuration evidence found; file errors are shown below.");
        return new(null, inspected, existing.Length > 1 ? "Multiple incomplete settings folders found. Choose the intended folder in Advanced settings." : "Settings not found. Make sure your automation software has completed its initial setup. You can also choose its settings folder in Advanced settings.");
    }
}
