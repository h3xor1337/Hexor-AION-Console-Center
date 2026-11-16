using System.Windows;
namespace Hexor;
public partial class App : Application
{
 ApplicationInstance? instance;
 internal static string ShutdownOrigin { get; private set; } = "window-close-or-external";
 internal static void RecordShutdownOrigin(string origin)
 {
  ShutdownOrigin=origin is "titlebar-close" or "alt-f4" or "session-ending" or "cleanup-command" or "update-gate" or "startup-recovery" or "ota-mandatory-declined" or "ota-install" ? origin : "window-close-or-external";
  Diagnostics.Log("application-lifecycle","shutdown-requested; source="+ShutdownOrigin);
 }
 internal static void RequestShutdown(string origin)
 {
  RecordShutdownOrigin(origin);Application.Current?.Shutdown();
 }
 protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
 {
  RecordShutdownOrigin("session-ending");Diagnostics.Log("application-lifecycle","session-ending; reason="+e.ReasonSessionEnding);base.OnSessionEnding(e);
 }
 protected override void OnExit(ExitEventArgs e)
 {
  Diagnostics.Log("application-lifecycle",$"application-exiting; source={ShutdownOrigin}; exit-code={e.ApplicationExitCode}");instance?.Dispose();base.OnExit(e);
 }
 protected override void OnStartup(StartupEventArgs e)
 {
  if(CleanupCommandLine.TryRun(e.Args,out var cleanupExitCode)){RecordShutdownOrigin("cleanup-command");Shutdown(cleanupExitCode);return;}
  base.OnStartup(e);
  Diagnostics.Initialize();
  instance=new ApplicationInstance();
  if(!instance.IsPrimary){MessageBox.Show("Hexor Control Center is already running. Use the existing window.","Hexor");Shutdown();return;}
  Diagnostics.Log("startup",$"application-version={System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown"}; registry-schema-version={LegionCommanderRegistrySchema.Version}; updater-monitor-revision=1.0.11-structural-confirmation-3; hexor-pid={Environment.ProcessId}");
  AppDomain.CurrentDomain.UnhandledException += (_,a)=>Diagnostics.Log("unhandled",a.ExceptionObject.GetType().Name);
  TaskScheduler.UnobservedTaskException += (_,a)=>{Diagnostics.Log("unobserved-task",a.Exception.GetType().Name);a.SetObserved();};
  using(var gate=new Mutex(false,Hexor.Updates.UpdateCore.GateName(AppContext.BaseDirectory)))
  {
   bool acquired;try{acquired=gate.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
   if(!acquired){MessageBox.Show("Hexor is being updated. Wait for the update to finish, then open Hexor again.","Hexor update");RequestShutdown("update-gate");return;}gate.ReleaseMutex();
  }
  if(System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory,".hexor-update","pending")))
  {
   try{
    var recovery=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HexorRecovery-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(recovery);
    var helper=System.IO.Path.Combine(recovery,"Hexor.Updater.exe");
    // Use the previous helper if replacement was interrupted.
    var backup=System.IO.Path.Combine(AppContext.BaseDirectory,".hexor-update","Hexor.Updater.exe");
    System.IO.File.Copy(backup,helper);
    var start=new System.Diagnostics.ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true};foreach(var arg in new[]{"--recover",AppContext.BaseDirectory,Environment.ProcessId.ToString()})start.ArgumentList.Add(arg);System.Diagnostics.Process.Start(start);
   }catch(Exception ex){MessageBox.Show("Hexor recovery could not start: "+ex.Message+". Retain the .hexor-update folder and contact support.","Hexor recovery");}
   RequestShutdown("startup-recovery");return;
  }
  DispatcherUnhandledException += (_, a) => { Diagnostics.Log("ui", a.Exception.GetType().Name); MessageBox.Show("The operation could not be completed. Please check file permissions and try again.", "Hexor"); a.Handled=true; };
  new MainWindow().Show();
 }
}
