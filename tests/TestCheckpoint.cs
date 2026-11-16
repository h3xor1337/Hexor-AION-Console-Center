using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;

internal static class TestCheckpoint
{
    // The regression gate observes these deadlines outside the WPF dispatcher,
    // captures a native dump on a stall, then terminates only its own test process.
    internal static void Write(string name, int timeoutSeconds = 30)
    {
        Console.WriteLine($"CHECKPOINT {name} timeout={timeoutSeconds}s");
        Console.Out.Flush();
    }

    internal static void Run(string name, Action action, int timeoutSeconds = 30)
    {
        Write(name, timeoutSeconds);
        action();
        Console.WriteLine("COMPLETED " + name);
    }

    internal static Application CreateApplication()
    {
        // App.Run also runs App.OnStartup, which opens a second customer window
        // and interactive OTA dialogs. Use the exact production resources only.
        using var source = typeof(TestCheckpoint).Assembly.GetManifestResourceStream("Hexor.Tests.ProductionApp.xaml")
            ?? throw new InvalidOperationException("Production application resources are missing.");
        var application = XDocument.Load(source).Root!;
        var ns = application.Name.Namespace;
        var dictionary = new XElement(ns + "ResourceDictionary",
            application.Attributes().Where(a => a.IsNamespaceDeclaration),
            application.Element(ns + "Application.Resources")!.Elements());
        return new Application { Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()) };
    }

    internal static void PreserveArtifacts(string root)
    {
        var destination = Environment.GetEnvironmentVariable("HEXOR_VALIDATION_ARTIFACTS");
        var source = Path.Combine(root, "artifacts");
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(source)) return;
        destination = Path.Combine(Path.GetFullPath(destination), Environment.ProcessId.ToString());
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
    }
}
