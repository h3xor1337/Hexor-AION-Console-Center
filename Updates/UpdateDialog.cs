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
        var shell = new Border { Background = Brush("#161220"), BorderBrush = Brush("#6F45AC"), BorderThickness = new(1), CornerRadius = new(18), Padding = new(28) };
        var root = new StackPanel(); shell.Child = root; Content = shell;
        root.Children.Add(Text("HEXOR CONTROL CENTER", 11, true, "#A77BE6"));
        root.Children.Add(Text("Hexor Update Available", 28, true, "#FFFFFF", new(0, 6, 0, 18)));
        var versions = new Grid { Margin = new(0, 0, 0, 18) }; versions.ColumnDefinitions.Add(new()); versions.ColumnDefinitions.Add(new());
        var installed = new StackPanel(); installed.Children.Add(Text("INSTALLED", 10, true, "#8F839F")); installed.Children.Add(Text(installedVersion, 18, true, "#F5F1FF")); versions.Children.Add(installed);
        var available = new StackPanel(); available.Children.Add(Text("AVAILABLE", 10, true, "#8F839F")); available.Children.Add(Text(release.Version, 18, true, "#D8B8FF")); Grid.SetColumn(available, 1); versions.Children.Add(available); root.Children.Add(versions);
        root.Children.Add(Text("WHAT'S NEW", 10, true, "#8F839F"));
        var releaseNotes = release.ReleaseNotes.Trim(); if (releaseNotes.Length > 900) releaseNotes = releaseNotes[..900] + "…";
        root.Children.Add(new TextBlock { Text = releaseNotes, Foreground = Brush("#E7E0EF"), TextWrapping = TextWrapping.Wrap, MaxHeight = 150, Margin = new(0, 6, 0, 14) });
        root.Children.Add(size); root.Children.Add(progress); root.Children.Add(stage); root.Children.Add(detail);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 22, 0, 0) };
        updateNow.Name = "UpdateNowButton"; updateNow.IsDefault = true; later.Name = "UpdateLaterButton"; later.IsCancel = true;
        actions.Children.Add(later); actions.Children.Add(updateNow); root.Children.Add(actions);
        later.Click += (_, _) => Close(); updateNow.Click += async (_, _) => await StartUpdate();
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
