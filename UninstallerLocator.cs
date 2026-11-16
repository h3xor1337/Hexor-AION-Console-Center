using Microsoft.Win32;
using System.IO;

namespace Hexor;

public sealed record UninstallerLocation(string? ExecutablePath, string Message, bool IsRegistered);
public static class UninstallerLocator
{
    public static UninstallerLocation Find(string? applicationBaseDirectory = null, Func<IEnumerable<(string Name, string? Command)>>? registrations = null)
    {
        var entries = registrations?.Invoke() ?? ReadRegistrations();
        foreach (var entry in entries.Where(x => x.Name.Contains("608D23F2", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Hexor", StringComparison.OrdinalIgnoreCase)))
        {
            var path = CommandPath(entry.Command);
            if (path != null && File.Exists(path)) return new(path, "Registered AIO uninstaller found.", true);
        }
        if (!string.IsNullOrWhiteSpace(applicationBaseDirectory))
        {
            var parent = Directory.GetParent(applicationBaseDirectory)?.FullName;
            if (parent != null)
                foreach (var name in new[] { "unins000.exe", "unins001.exe" })
                { var path = Path.Combine(parent, name); if (File.Exists(path)) return new(path, "Local AIO uninstaller found.", true); }
        }
        return new(null, "This is a portable Hexor build; no registered AIO uninstaller is available. Portable files were not removed.", false);
    }
    private static IEnumerable<(string Name, string? Command)> ReadRegistrations()
    {
        using var root = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall");
        if (root == null) yield break;
        foreach (var name in root.GetSubKeyNames()) { using var key = root.OpenSubKey(name); yield return (name, key?.GetValue("QuietUninstallString") as string ?? key?.GetValue("UninstallString") as string); }
    }
    private static string? CommandPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim(); if (text.StartsWith('"')) { var end = text.IndexOf('"', 1); return end > 1 ? text[1..end] : null; }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); return exe < 0 ? null : text[..(exe + 4)];
    }
}
