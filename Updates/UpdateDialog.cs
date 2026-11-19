using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hexor.Updates;

public sealed class UpdateAvailableDialog : Window
{
    readonly TextBlock stage = Text("Update available", 13, true, "#DCC8FF");
    readonly TextBlock detail = Text("Choose Update Now to download and install automatically.", 12, false, "#B9ADCA");
    readonly TextBlock size = Text("Update size: available when the download begins", 12, false, "#B9ADCA");
    readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 8, Margin = new(0, 14, 0, 8), Foreground = Brush("#9667E7"), Background = Brush("#302442") };
    readonly Button updateNow = Button("Update Now", "#7849B5");
    readonly Button later = Button("Later", "#302442");
    readonly Func<UpdateAvailableDialog, Task> beginUpdate;
    bool running;
    bool completed;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (running && !completed) e.Cancel = true;
    }
    internal void CompleteAndClose() { completed = true; Close(); }

    public UpdateAvailableDialog(string installedVersion, Manifest release, Func<UpdateAvailableDialog, Task> beginUpdate)
    {
        this.beginUpdate = beginUpdate;
        Title = "Hexor Update Available"; Width = 610; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Title = "Hexor Update Available"; Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        // Elevation reads through layered shadow, not a heavier border; the border stays for structure.
        var shell = new Border
        {
            Background = Brush("#161220"), BorderBrush = Brush("#6F45AC"), BorderThickness = new(1),
            CornerRadius = new(18), Padding = new(32, 26, 32, 28),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 42, ShadowDepth = 10, Opacity = 0.45, Color = ((Color)ColorConverter.ConvertFromString("#0B0614")) },
        };
        var root = new StackPanel(); shell.Child = root; Content = shell;
        root.Children.Add(Text("HEXOR CONTROL CENTER", 11, true, "#B08CE8", new(0, 0, 0, 4)));
        root.Children.Add(new TextBlock { Text = "Hexor Update Available", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brush("#FFFFFF"), LineHeight = 31, Margin = new(0, 0, 0, 20) });
        var versions = new Grid { Margin = new(0, 0, 0, 20) }; versions.ColumnDefinitions.Add(new()); versions.ColumnDefinitions.Add(new());
        var installed = new StackPanel(); installed.Children.Add(Text("INSTALLED", 10, true, "#9C90B0")); installed.Children.Add(new TextBlock { Text = installedVersion, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush("#F5F1FF"), LineHeight = 23 }); versions.Children.Add(installed);
        var available = new StackPanel(); available.Children.Add(Text("AVAILABLE", 10, true, "#9C90B0")); available.Children.Add(new TextBlock { Text = release.Version, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush("#D8B8FF"), LineHeight = 23 }); Grid.SetColumn(available, 1); versions.Children.Add(available); root.Children.Add(versions);
        root.Children.Add(Text("WHAT'S NEW", 10, true, "#9C90B0", new(0, 0, 0, 2)));
        root.Children.Add(BuildReleaseNotes(release.ReleaseNotes));
        root.Children.Add(size); root.Children.Add(progress); root.Children.Add(stage); root.Children.Add(detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        updateNow.Name = "UpdateNowButton"; updateNow.IsDefault = true; later.Name = "UpdateLaterButton"; later.IsCancel = true;
        actions.Children.Add(later); actions.Children.Add(updateNow); root.Children.Add(actions);
        later.Click += (_, _) => Close(); updateNow.Click += async (_, _) => await StartUpdate();
        // Keyboard entry point and a short, restrained enter motion (skipped when animations are off).
        Loaded += (_, _) => { updateNow.Focus(); if (SystemParameters.ClientAreaAnimation) { var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, System.Windows.Duration.Automatic) { Duration = TimeSpan.FromMilliseconds(150), EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } }; var slide = new System.Windows.Media.Animation.ThicknessAnimation { From = new Thickness(0, 14, 0, 0), To = new Thickness(0), Duration = TimeSpan.FromMilliseconds(150), EasingFunction = fade.EasingFunction }; BeginAnimation(OpacityProperty, fade); shell.BeginAnimation(MarginProperty, slide); } };
    }

    // Changelog lines arrive as lightweight markdown ("#", "##", "- " bullets). Render them as a
    // readable list instead of dumping the raw source; internal details never appear by design.
    static System.Windows.Controls.StackPanel BuildReleaseNotes(string notes)
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new(0, 6, 0, 14) };
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { panel.Children.Add(new System.Windows.Controls.TextBlock { Height = 6 }); continue; }
            var isHeading = line.StartsWith('#');
            line = line.TrimStart('#').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("- ") || line.StartsWith("* ")) line = "•  " + line[2..].Trim();
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = line,
                FontSize = isHeading ? 14 : 12.5,
                FontWeight = isHeading || !line.StartsWith("•") ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(isHeading ? "#D8B8FF" : "#E7E0EF"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new(isHeading ? 0 : 10, 0, 0, isHeading ? 2 : 3),
            });
        }
        var viewer = new ScrollViewer { MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        var host = new System.Windows.Controls.StackPanel(); host.Children.Add(viewer);
        return host;
    }

    async Task StartUpdate()
    {
        if (running) return; running = true; updateNow.IsEnabled = later.IsEnabled = false;
        try { await beginUpdate(this); }
        catch (Exception e) { ShowFailure("Update could not be completed", e.Message); }
    }

    public void SetStage(string name, string message, DownloadProgress? value = null)
    {
        stage.Text = name; detail.Text = message;
        if (value?.TotalBytes is > 0)
        {
            progress.IsIndeterminate = false; progress.Value = value.Percentage ?? 0;
            size.Text = $"Update size: {FormatBytes(value.TotalBytes.Value)} · Downloaded {FormatBytes(value.BytesReceived)} ({value.Percentage:0}%)";
        }
        else { progress.Value = 0; progress.IsIndeterminate = true; }
    }

    public void ShowFailure(string heading, string message)
    {
        running = false; progress.IsIndeterminate = false; progress.Value = 0; stage.Text = heading;
        detail.Text = message + " The current Hexor installation was not replaced. Check your connection or permissions, then retry.";
        updateNow.Content = "Retry"; updateNow.IsEnabled = later.IsEnabled = true;
    }

    static string FormatBytes(long value) => value >= 1024 * 1024 ? $"{value / 1024d / 1024d:0.0} MB" : $"{value / 1024d:0.0} KB";
    static TextBlock Text(string value, double font, bool bold, string color, Thickness? margin = null) => new() { Text = value, FontSize = font, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = margin ?? new() };
    static Button Button(string value, string background) => new() { Content = value, MinWidth = 120, MinHeight = 42, Margin = new(8, 0, 0, 0), Padding = new(18, 9, 18, 9), Foreground = Brushes.White, Background = Brush(background), BorderThickness = new(0), Cursor = System.Windows.Input.Cursors.Hand };
    static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

public sealed class UpdateCompletionWindow : Window
{
    public UpdateCompletionWindow()
    {
        Title = "Hexor Update Complete"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        var root = new StackPanel(); var shell = new Border { Background = Brush("#161220"), BorderBrush = Brush("#6F45AC"), BorderThickness = new(1), CornerRadius = new(18), Padding = new(30), Child = root }; Content = shell;
        root.Children.Add(Text("✓", 34, true, "#B98AFF")); root.Children.Add(Text("Update complete", 26, true, "#FFFFFF"));
        root.Children.Add(Text("Hexor Control Center has been updated successfully.", 15, true, "#F5F1FF", new(0, 16, 0, 8)));
        root.Children.Add(Text("Please open Hexor Control Center again to continue.", 13, false, "#B9ADCA"));
        var close = new Button { Name = "CloseCompletionButton", Content = "Close", MinWidth = 120, MinHeight = 42, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 24, 0, 0), Foreground = Brushes.White, Background = Brush("#7849B5"), BorderThickness = new(0) };
        close.Click += (_, _) => Close(); root.Children.Add(close);
    }
    public static void ShowAfterUpdate()
    {
        Exception? failure = null; var thread = new Thread(() => { try { new UpdateCompletionWindow().ShowDialog(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure != null) throw new InvalidOperationException("Completion notification failed.", failure);
    }
    static TextBlock Text(string value, double font, bool bold, string color, Thickness? margin = null) => new() { Text = value, FontSize = font, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = margin ?? new() };
    static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}
