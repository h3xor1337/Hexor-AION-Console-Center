using Hexor;
using Hexor.Updates;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class UpdateSessionTests
{
    internal static int InstanceChild(string name)
    {
        using var instance = new ApplicationInstance(name);
        return instance.IsPrimary ? 0 : 7;
    }

    internal static void Run(string root, Action<bool, string> check)
    {
        var session = new UpdateSession();
        var pending = new TaskCompletionSource();
        int checks = 0;
        using var joined = new CountdownEvent(200);
        var first = session.Run(true, () => { checks++; return pending.Task; });
        var followers = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        { var task = session.Run(i % 2 == 0, () => { Interlocked.Increment(ref checks); return Task.CompletedTask; }); joined.Signal(); return task; })).ToArray();
        check(checks == 1, "Concurrent startup/manual checks share one flight");
        // Wait until all worker calls have joined before releasing the flight.
        var joins = Enumerable.Range(0, 100).Select(i => Task.Run(() => {
            var task = session.Run(false, () => throw new Exception("Duplicate flight")); joined.Signal(); return task;
        })).ToArray();
        check(joined.Wait(TimeSpan.FromSeconds(10)), "Concurrent callers join without blocking on network work");
        pending.SetResult();
        Task.WhenAll(followers.Concat(joins).Append(first)).GetAwaiter().GetResult();
        session.Run(true, () => { checks++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        check(checks == 1, "Recreated startup panels do not repeat a completed startup check");
        session.Run(false, () => { checks++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        check(checks == 2, "Manual checks remain available after dismissal");
        try { session.Run(false, () => throw new InvalidOperationException("visible failure")).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { checks++; }
        session.Run(false, () => { checks++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        check(checks == 4, "Check errors propagate and release the flight for retry");

        var release = new Manifest(1,"9.0.0","2026-10-30T00:00:00Z","https://github.com/hexor-tests/hexor-updates/releases/download/v9.0.0/Hexor-9.0.0-win-x64.zip",new string('a',64),"Test","1.0.0",false);
        var owner = new Window(); var other = new Window(); owner.Show(); other.Show();
        int dialogs = 0, installs = 0;
        var install = new TaskCompletionSource();
        var modalSession = new UpdateSession();
        var popup = new UpdateAvailableDialog("1.0.13", release, _ => { installs++; return install.Task; }) { Owner = owner };
        popup.Loaded += (_, _) => popup.Dispatcher.BeginInvoke(new Action(() => {
            var follower = modalSession.Run(false, () => { dialogs++; return Task.CompletedTask; });
            check(!follower.IsCompleted && dialogs == 1, "Second window/manual check during modal dispatcher reentrancy joins existing dialog");
            var button = FindButton(popup,"UpdateNowButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            popup.Close();
            check(installs == 1 && popup.IsVisible, "Duplicate Update Now actions start one transaction and closing cannot abandon it");
            popup.ShowFailure("Test failure","Test failure");
            install.SetResult(); popup.Close();
        }));
        modalSession.Run(true, () => { dialogs++; popup.ShowDialog(); return Task.CompletedTask; }).GetAwaiter().GetResult();
        check(dialogs == 1, "Only one dialog is constructed across multiple windows");
        owner.Close(); other.Close();

        var name = @"Local\Hexor.Instance.Test." + Guid.NewGuid().ToString("N");
        using (var primary = new ApplicationInstance(name)) {
            check(primary.IsPrimary && Child(name) == 7, "Second process cannot acquire the UI instance guard");
        }
        check(Child(name) == 0, "UI instance guard is released on exit");
    }
    static int Child(string name)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false, CreateNoWindow=true };
        start.ArgumentList.Add("--instance-child"); start.ArgumentList.Add(name);
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(10000)) { child.Kill(); throw new TimeoutException("Instance child stalled"); }
        return child.ExitCode;
    }
    static Button FindButton(System.Windows.DependencyObject parent, string name)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent))
            if (child is Button b && b.Name == name) return b;
            else if (child is System.Windows.DependencyObject d) { try { return FindButton(d,name); } catch (InvalidOperationException) { } }
        throw new InvalidOperationException("Button not found");
    }
}
