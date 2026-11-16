using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Hexor.Updates;
public sealed class UpdatePanel : StackPanel
{
 readonly TextBlock current=new(),latest=new(){Text="Latest Version: Checking…"},status=new(){Text="Checking update",TextWrapping=TextWrapping.Wrap};
 readonly ProgressBar progress=new(){Height=8,Margin=new Thickness(0,8,0,8),IsIndeterminate=true};
 readonly Button check=new(){Content="Check for Updates"};
 readonly CancellationTokenSource cancellation=new();Manifest? release;string? envelope;bool started,checking,installing;
 static (string Latest,string Status)? lastResult;
 static string Installed=>Assembly.GetExecutingAssembly().GetName().Version is {} v?$"{v.Major}.{v.Minor}.{v.Build}":"1.0.0";
 public UpdatePanel(bool automaticallyCheck = true)
 {
  current.Text="Current Version: "+Installed;Children.Add(new TextBlock{Text="Updates",FontSize=18,FontWeight=FontWeights.SemiBold});
  foreach(var element in new UIElement[]{current,latest,status,progress,check})Children.Add(element);
  check.Click+=async(_,_)=>await RequestCheck(false);
  if(automaticallyCheck) Loaded+=async(_,_)=>{if(!started){started=true;await RequestCheck(true);}};
 }
 async Task RequestCheck(bool startup)
 {
  check.IsEnabled=false;
  try { await UpdateSession.Current.Run(startup,()=>Check(showAvailable:true)); }
  finally
  {
   // Rebuilt panels must display the shared outcome, including failures from a detached panel.
   if(lastResult is {} result){latest.Text=result.Latest;status.Text=result.Status;}
   progress.IsIndeterminate=false;check.IsEnabled=true;
  }
 }
 async Task Check(bool showAvailable)
 {
  if(checking||installing)return;checking=true;check.IsEnabled=false;release=null;progress.IsIndeterminate=true;status.Text="Checking update";
  var stage=OtaCheckStage.TrustConfiguration;var installCallbackInvoked=false;
  try
  {
   var trust=Trust.Load();if(string.IsNullOrWhiteSpace(trust.ManifestUrl)||string.IsNullOrWhiteSpace(trust.PublicKeyPem))throw new InvalidOperationException("Update hosting and release signing have not been configured for this build.");
   Hexor.Diagnostics.Log("ota","check-started");var path=Path.Combine(Path.GetTempPath(),"hexor-manifest-"+Guid.NewGuid()+".json");
   try{stage=OtaCheckStage.ManifestFetch;await UpdateCore.Download(trust.ManifestUrl,path,131072,token:cancellation.Token);stage=OtaCheckStage.ManifestRead;envelope=await File.ReadAllTextAsync(path,cancellation.Token);stage=OtaCheckStage.ManifestValidation;release=UpdateCore.Verify(envelope,trust.PublicKeyPem);Hexor.Diagnostics.Log("ota","signature-valid; version="+release.Version);}finally{try{File.Delete(path);}catch{stage=OtaCheckStage.ManifestCleanup;throw;}}
   stage=OtaCheckStage.Presentation;
   latest.Text="Latest Version: "+release.Version;var newer=UpdateCore.ParseVersion(release.Version)>UpdateCore.ParseVersion(Installed);status.Text=newer?"Update available":"Up to date";
   lastResult=(latest.Text,status.Text);
   if(newer&&showAvailable){var dialog=new UpdateAvailableDialog(Installed,release,d=>{installCallbackInvoked=true;return Install(d);}){Owner=Window.GetWindow(this)};dialog.ShowDialog();}
  }
  catch(Exception ex){Hexor.Diagnostics.Log("ota",OtaCheckDiagnostics.Format(stage,ex,installCallbackInvoked),DiagnosticSeverity.Error);status.Text="Unable to check for updates. Hexor remains available; try again later.";latest.Text="Latest Version: Not available";lastResult=(latest.Text,status.Text);}
  finally{progress.IsIndeterminate=false;check.IsEnabled=true;checking=false;}
 }
 async Task Install(UpdateAvailableDialog dialog)
 {
  if(installing||release==null||envelope==null)return;installing=true;check.IsEnabled=false;
  try
  {
   var trust=Trust.Load();release=UpdateCore.Verify(envelope,trust.PublicKeyPem);
   var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HexorAion","updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   var package=Path.Combine(root,"package.zip");dialog.SetStage("Downloading","Downloading the signed Hexor update…");
   await UpdateCore.Download(release.DownloadUrl,package,600_000_000,new Progress<DownloadProgress>(p=>dialog.SetStage("Downloading","Downloading the signed Hexor update…",p)),cancellation.Token);
   dialog.SetStage("Verifying signature","Checking the signed manifest and package SHA-256…");await Task.Run(()=>{UpdateCore.Verify(envelope,trust.PublicKeyPem);UpdateCore.VerifyPackage(package,release);},cancellation.Token);Hexor.Diagnostics.Log("ota","package-valid; version="+release.Version);
   dialog.SetStage("Preparing installation","Validating the external updater and preparing a safe transaction…");
   var manifest=Path.Combine(root,"manifest.json");await File.WriteAllTextAsync(manifest,envelope,cancellation.Token);var helper=Path.Combine(root,"Hexor.Updater.exe");File.Copy(Path.Combine(AppContext.BaseDirectory,"Hexor.Updater.exe"),helper);
   dialog.SetStage("Installing","Hexor will close now. The updater will safely replace the verified files.");
   var start=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root};foreach(var arg in new[]{AppContext.BaseDirectory,manifest,package,Environment.ProcessId.ToString(),"exit"})start.ArgumentList.Add(arg);
   _=Process.Start(start)??throw new IOException("Could not start Hexor updater.");Hexor.Diagnostics.Log("ota","installer-started; restart=false");dialog.CompleteAndClose();Hexor.App.RequestShutdown("ota-install");
  }
  catch(Exception ex){Hexor.Diagnostics.Log("ota","install-failed; type="+ex.GetType().Name);dialog.ShowFailure("Update could not be completed",SafeMessage(ex));}
  finally{installing=false;check.IsEnabled=true;}
 }
 static string SafeMessage(Exception ex)=>ex switch{HttpRequestException=>"The update could not be downloaded.",UnauthorizedAccessException=>"Hexor does not have permission to prepare the update.",IOException=>"The update files could not be prepared. Check free disk space and file permissions.",_=>"The signed update could not be verified or prepared."};
}
