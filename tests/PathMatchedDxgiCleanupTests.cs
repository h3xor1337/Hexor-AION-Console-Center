using System.IO;
using Hexor;
internal static class PathMatchedDxgiCleanupTests
{
 public static void Run(string root,Action<bool,string> assert)
 {
  var driveA=Path.Combine(root,"drive-a");var driveB=Path.Combine(root,"drive-b");var exeA=Make(driveA,"Steam","one");var exeB=Make(driveB,"Purple","two");var discovery=new AionInstallationDiscoveryService();var found=discovery.DiscoverOnFixedDrives([driveA,driveB,Path.Combine(root,"missing")]);assert(found.Count==2,"Fixed-drive scan finds multiple validated AION2 installations and ignores inaccessible roots");
  var service=new PathMatchedDxgiCleanupService(_=>false);var plan=found.Select(service.Inspect).ToArray();assert(plan.All(x=>x.Message.StartsWith("Path-matched")&&x.ExpectedFingerprint!=null),"Only exact game-relative dxgi.dll targets are path-matched");var targetA=plan.Single(x=>x.GameExecutablePath==exeA);File.WriteAllText(targetA.TargetPath,"changed");assert(service.Execute(targetA).State==VendorOverlayCleanupState.CouldNotVerify&&File.Exists(targetA.TargetPath),"Changed dxgi.dll is retained after preview");var targetB=plan.Single(x=>x.GameExecutablePath==exeB);assert(service.Execute(targetB).State==VendorOverlayCleanupState.Removed&&!File.Exists(targetB.TargetPath),"Confirmed exact path-matched dxgi.dll is removed");assert(service.Execute(targetB).State!=VendorOverlayCleanupState.Removed,"Repeated path cleanup is idempotent");
 }
 private static string Make(string root,string name,string contents){var w=Path.Combine(root,name,"AION2","Aion2","Binaries","Win64");Directory.CreateDirectory(w);var e=Path.Combine(w,"Aion2.exe");File.WriteAllText(e,"game");File.WriteAllText(Path.Combine(w,"dxgi.dll"),contents);return e;}
}
