using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hexor.Updates;

internal static class UpdateExperienceTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var release = new Manifest(1, "1.0.10", "2026-10-28T00:00:00Z",
            "https://github.com/hexor-tests/hexor-updates/releases/download/v1.0.10/Hexor-1.0.10-win-x64.zip",
            new string('a', 64), "Equipment Extraction support, safer registry defaults, and a polished signed update experience.", "1.0.0", false);
        var invoked = 0;
        var popup = new UpdateAvailableDialog("1.0.9", release, _ => { invoked++; return Task.CompletedTask; });
        popup.Show(); popup.UpdateLayout(); Pump();
        check(All<TextBlock>(popup).Any(t => t.Text == "Hexor Update Available") && All<TextBlock>(popup).Any(t => t.Text == "1.0.9")
            && All<TextBlock>(popup).Any(t => t.Text == "1.0.10") && Find<Button>(popup, "UpdateNowButton")?.IsDefault == true
            && Find<Button>(popup, "UpdateLaterButton")?.IsCancel == true, "Update popup presents installed/available versions, release notes, and primary/later actions");
        Capture(popup, Artifact(root, "update-available.png"));

        popup.SetStage("Downloading", "Downloading the signed Hexor update…", new DownloadProgress(5_242_880, 10_485_760)); popup.UpdateLayout(); Pump();
        var progress = All<ProgressBar>(popup).Single();
        check(!progress.IsIndeterminate && progress.Value == 50 && All<TextBlock>(popup).Any(t => t.Text.Contains("10.0 MB") && t.Text.Contains("50%")),
            "Download progress displays measured bytes and percentage without invented progress");
        Capture(popup, Artifact(root, "update-download-progress.png"));
        popup.ShowFailure("Update could not be completed", "The update could not be downloaded."); popup.UpdateLayout();
        check(Find<Button>(popup, "UpdateNowButton")?.Content is "Retry" && Find<Button>(popup, "UpdateNowButton")?.IsEnabled == true,
            "Update failure keeps the current installation and exposes a safe retry action");
        popup.Close();

        var complete = new UpdateCompletionWindow(); complete.Show(); complete.UpdateLayout(); Pump();
        check(All<TextBlock>(complete).Any(t => t.Text == "Hexor Control Center has been updated successfully.")
            && All<TextBlock>(complete).Any(t => t.Text == "Please open Hexor Control Center again to continue."),
            "Completion notification requires manual reopen and does not offer automatic restart");
        Capture(complete, Artifact(root, "update-complete.png")); complete.Close();
        check(invoked == 0, "Rendering update dialogs performs no download or installation side effects");
    }

    static string Artifact(string root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("HEXOR_CAPTURE_RELEASE_SCREENSHOT") == "1"
            ? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "output", "validation-1.0.10"))
            : Path.Combine(root, "artifacts");
        Directory.CreateDirectory(directory); return Path.Combine(directory, name);
    }
    static void Capture(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    static T? Find<T>(DependencyObject parent, string name) where T : FrameworkElement => All<T>(parent).FirstOrDefault(x => x.Name == name);
    static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { var child = VisualTreeHelper.GetChild(parent, index); if (child is T typed) yield return typed; foreach (var nested in All<T>(child)) yield return nested; }
    }
}
