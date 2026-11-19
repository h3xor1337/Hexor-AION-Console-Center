using Hexor.Updates;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
if(args.Contains("--live-github"))
{
 var probe=Path.Combine(Path.GetTempPath(),"HexorGitHubProbe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(probe);
 try
 {
  var raw=Path.Combine(probe,"raw-readme.txt");await UpdateCore.Download("https://raw.githubusercontent.com/dotnet/runtime/main/README.md",raw,131072);
  if(!File.ReadAllText(raw).Contains(".NET Runtime"))throw new Exception("Unexpected public raw GitHub response.");Console.WriteLine("PASS live anonymous raw.githubusercontent.com retrieval (public transport fixture, not a Hexor release manifest)");
  var hashes=Path.Combine(probe,"hashes.sha256");await UpdateCore.Download("https://github.com/PowerShell/PowerShell/releases/download/v7.5.4/hashes.sha256",hashes,100000);
  using var file=File.OpenRead(hashes);if(Convert.ToHexString(SHA256.HashData(file))!="C1013E31E30CCB8C68366FE6A6E4CEA50F4DDD12FF115078019E4D1D71D58F00")throw new Exception("GitHub release fixture checksum mismatch.");Console.WriteLine("PASS live anonymous GitHub Releases redirect download and independently pinned official SHA-256");return 0;
 }finally{Directory.Delete(probe,true);}
}
int count=0;
void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);count++;}
void Reject(Action action,string label){try{action();}catch{Check(true,label);return;}throw new Exception("Accepted: "+label);}
using var rsa=RSA.Create(3072);using var stranger=RSA.Create(3072);
var root=Path.Combine(Path.GetTempPath(),"HexorUpdateTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try{
 var package=Path.Combine(root,"update.zip");using(var zip=ZipFile.Open(package,ZipArchiveMode.Create))foreach(var name in UpdateCore.OwnedFiles){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write("MZnew release");}
 var m=new Manifest(1,"1.2.3",DateTimeOffset.UtcNow.ToString("O"),"https://github.com/hexor-test-fixture/hexor-updates/releases/download/v1.2.3/update.zip",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))),"Release notes","1.0.0",false);
 string Sign(Manifest manifest){var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest);return JsonSerializer.Serialize(new Envelope(Convert.ToBase64String(bytes),Convert.ToBase64String(rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))));}
 Check(UpdateCore.Verify(Sign(m),rsa.ExportSubjectPublicKeyInfoPem())==m,"valid signed manifest");
 Reject(()=>UpdateCore.Verify(Sign(m),stranger.ExportSubjectPublicKeyInfoPem()),"untrusted signature");
 var env=JsonSerializer.Deserialize<Envelope>(Sign(m))!;Reject(()=>UpdateCore.Verify(JsonSerializer.Serialize(env with{Payload=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(m with{Mandatory=true}))}),rsa.ExportSubjectPublicKeyInfoPem()),"tampered metadata");
 Reject(()=>UpdateCore.Verify(Sign(m with{DownloadUrl="http://example.com"}),rsa.ExportSubjectPublicKeyInfoPem()),"HTTP rejected");
 Reject(()=>UpdateCore.Verify(Sign(m with{MinimumUpdaterVersion="9.0.0"}),rsa.ExportSubjectPublicKeyInfoPem()),"incompatible helper");
 Check(UpdateCore.ParseVersion("1.10.0")>UpdateCore.ParseVersion("1.9.9"),"semantic numeric version comparison");Reject(()=>UpdateCore.ParseVersion("1.01.0"),"malformed version");
 UpdateCore.VerifyPackage(package,m);Check(true,"valid checksum");Reject(()=>UpdateCore.VerifyPackage(package,m with{Sha256=new string('0',64)}),"invalid checksum");Reject(()=>UpdateCore.VerifyPackage(package+"missing",m),"missing package");
 // Companion (rebranded Aion2.Updater.exe) delivery: manifest fields, staging and installation.
 var companionZip=Path.Combine(root,"companion.zip");using(var cz=ZipFile.Open(companionZip,ZipArchiveMode.Create)){using var writer=new StreamWriter(cz.CreateEntry(UpdateCore.Aion2UpdaterEntry).Open());writer.Write("MZrebranded updater");}
static void WriteCompanion(string zipPath,string content){using var zip=ZipFile.Open(zipPath,ZipArchiveMode.Update);var entry=zip.GetEntry(UpdateCore.Aion2UpdaterEntry)!;entry.Delete();using var writer=new StreamWriter(zip.CreateEntry(UpdateCore.Aion2UpdaterEntry).Open());writer.Write(content);}
 var companionHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(companionZip)));
 var cm=m with{Aion2UpdaterUrl="https://github.com/hexor-test-fixture/hexor-updates/releases/download/v1.2.3/companion.zip",Aion2UpdaterSha256=companionHash};
 Check(UpdateCore.Verify(Sign(cm),rsa.ExportSubjectPublicKeyInfoPem())==cm,"manifest with companion updater fields verifies");
 Reject(()=>UpdateCore.Verify(Sign(cm with{Aion2UpdaterUrl="http://example.com"}),rsa.ExportSubjectPublicKeyInfoPem()),"companion HTTP URL rejected");
 Reject(()=>UpdateCore.Verify(Sign(cm with{Aion2UpdaterSha256="nothex"}),rsa.ExportSubjectPublicKeyInfoPem()),"companion malformed checksum rejected");
 Reject(()=>UpdateCore.Verify(Sign(m with{Aion2UpdaterUrl=cm.Aion2UpdaterUrl}),rsa.ExportSubjectPublicKeyInfoPem()),"companion URL without checksum rejected");
 Check(!Aion2UpdaterSync.IsAdvertised(m)&&Aion2UpdaterSync.IsAdvertised(cm),"companion advertisement detection");
 var cstage=Path.Combine(root,"companion-stage");UpdateCore.StageAion2Updater(companionZip,cstage);Check(true,"companion package staged");
 Reject(()=>{var extra=Path.Combine(root,"extra.zip");using(var ez=ZipFile.Open(extra,ZipArchiveMode.Create)){ez.CreateEntry(UpdateCore.Aion2UpdaterEntry);ez.CreateEntry("Hexor.Updater.exe");}UpdateCore.StageAion2Updater(extra,Path.Combine(root,"extra-stage"));},"companion package with extra entries rejected");
 Reject(()=>{var wrong=Path.Combine(root,"wrong.zip");using(var wz=ZipFile.Open(wrong,ZipArchiveMode.Create)){wz.CreateEntry("Other.exe");}UpdateCore.StageAion2Updater(wrong,Path.Combine(root,"wrong-stage"));},"companion package with unexpected entry name rejected");
 var vendorRoot=Path.Combine(root,"vendor","app");var vendorTarget=Path.Combine(root,"vendor","AION Auto Updater",UpdateCore.Aion2UpdaterEntry);
 var target=UpdateCore.Aion2UpdaterTarget(vendorRoot);Check(target==vendorTarget,"companion target sits beside the console install, in the vendor updater folder");
 UpdateCore.InstallAion2Updater(target,Path.Combine(cstage,UpdateCore.Aion2UpdaterEntry));Check(File.ReadAllText(target)=="MZrebranded updater","companion updater installed on fresh machine");
 WriteCompanion(companionZip,"MZrebranded updater v2");
 UpdateCore.StageAion2Updater(companionZip,cstage);UpdateCore.InstallAion2Updater(target,Path.Combine(cstage,UpdateCore.Aion2UpdaterEntry));Check(File.ReadAllText(target)=="MZrebranded updater v2","companion updater replaced atomically");
 var bad=Path.Combine(root,"bad.zip");using(var z=ZipFile.Open(bad,ZipArchiveMode.Create)){z.CreateEntry("../victim.exe");z.CreateEntry("Hexor.Updater.exe");}Reject(()=>UpdateCore.Stage(bad,Path.Combine(root,"badstage")),"archive traversal");
 var install=Path.Combine(root,"installed");Directory.CreateDirectory(install);foreach(var name in UpdateCore.OwnedFiles)File.WriteAllText(Path.Combine(install,name),"MZold release");File.WriteAllText(Path.Combine(install,"customer.json"),"customer settings");File.WriteAllText(Path.Combine(install,"Aion2.Updater.exe"),"unrelated");
 var stage=Path.Combine(root,"stage");UpdateCore.Stage(package,stage);
 Reject(()=>UpdateCore.Install(install,stage,i=>{if(i==0)throw new IOException("simulated failure");}),"installation failure");Check(UpdateCore.OwnedFiles.All(n=>File.ReadAllText(Path.Combine(install,n))=="MZold release"),"rollback restores both binaries");
 using(var locked=new FileStream(Path.Combine(install,UpdateCore.OwnedFiles[1]),FileMode.Open,FileAccess.Read,FileShare.Read)){Reject(()=>UpdateCore.Install(install,stage),"locked executable");}
 Check(File.ReadAllText(Path.Combine(install,UpdateCore.OwnedFiles[0]))=="MZold release","locked update leaves old application");
 UpdateCore.Install(install,stage);Check(UpdateCore.OwnedFiles.All(n=>File.ReadAllText(Path.Combine(install,n))=="MZnew release"),"successful installation including helper self replacement");
 Check(File.ReadAllText(Path.Combine(install,"customer.json"))=="customer settings"&&File.ReadAllText(Path.Combine(install,"Aion2.Updater.exe"))=="unrelated","customer and third party files preserved");
 File.WriteAllText(Path.Combine(install,".hexor-update","pending"),"interrupted");UpdateCore.Recover(install);Check(UpdateCore.OwnedFiles.All(n=>File.ReadAllText(Path.Combine(install,n))=="MZold release"),"journal recovery after interrupted installation");
 using var cancel=new CancellationTokenSource();cancel.Cancel();try{await UpdateCore.Download("https://raw.githubusercontent.com/hexor-test-fixture/hexor-updates/main/latest.json",Path.Combine(root,"partial"),100,token:cancel.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){Check(!File.Exists(Path.Combine(root,"partial")),"cancelled download leaves no partial package");}
 await DownloadTests.Run(root,Check);
 Console.WriteLine($"{count} updater checks passed");return 0;
}finally{Directory.Delete(root,true);}
