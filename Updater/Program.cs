using System.Diagnostics;
using System.IO;
using Hexor.Updates;

var logRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HexorAion","updates");Directory.CreateDirectory(logRoot);
var log=Path.Combine(logRoot,"updater.log");
try
{
 if(args.Length==3&&args[0]=="--recover")
 {
  using var recoveryGate=new Mutex(false,UpdateCore.GateName(args[1]));
  if(!recoveryGate.WaitOne(0))throw new IOException("Another Hexor update is running.");
  try{
  try{using var parent=Process.GetProcessById(int.Parse(args[2]));if(!parent.WaitForExit(120000))throw new IOException("Close Hexor to recover.");}catch(ArgumentException){}
  UpdateCore.Recover(args[1]);File.AppendAllText(log,$"{DateTimeOffset.Now:O} Recovered interrupted installation.\n");
  }finally{recoveryGate.ReleaseMutex();}
  Process.Start(new ProcessStartInfo(Path.Combine(args[1],UpdateCore.OwnedFiles[0])){UseShellExecute=true,WorkingDirectory=args[1]});return 0;
 }
 if(args.Length!=5)throw new ArgumentException("Expected install directory, manifest, package, parent PID, restart flag.");
 var install=Path.GetFullPath(args[0]);UpdateCore.NoLinks(install);
 // The helper is copied out of the installation before launch so it can update itself.
 var manifest=UpdateCore.Verify(File.ReadAllText(args[1]),Trust.Load().PublicKeyPem);UpdateCore.VerifyPackage(args[2],manifest);
 using var gate=new Mutex(false,UpdateCore.GateName(install));
 if(!gate.WaitOne(0))throw new IOException("Another Hexor update is running.");
 try
 {
  try{using var parent=Process.GetProcessById(int.Parse(args[3]));if(!parent.WaitForExit(120000))throw new IOException("Close Hexor before updating.");}catch(ArgumentException){}
  foreach(var process in Process.GetProcessesByName("Hexor-AION-Control-Center")){using(process){try{if(string.Equals(process.MainModule?.FileName,Path.Combine(install,UpdateCore.OwnedFiles[0]),StringComparison.OrdinalIgnoreCase))throw new IOException("Another Hexor window is open. Close it and retry.");}catch(System.ComponentModel.Win32Exception){throw new IOException("Cannot verify running Hexor processes.");}}}
  UpdateCore.Recover(install);
  var current=FileVersionInfo.GetVersionInfo(Path.Combine(install,UpdateCore.OwnedFiles[0]));
  if(UpdateCore.ParseVersion(manifest.Version)<=new Version(current.FileMajorPart,current.FileMinorPart,current.FileBuildPart))throw new IOException("Release is not newer than the installed version.");
  var stage=Path.Combine(Path.GetDirectoryName(args[2])!,"stage-"+Guid.NewGuid().ToString("N"));
  UpdateCore.Stage(args[2],stage);
  var stagedVersion=FileVersionInfo.GetVersionInfo(Path.Combine(stage,UpdateCore.OwnedFiles[0]));
  if(new Version(stagedVersion.FileMajorPart,stagedVersion.FileMinorPart,stagedVersion.FileBuildPart)!=UpdateCore.ParseVersion(manifest.Version))throw new IOException("Package version does not match signed release metadata.");
  UpdateCore.Install(install,stage);
  File.AppendAllText(log,$"{DateTimeOffset.Now:O} Installed {manifest.Version}; previous binaries retained in .hexor-update.\n");
 }finally{gate.ReleaseMutex();}
 if(args[4]=="restart")Process.Start(new ProcessStartInfo(Path.Combine(install,UpdateCore.OwnedFiles[0])){UseShellExecute=true,WorkingDirectory=install});
 if(args[4]=="exit"&&Environment.GetEnvironmentVariable("HEXOR_UPDATER_NO_DIALOG")!="1")UpdateCompletionWindow.ShowAfterUpdate();
 return 0;
}
catch(Exception ex){File.AppendAllText(log,$"{DateTimeOffset.Now:O} ERROR {ex}\n");var message="Hexor update failed: "+ex.Message+"\n\nYour recovery backups have been retained. Close other Hexor windows and reopen Hexor to retry recovery if needed.\n\nLog: "+log;Console.Error.WriteLine(message);if(Environment.GetEnvironmentVariable("HEXOR_UPDATER_NO_DIALOG")!="1")NativeError.MessageBox(IntPtr.Zero,message,"Hexor update",0x10);return 1;}
static class NativeError
{
 [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,EntryPoint="MessageBoxW")]
 internal static extern int MessageBox(IntPtr window,string text,string caption,uint type);
}
