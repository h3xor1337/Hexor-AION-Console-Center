using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
namespace Hexor;
// Explicit updater-owned roots only. The installed updater under Programs\Hexor is never included.
public sealed class UpdaterDataCleanupService
{
 public const string RegistryPath="Software\\Aion2Updater";
 private readonly string registryPath;
 private readonly Func<string,bool> running;
 public UpdaterDataCleanupService(string? registryPath=null,Func<string,bool>? running=null){this.registryPath=registryPath??RegistryPath;this.running=running??Running;}
 public string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Aion2Updater");
 public IReadOnlyList<CleanupPlanItem> Inspect(string? root=null){root??=Root;var items=new List<CleanupPlanItem>();if(Directory.Exists(root)){var fullRoot=Path.GetFullPath(root);items.Add(new(CleanupItemKind.UpdaterData,fullRoot,VendorOverlayCleanupState.Skipped,"Verified updater-owned settings, language, authorization, cache, and temporary data.",ScopeRoot:fullRoot));}if(Registry.CurrentUser.OpenSubKey(registryPath)!=null)items.Add(new(CleanupItemKind.UpdaterRegistry,"HKCU\\"+registryPath,VendorOverlayCleanupState.Skipped,"Verified updater-owned registry settings, language, license, and authorization data."));return items;}
 public CleanupPlanItem Execute(CleanupPlanItem item){if(new[]{"Aion2","Aion2.Updater","Aion2Updater.Launcher","LegionCommander"}.Any(running))return item with{State=VendorOverlayCleanupState.BlockedByRunningProcess,Message="Close AION2 and the updater before cleanup."};try{if(item.Kind==CleanupItemKind.UpdaterRegistry){Registry.CurrentUser.DeleteSubKeyTree(registryPath,false);return Registry.CurrentUser.OpenSubKey(registryPath)==null?item with{State=VendorOverlayCleanupState.Removed,Message="Updater registry settings, language, license, and authorization data removed."}:item with{State=VendorOverlayCleanupState.Failed,Message="Updater registry data remained."};}if(item.ScopeRoot==null||!SamePath(item.ScopeRoot,item.TargetPath)||!Directory.Exists(item.TargetPath))return item with{State=VendorOverlayCleanupState.Skipped,Message="Already absent or unsafe updater-data path."};if(HasReparsePoint(item.TargetPath))return item with{State=VendorOverlayCleanupState.CouldNotVerify,Message="Unsafe updater-data reparse point retained."};Directory.Delete(item.TargetPath,true);return Directory.Exists(item.TargetPath)?item with{State=VendorOverlayCleanupState.Failed,Message="Updater data directory remained."}:item with{State=VendorOverlayCleanupState.Removed,Message="Updater-owned data removed."};}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){return item with{State=VendorOverlayCleanupState.Failed,Message="Updater data cleanup failed: "+e.GetType().Name};}}
 static bool HasReparsePoint(string root){var q=new Stack<string>();q.Push(root);while(q.Count>0){var path=q.Pop();if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return true;foreach(var child in Directory.EnumerateFileSystemEntries(path)){if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)return true;if(Directory.Exists(child))q.Push(child);}}return false;}
 static bool Inside(string root,string p){var r=Path.GetRelativePath(root,Path.GetFullPath(p));return!Path.IsPathRooted(r)&&!r.StartsWith(".."+Path.DirectorySeparatorChar)&&r!="..";}
 static bool SamePath(string left,string right)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),StringComparison.OrdinalIgnoreCase);
 static bool Running(string n){var p=Process.GetProcessesByName(n);try{return p.Length>0;}finally{foreach(var x in p)x.Dispose();}}
}
