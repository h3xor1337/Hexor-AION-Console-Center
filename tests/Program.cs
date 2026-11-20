using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hexor;
using Microsoft.Win32;
using System.Diagnostics;
internal static class Program
{
 private static int passed;
 private static void Assert(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);passed++;}
 [STAThread]private static int Main(string[] args)
 {
  Hexor.Runtime.CharacterSettingsRepairService.LiveSweepEnabled=false; // tests sweep explicit temp directories only
  if(args.Length==2&&args[0]=="--instance-child")return UpdateSessionTests.InstanceChild(args[1]);
  if(args.Length==1&&args[0]=="--update-session-only")return RunFocused(UpdateSessionTests.Run);
  if(args.Length==1&&args[0]=="--daily-registry-only")return RunFocused(DailyQuestRegistryTests.Run);
  if(args.Length==1&&args[0]=="--captcha-only")return RunFocused(CaptchaMonitorTests.Run);
  if(args.Length==1&&args[0]=="--runtime-mcp-only")return RunFocused(RuntimeMcpClientTests.Run);
  if(args.Length==1&&args[0]=="--game-ipc-only")return RunFocused(GameIpcTests.Run);
  if(args.Length==1&&args[0]=="--captcha-solver-only")return RunFocused(CaptchaAutoSolverTests.Run);
  if(args.Length==1&&args[0]=="--game-ipc-live")return RunFocused(GameIpcLiveTests.Run);
  if(args.Length==1&&args[0]=="--launcher-only")return RunFocused((_,check)=>LauncherProvenanceTests.Run(check));
  if(args.Length==1&&args[0]=="--storage-audit-only")return RunFocused((root,check)=>
  {
   var branch="Software\\HexorTests\\"+Guid.NewGuid().ToString("N");
   try { StorageAuditTests.Run(root,branch,check); }
   finally { Registry.CurrentUser.DeleteSubKeyTree(branch,false); }
  });
  if(args.Length==1&&args[0]=="--compatibility-only")return RunFocused((root,check)=>
  {
   var branch="Software\\HexorTests\\"+Guid.NewGuid().ToString("N");
   try
   {
    using(var key=Registry.CurrentUser.CreateSubKey(branch+"\\AutoTask"))key.SetValue("CharacterJob",777,RegistryValueKind.DWord);
    using(var key=Registry.CurrentUser.CreateSubKey(branch+"\\MiniGame"))key.SetValue("AutoEnter","opaque",RegistryValueKind.String);
    CompatibilityTests.Run(root,new RegistrySettingsService(branch),check);
   }
   finally { Registry.CurrentUser.DeleteSubKeyTree(branch,false); }
  });
  if(args.Length==2&&args[0]=="--native-handoff-parent")return VendorUpdaterNativeHandoffTests.RunParent(args[1]);
  if(args.Length==2&&args[0]=="--native-handoff-child")return VendorUpdaterNativeHandoffTests.RunChild(args[1]);
  if(args.Length==4&&args[0]=="--diagnostic-child"&&int.TryParse(args[3],out var diagnosticCount)){Diagnostics.ConfigureRoot(args[1]);for(var i=0;i<diagnosticCount;i++)if(!Diagnostics.TryLog("cross-process-test",args[2]+i))return 2;return 0;}
  if(args.Length==3&&args[0]=="--diagnostic-crash-child"){Diagnostics.ConfigureRoot(args[1]);if(!Diagnostics.TryLog("crash-test",args[2]))return 2;Console.WriteLine("READY");Console.Out.Flush();Thread.Sleep(Timeout.Infinite);return 0;}
  if(args.Length==2&&args[0]=="--registry-sync-child")return new RegistryInitializationService(new RegistrySettingsService(args[1])).Ensure().Success?0:2;
  if(args.Contains("--inspect-local"))return LocalDetectionProbe.Run();
  if(args.Length==2&&args[0]=="--stress-retained-worker"&&int.TryParse(args[1],out var retainedIterations)){VendorUpdaterTests.StressRetainedWorkerRecovery(retainedIterations);Console.WriteLine($"PASS retained-worker stress: {retainedIterations}");return 0;}
  if(args.Length==2&&args[0]=="--stress-invisible-handoff"&&int.TryParse(args[1],out var handoffIterations)){VendorUpdaterHandoffTests.StressInvisibleSecondCandidate(handoffIterations);Console.WriteLine($"PASS invisible-handoff stress: {handoffIterations}");return 0;}
  if(args.Length==1&&args[0]=="--vendor-updater-only")return RunFocused(VendorUpdaterTests.Run);
  if(args.Length==1&&args[0]=="--vendor-updater-handoff-only")return RunFocused(VendorUpdaterHandoffTests.Run);
  if(args.Length==1&&args[0]=="--uia-diagnostics-only")return RunFocused(VendorUpdaterCancellationTests.RunProbeDiagnostics);
  if(args.Length==1&&args[0]=="--logging-outcomes-only")return RunFocused(LoggingOutcomeTests.Run);
  if(args.Length==1&&args[0]=="--logging-only")return RunFocused(DiagnosticsTests.Run);
  if(args.Length==1&&args[0]=="--registry-only")return RunFocused((root,check)=>RegistryInitializationTests.Run(root,"Software\\HexorTests\\"+Guid.NewGuid().ToString("N"),check));
  if(args.Length==1&&args[0]=="--updater-ui-only")return RunFocused(UpdateExperienceTests.Run);
  if(args.Length==1&&args[0]=="--updater-workflow-only")return RunFocused((root,check)=>
  {
   TestCheckpoint.Run(nameof(VendorUpdaterConsentTests),()=>VendorUpdaterConsentTests.Run(root,check),120);
  });
  if(args.Length==1&&args[0]=="--wpf-only")return RunFocused((root,_)=>RunWpfOnly(root));
  if(args.Length==2&&args[0]=="--stress-uia-diagnostics"&&int.TryParse(args[1],out var diagnosticsIterations)&&diagnosticsIterations>0)return RunFocused((root,check)=>{for(var iteration=1;iteration<=diagnosticsIterations;iteration++){VendorUpdaterCancellationTests.RunProbeDiagnostics(root,check);Console.WriteLine($"PASS UIA diagnostics iteration {iteration}/{diagnosticsIterations}");}});
  var root=Path.Combine(Path.GetTempPath(),"HexorTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Diagnostics.ConfigureRoot(Path.Combine(root,"logs"));var registryRoot="Software\\HexorTests\\"+Guid.NewGuid().ToString("N");
  try
  {
   Environment.SetEnvironmentVariable("HEXOR_TEST_ARTIFACTS",Path.Combine(root,"artifacts"));
   var json=new JsonConfigurationService(Path.Combine(root,"preferences.json"),new SettingsBackupService());var prefs=json.Load();Assert(prefs.UiLanguage=="English"&&prefs.GameLanguage=="English","English defaults persist on first launch");Assert(File.Exists(Path.Combine(root,"preferences.json")),"First launch writes preferences");
   prefs.Instructions.Add(new(){Type=InstructionType.Gathering});prefs.Instructions.Add(new(){Type=InstructionType.AutoKraoDungeon});json.Save(prefs);var loaded=json.Load();Assert(loaded.Instructions.Count==3&&loaded.Instructions[0].Type==loaded.Instructions[1].Type&&loaded.Instructions[2].Type==InstructionType.AutoKraoDungeon,"Sequence duplicates and order round trip");
   var fixture=Path.Combine(root,"fixture.json");File.WriteAllText(fixture,"{\"supported\":1,\"unknown\":{\"retain\":[1,2,3]}}");json.UpdatePropertyAsync(fixture,"supported",JsonValue.Create(2)).GetAwaiter().GetResult();var obj=JsonNode.Parse(File.ReadAllText(fixture))!;Assert(obj["unknown"]!["retain"]!.AsArray().Count==3&&obj["supported"]!.GetValue<int>()==2,"JSON patch preserves unknown properties");Assert(Directory.GetFiles(root,"*.bak").Length>=2,"Backups precede replacement");
   var corrupt=Path.Combine(root,"broken.json");File.WriteAllText(corrupt,"{invalid");Assert(json.ReadObjectAsync(corrupt).GetAwaiter().GetResult()==null,"Corrupted discovery JSON handled");var recovery=new JsonConfigurationService(corrupt,new SettingsBackupService());Assert(recovery.Load().UiLanguage=="English"&&File.ReadAllText(corrupt)=="{invalid","Invalid preferences retained before explicit save");recovery.Save(new());Assert(Directory.GetFiles(root,"broken.json.*.bak").Any(p=>File.ReadAllText(p)=="{invalid"),"Invalid preferences backed up on recovery save");
   var readOnly=Path.Combine(root,"readonly.json");File.WriteAllText(readOnly,"{\"supported\":1}");File.SetAttributes(readOnly,FileAttributes.ReadOnly);try{json.UpdatePropertyAsync(readOnly,"supported",JsonValue.Create(2)).GetAwaiter().GetResult();throw new Exception("Readonly file modified");}catch(UnauthorizedAccessException){Assert(File.ReadAllText(readOnly)=="{\"supported\":1}","Read-only source remains intact after failed write");}finally{File.SetAttributes(readOnly,FileAttributes.Normal);}var reg=new RegistrySettingsService(registryRoot);Assert(reg.Read("MiniGame","AutoEnter")==null,"Missing registry value handled");reg.WriteVerified("MiniGame","UseTaiwanVillageRules",1);reg.WriteVerified("MiniGame","AutoEnter",0);Assert(reg.Read("MiniGame","UseTaiwanVillageRules")==1&&reg.Read("MiniGame","AutoEnter")==0,"Festival values stay independent");reg.WriteVerified("MiniGame","AutoEnter",1);reg.WriteVerified("MiniGame","UseTaiwanVillageRules",0);Assert(reg.Read("MiniGame","UseTaiwanVillageRules")==0&&reg.Read("MiniGame","AutoEnter")==1,"Reverse festival edit stays independent");
   using(var key=Registry.CurrentUser.CreateSubKey(registryRoot+"\\AutoTask")){key.SetValue("CharacterJob",777);key.SetValue("Unrelated",42);}reg.WriteVerified("AutoTask","ClearSideQuestsBeforeDungeon",1);Assert(reg.Read("AutoTask","CharacterJob")==777,"Unknown class preserved");using(var key=Registry.CurrentUser.OpenSubKey(registryRoot+"\\AutoTask"))Assert((int?)key!.GetValue("Unrelated")==42,"Unrelated registry values preserved");try{reg.WriteVerified("AutoTask","CharacterJob",1);throw new Exception("Mapping write allowed");}catch(InvalidOperationException){Assert(true,"Unverified class mapping rejected");}
   using(var key=Registry.CurrentUser.CreateSubKey(registryRoot+"\\MiniGame"))key.SetValue("AutoEnter","opaque",RegistryValueKind.String);Assert(reg.Read("MiniGame","AutoEnter")==null,"Unsupported registry type retained on read");
   var denied=new RegistryToggle(new DeniedRegistry(),"MiniGame","AutoEnter","test",_=>{});denied.Value=true;Assert(denied.Value==null,"Registry failure rolls back UI value");
   using(var vm=new MainViewModel(json,reg)){vm.MaxAccounts="0";vm.SaveSchedule();Assert(vm.Status.StartsWith("Enter positive"),"Zero accounts rejected");vm.MaxAccounts="2";vm.WorkMin="120";vm.WorkMax="60";vm.SaveSchedule();Assert(vm.Status.StartsWith("Enter positive"),"Reversed work range rejected");vm.WorkMin="invalid";vm.SaveSchedule();Assert(vm.Status.StartsWith("Enter positive"),"Invalid numeric input does not crash");vm.WorkMin="60";vm.WorkMax="120";vm.RestMin="5";vm.RestMax="10";vm.SaveSchedule();Assert(json.Load().MaxAccounts==2,"Valid schedule persists");vm.GatherMin="40";vm.GatherMax="20";vm.SaveDaily();Assert(vm.Status.StartsWith("Gathering duration"),"Invalid gathering range rejected");}
   using(var capVm=new MainViewModel(json,reg)){reg.WriteVerified("Enhance","AutoUpgradeMaxBattlePowerK",77);reg.WriteVerified("Enhance","AutoUpgradeMaxBattlePowerK",77);capVm.ReloadRegistry();Assert(capVm.UpgradeMaxBattlePowerK=="77","Combat-power cap loads from its registry mapping");capVm.UpgradeMaxBattlePowerK="55";capVm.SaveUpgradeMaxBattlePowerK();Assert(reg.Read("Enhance","AutoUpgradeMaxBattlePowerK")==55,"Combat-power slider value persists");capVm.UpgradeMaxBattlePowerK="0";capVm.SaveUpgradeMaxBattlePowerK();Assert(reg.Read("Enhance","AutoUpgradeMaxBattlePowerK")==55&&capVm.Status.Contains("1–100K"),"Combat-power cap rejects values below the verified slider range");capVm.UpgradeMaxBattlePowerK="101";capVm.SaveUpgradeMaxBattlePowerK();Assert(reg.Read("Enhance","AutoUpgradeMaxBattlePowerK")==55,"Combat-power cap rejects values above the verified slider range");var od=capVm.Toggles.Single(x=>x.Name=="AutoBuyOdEnergy");var revive=capVm.Toggles.Single(x=>x.Name=="AutoBuyReviveStone");od.Value=false;revive.Value=true;Assert(reg.Read("Enhance","AutoBuyOdEnergy")==0&&reg.Read("Enhance","AutoBuyReviveStone")==1,"Membership Benefits toggles persist independently");}
   using(var deniedCapVm=new MainViewModel(json,new DeniedRegistry())){deniedCapVm.UpgradeMaxBattlePowerK="50";deniedCapVm.SaveUpgradeMaxBattlePowerK();Assert(deniedCapVm.Status.Contains("not saved")&&deniedCapVm.CurrentUpgradeMaxBattlePowerK.Contains("Default"),"Failed combat-power registry writes do not claim persistence");}
   var mock=Path.Combine(root,"config");Directory.CreateDirectory(mock);File.WriteAllText(Path.Combine(mock,"settings.json"),"{\"unrelated\":1}");var discovery=new ConfigurationDiscoveryService(json);Assert(!discovery.DiscoverAsync(mock).GetAwaiter().GetResult().Any(c=>c.Folder==mock),"Filename alone does not establish schema");File.WriteAllText(Path.Combine(mock,"profile.json"),"{\"AutoCombatConfig\":{},\"GatherConfig\":{},\"AuctionConfig\":{}}");Assert(discovery.DiscoverAsync(mock).GetAwaiter().GetResult().Any(c=>c.Folder==mock),"Observed schema signature discovered");Assert(!discovery.DiscoverAsync(Path.Combine(root,"missing")).GetAwaiter().GetResult().Any(c=>c.Folder.EndsWith("missing")),"Missing configuration folder handled");
   var games=new GameInstallationDiscoveryService();var steam=Path.Combine(root,"Steam","steamapps","common","AION2","Binaries","AION2.exe");Directory.CreateDirectory(Path.GetDirectoryName(steam)!);File.WriteAllText(steam,"");Assert(games.IdentifyClient(steam)=="Steam","Mock Steam structure identified");var purple=Path.Combine(root,"PURPLE","AION2.exe");Assert(games.IdentifyClient(purple).StartsWith("Unknown"),"Unverified PURPLE path does not fabricate launcher metadata");
   Assert(new LanguageSettingsService().OriginalGameLanguageVerified,"Original game language storage now verified");Assert(Translations.Get("简体中文","General")=="常规"&&Translations.Get("한국어","General")=="일반","Navigation translations available");
   TestCheckpoint.Run(nameof(RegistryInitializationTests),()=>RegistryInitializationTests.Run(root,registryRoot,Assert),120);
   TestCheckpoint.Run(nameof(UpdateExperienceTests),()=>UpdateExperienceTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(UpdateSessionTests),()=>UpdateSessionTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(CustomerDiscoveryTests),()=>CustomerDiscoveryTests.Run(root,reg,Assert),120);
   TestCheckpoint.Run(nameof(CompatibilityTests),()=>CompatibilityTests.Run(root,reg,Assert),120);
   TestCheckpoint.Run(nameof(VendorUpdaterTests),()=>VendorUpdaterTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(VendorUpdaterHandoffTests),()=>VendorUpdaterHandoffTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(VendorUpdaterCancellationTests),()=>VendorUpdaterCancellationTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(VendorUpdaterNativeHandoffTests),()=>VendorUpdaterNativeHandoffTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(VendorUpdaterConsentTests),()=>VendorUpdaterConsentTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(VendorOverlayCleanupTests),()=>VendorOverlayCleanupTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(MenuStatusTests),()=>MenuStatusTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(AionInstallationDiscoveryTests),()=>AionInstallationDiscoveryTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(CleanupWorkflowTests),()=>CleanupWorkflowTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(UpdaterCacheCleanupTests),()=>UpdaterCacheCleanupTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(UpdaterDataCleanupTests),()=>UpdaterDataCleanupTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(DiagnosticsTests),()=>DiagnosticsTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(PathMatchedDxgiCleanupTests),()=>PathMatchedDxgiCleanupTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(CleanupCommandLineTests),()=>CleanupCommandLineTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(CharacterSettingsRepairTests),()=>CharacterSettingsRepairTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(RuntimeDisplayTests),()=>RuntimeDisplayTests.Run(root,reg,Assert),120);
   TestCheckpoint.Run(nameof(ReleaseReadinessTests),()=>ReleaseReadinessTests.Run(root,registryRoot,Assert),120);
   TestCheckpoint.Run(nameof(StorageAuditTests),()=>StorageAuditTests.Run(root,registryRoot,Assert),120);
   TestCheckpoint.Run(nameof(SharedSettingsSyncTests),()=>SharedSettingsSyncTests.Run(root,reg,Assert),120);
   TestCheckpoint.Run(nameof(CaptchaMonitorTests),()=>CaptchaMonitorTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(RuntimeMcpClientTests),()=>RuntimeMcpClientTests.Run(root,Assert),120);
   TestCheckpoint.Run(nameof(GameIpcTests),()=>GameIpcTests.Run(root,Assert),60);
   TestCheckpoint.Run(nameof(CaptchaAutoSolverTests),()=>CaptchaAutoSolverTests.Run(root,Assert),60);
   TestCheckpoint.Run(nameof(LauncherProvenanceTests),()=>LauncherProvenanceTests.Run(Assert),120);
   RunWpfRegression(root,json,reg);
   Console.WriteLine($"ALL {passed} CHECKS PASSED");return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally{TestCheckpoint.PreserveArtifacts(root);Environment.SetEnvironmentVariable("HEXOR_TEST_ARTIFACTS",null);Registry.CurrentUser.DeleteSubKeyTree(registryRoot,false);var resolved=Path.GetFullPath(root);if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(resolved).StartsWith("HexorTests-"))throw new InvalidOperationException("Invalid cleanup target");foreach(var file in Directory.EnumerateFiles(resolved,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);Directory.Delete(resolved,true);}
 }
 private static int RunFocused(Action<string,Action<bool,string>> suite)
 {
  var root=Path.Combine(Path.GetTempPath(),"HexorFocusedTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  Diagnostics.ConfigureRoot(Path.Combine(root,"logs"));
  try{suite(root,Assert);Console.WriteLine($"ALL {passed} FOCUSED CHECKS PASSED");return 0;}
  catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally
  {
   TestCheckpoint.PreserveArtifacts(root);
   var resolved=Path.GetFullPath(root);
   if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(resolved).StartsWith("HexorFocusedTests-"))throw new InvalidOperationException("Invalid focused cleanup target");
   if(Directory.Exists(resolved))
   {
    foreach(var file in Directory.EnumerateFiles(resolved,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
    Directory.Delete(resolved,true);
   }
  }
 }
 private static void RunWpfOnly(string root)
 {
  var registryRoot="Software\\HexorTests\\"+Guid.NewGuid().ToString("N");
  var config=Path.Combine(root,"verified","config");Directory.CreateDirectory(config);
  JsonObject Envelope(Hexor.Compatibility.WorkflowPlan plan)=>new(){["FormatVersion"]=1,["Values"]=new JsonObject{["UserLanguage"]="en-US",["FutureScalar"]="preserve"},["Workflow"]=plan.ToJson(),["WorkflowDefaultVersion"]=2,["FutureRoot"]=new JsonObject{["keep"]=true}};
  File.WriteAllText(Path.Combine(config,"settings.json"),Envelope(Hexor.Compatibility.WorkflowPlan.Default()).ToJsonString());
  File.WriteAllText(Path.Combine(config,"machine.json"),Envelope(new()).ToJsonString());
  var json=new JsonConfigurationService(Path.Combine(root,"preferences.json"),new SettingsBackupService());json.Load();
  Environment.SetEnvironmentVariable("HEXOR_TEST_ARTIFACTS",Path.Combine(root,"artifacts"));
  try{RunWpfRegression(root,json,new RegistrySettingsService(registryRoot));}
  finally{Environment.SetEnvironmentVariable("HEXOR_TEST_ARTIFACTS",null);Registry.CurrentUser.DeleteSubKeyTree(registryRoot,false);}
 }
 private static void RunWpfRegression(string root,IJsonConfigurationService json,IRegistrySettingsService reg)
 {
  var completed=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
  var thread=new Thread(()=>
  {
   Application? app=null;
   try
   {
    TestCheckpoint.Write("WPF: load production resources");app=TestCheckpoint.CreateApplication();
    var retainedFiles=Directory.GetFiles(Path.Combine(root,"verified","config"),"*.json").ToDictionary(p=>p,File.ReadAllBytes);
    var captchaLogs=Path.Combine(root,"wpf-captcha-fixture");
    Directory.CreateDirectory(captchaLogs);
    var captchaLog=Path.Combine(captchaLogs,"debug_20260101_000000.log");
    File.WriteAllText(captchaLog,"[Main] fixture startup\n");
    var captchaMonitor=new Hexor.Runtime.CaptchaMonitor([new Hexor.Runtime.CaptchaLogTailSource(captchaLogs)]);
    var window=new MainWindow(new MainViewModel(json,reg,new Hexor.Compatibility.LegionConfigurationService(Path.Combine(root,"verified")),captchaMonitor:captchaMonitor),automaticallyCheckForUpdates:false);
    EventHandler? rendered=null;
    rendered=(_,_)=>{window.ContentRendered-=rendered;window.Dispatcher.BeginInvoke(async () =>
    {
     try
     {
      TestCheckpoint.Write("WPF: initial load");await window.InitialLoadCompleted;
      Assert(app.Windows.Count==1&&ReferenceEquals(app.Windows[0],window),"WPF test host opens only the fixture window without customer startup dialogs");
      for(var i=0;i<9;i++){var tab=i;TestCheckpoint.Run("WPF: initial navigation "+tab,()=>{window.ShowPage(tab);window.UpdateLayout();});Assert(window.Content!=null,"WPF navigation page "+i);}
      window.ShowPage(1);window.UpdateLayout();
      System.Windows.Controls.RadioButton Radio(string name)=>RuntimeDisplayTests.All<System.Windows.Controls.RadioButton>(window).Single(r=>r.Name==name);
      Assert(Radio("AuctionPriceModeAverage").IsChecked==true,"Auto Auction radio displays the initialized Average registry value");
      window.Model.AuctionPriceMode=0;window.UpdateLayout();
      Assert(Radio("AuctionPriceModeLowest").IsChecked==true,"Auto Auction radio follows the saved Lowest registry value");
      window.Model.InitializeRegistryDefaults();window.UpdateLayout();
      Assert(Radio("AuctionPriceModeAverage").IsChecked==true&&window.Model.Toggles.Single(t=>t.Name=="AuctionAutoSell").Value==false,
       "Initialization refreshes bound Auto Auction controls to the actual OFF and Average values");
      var layerLower=Radio("AbyssLayerLower");var layerMiddle=Radio("AbyssLayerMiddle");
      Assert(layerLower.IsChecked==true&&!layerMiddle.IsChecked==true,"Abyss layer radios show the saved Lower value");
      layerMiddle.IsChecked=true;window.UpdateLayout();
      Assert(reg.Read("ResourceGrab","AbyssLayer")==23,"Abyss layer radio persists Middle 23");
      window.Model.ReloadRegistry();window.UpdateLayout();
      Assert(layerMiddle.IsChecked==true,"Abyss layer radios refresh from existing registry choices");
      layerLower.IsChecked=true;window.UpdateLayout();
      Assert(reg.Read("ResourceGrab","AbyssLayer")==21,"Abyss layer radio persists Lower 21");
      window.ShowPage(2);window.UpdateLayout();
      var dungeonNormal=Radio("DungeonModeSmart");var dungeonConquest=Radio("DungeonModeOrder");
      var dungeonList=LogicalTreeHelper.FindLogicalNode(window,"DungeonPriorityList") as System.Windows.Controls.ItemsControl;
      Assert(dungeonNormal.IsChecked==true&&!dungeonConquest.IsChecked==true
       &&dungeonList!=null&&window.Model.DungeonPriority.Count==4
       &&window.Model.DungeonPriority.Select(r=>r.MapId).SequenceEqual(new[]{600001,600032,600031,600012})
       &&window.Model.DungeonPriority.Count(r=>r.Enabled)==3,
       "Dungeon radios and priority show exact choices and the vendor-default order (Uruguru Cave disabled)");
      dungeonConquest!.IsChecked=true;window.UpdateLayout();
      Assert(reg.Read("ResourceGrab","AutoDungeonMode")==1,"Dungeon mode radio persists Priority order 1");
      window.UpdateLayout();
      Assert(dungeonList!.Items.Count==4&&window.Model.DungeonPriority.All(r=>!string.IsNullOrEmpty(r.Label)&&r.Ordinal>=1&&r.Ordinal<=4),
       "Dungeon priority rows keep their own numbered label element beside each toggle");
      window.Model.ReloadRegistry();window.UpdateLayout();
      Assert(dungeonConquest.IsChecked==true,"Dungeon mode radios refresh from existing registry choices");
      dungeonNormal.IsChecked=true;window.UpdateLayout();
      Assert(reg.Read("ResourceGrab","AutoDungeonMode")==0,"Dungeon mode radio persists Normal 0");
      var uruguru=window.Model.DungeonPriority.First(r=>r.MapId==600012);uruguru.Enabled=true;window.UpdateLayout();
      var priority=(string?)reg.ReadString("ResourceGrab","AutoDungeonPriority");
      Assert(priority!=null&&priority.Split('\n').SequenceEqual(new[]{"1\t600001","1\t600032","1\t600031","1\t600012"}),
       "Dungeon priority edits write the vendor REG_SZ format in the current row order");
      window.Model.MoveDungeonPriority(600012,-1);window.UpdateLayout();
      priority=(string?)reg.ReadString("ResourceGrab","AutoDungeonPriority");
      Assert(priority!=null&&priority.Split('\n').Skip(2).First()=="1\t600012","Dungeon priority reorder writes the moved row one slot up");
      window.Model.ResetDungeonPriority();window.UpdateLayout();
      priority=(string?)reg.ReadString("ResourceGrab","AutoDungeonPriority");
      Assert(priority!=null&&priority.Split('\n').SequenceEqual(new[]{"1\t600001","1\t600032","1\t600031","0\t600012"}),
       "Reset Default restores the vendor default priority and selection");
      window.ShowPage(1);window.UpdateLayout();
      Radio("AuctionPriceModeAverage").BringIntoView();window.UpdateLayout();
      var resourceImage=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);resourceImage.Render(window);
      var resourceEncoder=new PngBitmapEncoder();resourceEncoder.Frames.Add(BitmapFrame.Create(resourceImage));
      Directory.CreateDirectory(Path.Combine(root,"artifacts"));using(var stream=File.Create(Path.Combine(root,"artifacts","resource-selectors.png")))resourceEncoder.Save(stream);
      RuntimeDisplayTests.RunWpf(window,Assert);window.ShowPage(0);window.UpdateLayout();
      (LogicalTreeHelper.FindLogicalNode(window,"UnloadMenuButton") as FrameworkElement)?.BringIntoView();window.UpdateLayout();
      var releaseScreenshot=Environment.GetEnvironmentVariable("HEXOR_CAPTURE_RELEASE_SCREENSHOT")=="1"?Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),"output","validation-1.0.10","ui-general.png")):Path.Combine(root,"artifacts","ui-general.png");
      TestCheckpoint.Write("WPF: general screenshot render/save");Directory.CreateDirectory(Path.GetDirectoryName(releaseScreenshot)!);var generalImage=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);generalImage.Render(window);var generalEncoder=new PngBitmapEncoder();generalEncoder.Frames.Add(BitmapFrame.Create(generalImage));using(var stream=File.Create(releaseScreenshot))generalEncoder.Save(stream);
      TestCheckpoint.Write("WPF: interface screenshot render/save");window.ShowPage(1);window.UpdateLayout();var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));var screenshot=Path.Combine(root,"artifacts","interface-preview.png");Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);using(var stream=File.Create(screenshot))encoder.Save(stream);
      await CaptchaMonitorTests.RunWpfAsync(window,captchaLog,Assert);
      RuntimeDisplayTests.CloseThroughTitleBar(window,Assert);Assert(retainedFiles.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Startup and navigation preserve all original configuration documents");completed.TrySetResult(null);
     }
     catch(Exception e){completed.TrySetResult(e);app.Shutdown(1);}
    },System.Windows.Threading.DispatcherPriority.Loaded);};
    window.ContentRendered+=rendered;
    app.Run(window);
   }
   catch(Exception e){completed.TrySetResult(e);}
  }){IsBackground=true,Name="Hexor WPF regression STA"};
  thread.SetApartmentState(ApartmentState.STA);thread.Start();
  if(!completed.Task.Wait(TimeSpan.FromMinutes(2)))Environment.FailFast("WPF regression exceeded its two-minute dispatcher/render timeout.");
  if(completed.Task.Result is { } failure)throw new Exception("WPF regression failed on its STA dispatcher thread.",failure);
  if(!thread.Join(TimeSpan.FromSeconds(10)))Environment.FailFast("WPF regression dispatcher did not exit after window closure.");
 }
 private sealed class DeniedRegistry : IRegistrySettingsService{public int? Read(string section,string name)=>throw new UnauthorizedAccessException();public void WriteVerified(string section,string name,int value)=>throw new UnauthorizedAccessException();}
}


