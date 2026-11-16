using System.IO;
using System.Text.Json.Nodes;
using Hexor;
using Hexor.Compatibility;

internal static class CustomerDiscoveryTests
{
    public static void Run(string temporaryRoot, RegistrySettingsService registry, Action<bool,string> check)
    {
        var drive = Path.Combine(temporaryRoot, "customer-drive");
        Directory.CreateDirectory(drive);
        var installed = Path.Combine(drive, Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(installed, "config"));
        var settings = Path.Combine(installed, "config", "settings.json");
        var machine = Path.Combine(installed, "config", "machine.json");
        string Document() => new JsonObject { ["FormatVersion"] = 1, ["Values"] = new JsonObject(), ["Workflow"] = WorkflowPlan.Default().ToJson(), ["WorkflowDefaultVersion"] = 2 }.ToJsonString();
        File.WriteAllText(settings, Document());
        var inspector = new ConfigurationInspector();
        ConfigurationResolution Find(string user) => new ConfigurationResolver(inspector).Resolve(LegionConfigurationLocator.Candidates(null, user, [drive]), null);
        foreach(var username in new[]{"Alice", "Customer Two", "客户"})
            check(Find(username).Selected?.DataRoot == installed, "Changed Windows metadata/username discovers existing GUID root: " + username);
        var cwd = Environment.CurrentDirectory;
        try
        {
            foreach(var folder in new[]{"Desktop", "Downloads", "Other install"})
            {
                var working = Path.Combine(temporaryRoot, folder); Directory.CreateDirectory(working); Environment.CurrentDirectory = working;
                check(Find("different-id").Selected?.DataRoot == installed, "Discovery independent of working directory: " + folder);
            }
        }
        finally { Environment.CurrentDirectory = cwd; }
        check(inspector.Inspect(installed).Machine.Availability == ConfigurationAvailability.Missing && Find("x").Selected != null, "Missing machine and SQLite do not block portable settings");
        var before = File.ReadAllBytes(settings); File.SetAttributes(settings, FileAttributes.ReadOnly);
        check(Find("x").Selected != null && before.SequenceEqual(File.ReadAllBytes(settings)), "Read-only configuration discovered without writes");
        File.SetAttributes(settings, FileAttributes.Normal);
        File.WriteAllText(settings, "{bad");
        check(Find("x").Selected?.Portable.Availability == ConfigurationAvailability.Invalid, "Invalid JSON retained and reported");
        File.Delete(settings); var machineDocument = JsonNode.Parse(Document())!; machineDocument["Workflow"] = new WorkflowPlan().ToJson(); File.WriteAllText(machine, machineDocument.ToJsonString());
        check(Find("x").Selected?.Machine.Availability == ConfigurationAvailability.Ready, "Machine-only initialization recognized safely");
        File.WriteAllText(settings, Document());
        var second = Path.Combine(drive, "Aion.ONL"); Directory.CreateDirectory(Path.Combine(second, "config")); File.WriteAllText(Path.Combine(second, "config", "settings.json"), Document());
        check(Find("x").Selected == null, "Multiple verified roots require confirmation");
        var preferences = new JsonConfigurationService(Path.Combine(temporaryRoot,"customer-preferences.json"), new SettingsBackupService());
        using(var vm = new MainViewModel(preferences, registry))
        {
            check(!vm.ConfirmConfigurationFolder(Path.Combine(temporaryRoot,"fresh")) && vm.Preferences.ConfigurationFolder == null, "Invalid manual selection is never cached");
            check(vm.ConfirmConfigurationFolder(installed) && preferences.Load().ConfigurationFolder == installed && vm.OriginalConfigurationReady, "Only confirmed valid folder cached and controls enabled");
        }
        File.Delete(settings); File.Delete(machine); File.Delete(Path.Combine(second,"config","settings.json"));
        check(Find("fresh-user").Selected == null && !File.Exists(settings) && !File.Exists(machine), "Fresh customer install gets guidance without fake documents");
    }
}

