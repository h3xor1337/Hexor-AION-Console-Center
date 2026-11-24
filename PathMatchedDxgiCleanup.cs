using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Hexor;

// User-authorized policy: only the exact dll beside a validated Aion2.exe is eligible.
public sealed class PathMatchedDxgiCleanupService
{
    private readonly Func<string,bool> running;
    public PathMatchedDxgiCleanupService(Func<string,bool>? running=null)=>this.running=running??IsRunning;
    public CleanupPlanItem Inspect(AionInstallation installation)
    {
        var exe=Path.GetFullPath(installation.ExecutablePath); var win64=Path.GetDirectoryName(exe)!; var target=Path.Combine(win64,"dxgi.dll");
        if(!IsValid(exe,target)) return new(CleanupItemKind.PathMatchedDxgi,target,VendorOverlayCleanupState.CouldNotVerify,"Unsafe path or invalid AION2 installation; dxgi.dll was retained.");
        if(!File.Exists(target))
        {
            var backup=target+VendorOverlayCleanupService.UnloadedSuffix;
            if(File.Exists(backup)) return new(CleanupItemKind.PathMatchedDxgi,backup,VendorOverlayCleanupState.Skipped,"Unloaded module backup; permanent removal deletes it as well.",exe,ExpectedFingerprint:Hash(backup));
            return new(CleanupItemKind.PathMatchedDxgi,target,VendorOverlayCleanupState.Skipped,"No path-matched dxgi.dll exists.",exe);
        }
        return new(CleanupItemKind.PathMatchedDxgi,target,VendorOverlayCleanupState.Skipped,"Path-matched dxgi.dll; explicit confirmation is required before deletion.",exe,ExpectedFingerprint:Hash(target));
    }
    public CleanupPlanItem Execute(CleanupPlanItem item)
    {
        if(item.State!=VendorOverlayCleanupState.Skipped||item.GameExecutablePath==null||string.IsNullOrWhiteSpace(item.ExpectedFingerprint))return item;
        if(new[]{"Aion2","Aion2.Updater","Aion2Updater.Launcher","LegionCommander"}.Any(running))return item with{State=VendorOverlayCleanupState.BlockedByRunningProcess,Message="Locked or active: close AION2 and the updater before cleanup."};
        try { var baseTarget=item.TargetPath.EndsWith(VendorOverlayCleanupService.UnloadedSuffix,StringComparison.OrdinalIgnoreCase)?Path.Combine(Path.GetDirectoryName(item.TargetPath)!,"dxgi.dll"):item.TargetPath;if(!IsValid(item.GameExecutablePath,baseTarget)||!File.Exists(item.TargetPath)||(File.GetAttributes(item.TargetPath)&FileAttributes.ReparsePoint)!=0)return item with{State=VendorOverlayCleanupState.CouldNotVerify,Message="Unsafe path; dxgi.dll was retained."};if(!Hash(item.TargetPath).Equals(item.ExpectedFingerprint,StringComparison.OrdinalIgnoreCase))return item with{State=VendorOverlayCleanupState.CouldNotVerify,Message="dxgi.dll changed since preview; it was retained."};File.Delete(item.TargetPath);if(File.Exists(item.TargetPath))return item with{State=VendorOverlayCleanupState.Failed,Message="Path-matched dxgi.dll could not be removed."};
        // Permanent removal also erases our own unloaded-module backup beside the target.
        var sibling=item.TargetPath+VendorOverlayCleanupService.UnloadedSuffix;
        if(sibling!=item.TargetPath&&File.Exists(sibling)&&Path.GetFileName(Path.GetDirectoryName(item.TargetPath)).Equals("Win64",StringComparison.OrdinalIgnoreCase))File.Delete(sibling);
        return item with{State=VendorOverlayCleanupState.Removed,Message="Path-matched dxgi.dll was removed."}; }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return item with{State=VendorOverlayCleanupState.Failed,Message="Path-matched dxgi.dll removal failed: "+e.GetType().Name+"."};}
    }
    private static bool IsValid(string exe,string target){try{if(!File.Exists(exe)||!string.Equals(Path.GetFileName(exe),"Aion2.exe",StringComparison.OrdinalIgnoreCase))return false;var d=Path.GetDirectoryName(exe)!;var expected=Path.Combine(d,"dxgi.dll");return Path.GetFullPath(target).Equals(expected,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(d).Equals("Win64",StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(Path.GetDirectoryName(d)!).Equals("Binaries",StringComparison.OrdinalIgnoreCase)&&(File.GetAttributes(d)&FileAttributes.ReparsePoint)==0;}catch{return false;}}
    private static string Hash(string p){using var s=File.OpenRead(p);return Convert.ToHexString(SHA256.HashData(s));}
    private static bool IsRunning(string n){var p=Process.GetProcessesByName(n);try{return p.Length>0;}finally{foreach(var x in p)x.Dispose();}}
}
