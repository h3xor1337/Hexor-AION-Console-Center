using System.IO;
using System.Security.Cryptography;

namespace Hexor.Updates;

// Keeps the rebranded vendor updater next to the console in step with each signed release.
// The main OTA package stays the two Hexor executables so already-installed versions keep
// updating; the companion asset is referenced by optional signed manifest fields and fetched
// separately. Failures never block the update check; the next check retries automatically.
public static class Aion2UpdaterSync
{
 // Wired by the console to the application log; test projects run without a logger.
 internal static Action<string>? LogSink;
 static void Log(string message)=>LogSink?.Invoke(message);
 public static bool IsAdvertised(Manifest release)=>
  !string.IsNullOrWhiteSpace(release.Aion2UpdaterUrl)&&!string.IsNullOrWhiteSpace(release.Aion2UpdaterSha256);

 public static async Task ReconcileAsync(Manifest release,string installDirectory,CancellationToken token)
 {
  if(!IsAdvertised(release))return;
  var expected=release.Aion2UpdaterSha256!.Trim();
  var target=UpdateCore.Aion2UpdaterTarget(installDirectory);
  var stamp=Path.Combine(Path.GetDirectoryName(target)!,UpdateCore.Aion2UpdaterStamp);
  try
  {
   if(File.Exists(target)&&File.Exists(stamp)&&File.ReadAllText(stamp).Trim().Equals(expected,StringComparison.OrdinalIgnoreCase))
   {Log("companion-updater-current");return;}
   var root=Path.Combine(Path.GetTempPath(),"hexor-companion-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   try
   {
    var package=Path.Combine(root,"companion.zip");
    await UpdateCore.Download(release.Aion2UpdaterUrl!,package,600_000_000,token:token);
    using(var stream=File.OpenRead(package))if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected,StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("Companion updater checksum is invalid.");
    var stage=Path.Combine(root,"stage");UpdateCore.StageAion2Updater(package,stage);
    UpdateCore.InstallAion2Updater(target,Path.Combine(stage,UpdateCore.Aion2UpdaterEntry));
    await File.WriteAllTextAsync(stamp,expected,token);
    // Keep ownership verification in step with the signed companion: the recorded updater
    // evidence is refreshed so the menu-status card never reports an uncertain updater
    // after a routine signed release.
    VendorOverlayCleanupService.RefreshUpdaterEvidence(Path.Combine(Path.GetDirectoryName(target)!,VendorOverlayCleanupService.ManifestName),target);
    Log("companion-updater-installed");
   }
   finally
   {
    try{Directory.Delete(root,true);}catch(IOException){}
   }
  }
  catch(OperationCanceledException){throw;}
  catch(Exception error)
  {
   // Busy vendor updater or a transient failure: leave the existing file untouched and retry on the next check.
   Log("companion-updater-retry-pending; type="+error.GetType().Name);
  }
 }
}
