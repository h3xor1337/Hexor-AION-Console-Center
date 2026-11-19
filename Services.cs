using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Win32;
using Hexor.Compatibility;
namespace Hexor;
public enum DiagnosticSeverity { Information, Warning, Error }
public static class Diagnostics
{
 static readonly object logLock=new();
 static readonly string productionRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Hexor","ControlCenter","Logs");
 static string root=productionRoot;
 static bool loggingStarted;
 public static string Root { get { lock(logLock) return root; } }
 public static string ProductionRoot=>productionRoot;
 public static string SessionId {get;private set;}=Guid.NewGuid().ToString("N");
 public static string? LastExportPath {get;private set;}
 public static string? LastExportError {get;private set;}
 public const long ActiveLogMaxBytes=10*1024*1024;
 const int ArchiveRetention=3;
 public static string ActiveLogPath=>Path.Combine(Root,"hexor.log");
 public static long ActiveLogSize { get { try{return new FileInfo(ActiveLogPath).Length;}catch{return 0;} } }
 // Test hosts may redirect diagnostics before the first logging operation. Production never calls this.
 public static void ConfigureRoot(string configuredRoot){if(string.IsNullOrWhiteSpace(configuredRoot))throw new ArgumentException("Diagnostics root is required.",nameof(configuredRoot));lock(logLock){if(loggingStarted)throw new InvalidOperationException("Diagnostics root cannot change after logging has started.");root=Path.GetFullPath(configuredRoot);}}
 public static void Initialize(){Log("startup",$"version={System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}; windows={Environment.OSVersion.Version}; elevated={IsElevated()}");}
 public static void Log(string area,string outcome)=>TryLog(area,outcome);
 // Keep the two-argument API and historical classification for unrelated callers.
 public static bool TryLog(string area,string outcome)=>TryLog(area,outcome,Severity(Clean(area),Clean(outcome)));
 public static void Log(string area,string outcome,DiagnosticSeverity severity)=>TryLog(area,outcome,severity);
 public static bool TryLog(string area,string outcome,DiagnosticSeverity severity)=>TryLog(area,outcome,severity switch
 {DiagnosticSeverity.Error=>"ERROR",DiagnosticSeverity.Warning=>"WARN",_=>"INFO"});
 static bool TryLog(string area,string outcome,string severity)
 {
  lock(logLock)
  {
   loggingStarted=true;
   try
   {
    Directory.CreateDirectory(Root);
    using var processGate=OpenProcessGate();
    if(!Wait(processGate))return false;
    try
    {
     var component=Clean(area);var details=Clean(outcome);
     var record=$"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{severity}] [{SessionId}] [pid {Environment.ProcessId}] {component}: {Flatten(details)}"+Environment.NewLine;
     var bytes=Encoding.UTF8.GetBytes(record);RotateIfNeeded(bytes.Length);
     using var stream=new FileStream(ActiveLogPath,FileMode.Append,FileAccess.Write,FileShare.Read,4096,severity=="ERROR"||component.Contains("update",StringComparison.OrdinalIgnoreCase)?FileOptions.WriteThrough:FileOptions.None);
     stream.Write(bytes);stream.Flush(severity=="ERROR"||component.Contains("update",StringComparison.OrdinalIgnoreCase));
     return true;
    }
    finally{processGate.ReleaseMutex();}
   }
   catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Threading.WaitHandleCannotBeOpenedException){return false;}
  }
 }
 public static string ExportSupportLogs(){lock(logLock){var export=Path.Combine(Root,"Hexor-support-"+SessionId+".zip");try{LastExportError=null;Directory.CreateDirectory(Root);if(File.Exists(export))File.Delete(export);using var zip=ZipFile.Open(export,ZipArchiveMode.Create);var logs=new[]{ActiveLogPath}.Where(File.Exists).Concat(Archives().Take(ArchiveRetention)).ToArray();foreach(var file in logs){var entry=zip.CreateEntry(Path.GetFileName(file),CompressionLevel.Optimal);using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));foreach(var line in File.ReadLines(file))writer.WriteLine(RedactRecord(line));}using(var writer=new StreamWriter(zip.CreateEntry("diagnostic-summary.json").Open()))writer.Write(JsonSerializer.Serialize(new{sessionId=SessionId,version=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version,created=DateTimeOffset.UtcNow,logCount=logs.Length,activeLog="hexor.log"}));LastExportPath=export;return export;}catch(Exception e){LastExportError=e.GetType().Name+": "+e.Message;return "";}}}
 static Mutex OpenProcessGate(){var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).ToUpperInvariant())))[..24];return new(false,"Local\\HexorDiagnostics-"+hash);}
 static bool Wait(Mutex gate){try{return gate.WaitOne(TimeSpan.FromSeconds(3));}catch(AbandonedMutexException){return true;}}
 static IEnumerable<string> Archives()=>Directory.Exists(Root)?Directory.EnumerateFiles(Root,"hexor-*.log").OrderByDescending(File.GetLastWriteTimeUtc):[];
 static void RotateIfNeeded(int incomingBytes)
 {
  var active=ActiveLogPath;if(!File.Exists(active)||new FileInfo(active).Length+incomingBytes<=ActiveLogMaxBytes)return;
  var archive=Path.Combine(Root,$"hexor-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
  File.Move(active,archive,false);
  foreach(var old in Archives().Skip(ArchiveRetention))try{File.Delete(old);}catch(IOException){}catch(UnauthorizedAccessException){}
 }
 static string RedactRecord(string line){if(line.Length>64*1024)return $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [WARN] [{SessionId}] [pid {Environment.ProcessId}] support-export: oversized or malformed historical diagnostic record omitted.";return Clean(line);}
 // Historical JSON records and structured detail payloads are flattened to readable text:
 // {"Outcome":"Attempt","OperationId":"x"} -> Outcome=Attempt, OperationId=x
 static string Flatten(string details)
 {
  var text=details??"";
  if(text.Length<2||text[0]!='{')return text;
  try
  {
   if(JsonNode.Parse(text) is not JsonObject obj)return text;
   return string.Join(", ",obj.Select(pair=>pair.Key+"="+(pair.Value is JsonValue value&&value.TryGetValue<string>(out var s)?s:pair.Value is null?"null":pair.Value is JsonArray array?"["+string.Join(",",array.Select(item=>item is JsonValue v&&v.TryGetValue<string>(out var item2)?item2:item?.ToJsonString()??"null"))+"]":pair.Value?.ToJsonString()??"null")));
  }
  catch{return text;}
 }
 static string Severity(string area,string details)=>area.Contains("error",StringComparison.OrdinalIgnoreCase)||area.Contains("unhandled",StringComparison.OrdinalIgnoreCase)||details.Contains("failed",StringComparison.OrdinalIgnoreCase)||details.Contains("error",StringComparison.OrdinalIgnoreCase)?"ERROR":"INFO";
 static string Clean(string value){var text=value??"";text=Regex.Replace(text,"(?i)(license|token|password|authorization|secret|key)\\s*[:=]\\s*[^;\\s]+","$1=[redacted]");text=Regex.Replace(text,"[\\r\\n]"," ");return text.Length>480?text[..480]:text;}
 static bool IsElevated(){try{return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}catch{return false;}}
}
public interface ISettingsBackupService {string Backup(string path);}
public sealed class SettingsBackupService : ISettingsBackupService
{public string Backup(string path){var target=path+"."+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"."+Guid.NewGuid().ToString("N")+".bak";File.Copy(path,target,false);return target;}}
public interface IJsonConfigurationService {Preferences Load();void Save(Preferences preferences);Task<JsonObject?> ReadObjectAsync(string path,CancellationToken token=default);Task UpdatePropertyAsync(string path,string property,JsonNode? value);}
public sealed class JsonConfigurationService(string path,ISettingsBackupService backup) : IJsonConfigurationService
{
 private readonly JsonSerializerOptions options=new(){WriteIndented=true,Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}};
 private bool invalid;
 public Preferences Load(){if(!File.Exists(path)){var p=new Preferences();Save(p);return p;}try{var text=File.ReadAllText(path);var p=JsonSerializer.Deserialize<Preferences>(text,options)??throw new JsonException();if(p.SchemaVersion!=1||p.Instructions==null||p.Instructions.Any(i=>i==null||!Enum.IsDefined(i.Type)))throw new JsonException();var raw=JsonNode.Parse(text)?["Instructions"] as JsonArray;if(raw!=null){for(int index=0;index<raw.Count;index++){if(raw[index] is JsonObject item&&!item.ContainsKey("MinimumMinutes")){p.Instructions[index].MinimumMinutes=p.GatherMin;p.Instructions[index].MaximumMinutes=p.GatherMax;p.Instructions[index].ResetTimer=p.ResetTimer;}}}return p;}catch(JsonException){invalid=true;Diagnostics.Log("preferences","invalid JSON; original retained");return new();}}
 public void Save(Preferences p)=>StorageAudit.Run(StorageTarget.HexorPreferences,[],()=>{Directory.CreateDirectory(Path.GetDirectoryName(path)!);if(File.Exists(path)){backup.Backup(path);invalid=false;}Atomic(path,JsonSerializer.Serialize(p,options));return true;},changed=>changed,completed:StorageOutcome.WriteCompleted);
 private static void Atomic(string target,string content){var temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temp,content);File.Move(temp,target,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
 public async Task<JsonObject?> ReadObjectAsync(string source,CancellationToken token=default){try{if(new FileInfo(source).Length>4_000_000)return null;return JsonNode.Parse(await File.ReadAllTextAsync(source,token)) as JsonObject;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){Diagnostics.Log("json",e.GetType().Name);return null;}}
 public async Task UpdatePropertyAsync(string target,string property,JsonNode? value){var obj=await ReadObjectAsync(target)??throw new InvalidDataException("Configuration is not a valid JSON object.");obj[property]=value;backup.Backup(target);Atomic(target,obj.ToJsonString(options));}
 public bool RecoveredInvalidJson=>invalid;
}
public record RegistryEntry(int? Value,bool Exists,bool Supported);
public interface IRegistrySettingsService
{
 int? Read(string section,string name);
 void WriteVerified(string section,string name,int value);
 RegistryEntry Inspect(string section,string name) { var value=Read(section,name);return new(value,value!=null,true); }
 void MirrorAutoTaskNumber(string name,int value) => throw new NotSupportedException("Numeric mirror is unavailable.");
 bool CreateMissingDefault(string section,string name,int value) => throw new NotSupportedException("Initialization is unavailable.");
 bool RestoreStartupDefault(string section,string name) => throw new NotSupportedException("Startup defaults are unavailable.");
 int ReadAppliedSchemaVersion() => 0;
 void WriteAppliedSchemaVersion(int version) { }
 GatheringRegistrySnapshot ReadGathering() => new(new(null,false,false),new(null,false,false));
 string? ReadString(string section,string name) => null;
 void WriteGatheringSelectionVerified(GatheringRegistrySnapshot expected,int targetIndex,bool enabled) => throw new NotSupportedException("Gathering settings are unavailable.");
 void WriteGatheringTargetsVerified(GatheringRegistrySnapshot expected,string targets) => throw new NotSupportedException("Gathering settings are unavailable.");
 DungeonRegistrySnapshot ReadDungeon() => new(new(null,false,false),new(null,false,true),new(null,false,true));
 void WriteDungeonPriorityVerified(IReadOnlyList<(int MapId,bool Enabled)> entries) => throw new NotSupportedException("Dungeon settings are unavailable.");
 IReadOnlyList<(int MapId,bool Enabled)> DungeonDefaults() => [(600001,true),(600032,true),(600031,true),(600012,false)];
}
public sealed partial class RegistrySettingsService(string root="Software\\LegionCommander", Action<string>? auditSink=null, string? schemaStateRoot=null) : IRegistrySettingsService
{
 private readonly string schemaStateRoot = schemaStateRoot ?? (root == "Software\\LegionCommander" ? "Software\\Hexor\\ControlCenter" : root + "\\_HexorSchema");
 private static readonly Dictionary<string,string[]> Allowed=new(){["AutoTask"]=["CharacterRace","CharacterJob","ClearSideQuestsBeforeDungeon","ExploreCavesBeforeDungeon","SwitchToGatherAfterTasks","AutoDailyQuests"],["Enhance"]=["AutoEnchantMaxPower","AutoBuyOdEnergy","AutoBuyReviveStone","AutoDungeonQuickJoinEnabled","AutoDailyWaveEnabled","AutoNightmareEnabled","AutoAwakenEnabled","AutoOdMorphEnabled","AutoUpgradeMaxBattlePowerK","DestroyInsteadOfExtract"],["MiniGame"]=["AutoEnter","UseTaiwanVillageRules"],["ResourceGrab"]=["AutoDungeonMode","AutoDungeonPriority"]};
 private static void Check(string section,string name){if(LegionCommanderRegistrySchema.Find(section,name)==null&&!LegionCommanderFeatureSettings.IsDWord(section,name)&&(!Allowed.TryGetValue(section,out var names)||!names.Contains(name)))throw new InvalidOperationException("Unverified registry setting.");}
 public bool RestoreStartupDefault(string section,string name)=>StorageAudit.Run(StorageTarget.Registry,[section+"."+name],()=>
 {
  var item=LegionCommanderRegistrySchema.Find(section,name);
  if(item?.ResetOnStartup!=true)throw new InvalidOperationException("Setting has no startup reset policy.");
  var before=Inspect(section,name);
  if(before.Exists&&before.Supported&&before.Value==item.InitializationDefault)return false;
  using(var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64))
  using(var key=hive.CreateSubKey(root+"\\"+section))key.SetValue(name,item.InitializationDefault,RegistryValueKind.DWord);
  var after=Inspect(section,name);
  if(!after.Supported||after.Value!=item.InitializationDefault)throw new IOException("Startup default verification failed.");
  LegionSharedSettings.SyncFromRegistry(section,name,root);
  return true;
 },changed=>changed,auditSink);
 public bool CreateMissingDefault(string section,string name,int value)=>StorageAudit.Run(StorageTarget.Registry,[section+"."+name],()=>
 {
  if(!RegistryDefaults.IsValid(section,name,value))throw new InvalidDataException("Unsupported initialization value.");
  using(var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64))
  using(var key=hive.CreateSubKey(root+"\\"+section))
  {
   if(key.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase))return false;
   key.SetValue(name,value,RegistryValueKind.DWord);
  }
  var verified=Inspect(section,name);
  if(!verified.Supported||verified.Value!=value)throw new IOException("Registry initialization verification failed.");
  LegionSharedSettings.SyncFromRegistry(section,name,root);
  return true;
 },changed=>changed,auditSink);
 public int ReadAppliedSchemaVersion(){using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var key=hive.OpenSubKey(schemaStateRoot);return key?.GetValue("LegionCommanderSchemaVersion") is int value&&value>=0?value:0;}
 public void WriteAppliedSchemaVersion(int version){if(version<0)throw new InvalidDataException("Invalid registry schema version.");using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var key=hive.CreateSubKey(schemaStateRoot);if(key.GetValue("LegionCommanderSchemaVersion") is int value&&value==version)return;key.SetValue("LegionCommanderSchemaVersion",version,RegistryValueKind.DWord);}
 public int? Read(string section,string name)=>Inspect(section,name).Value;
 public string? ReadString(string section,string name)
 {
  Check(section,name);
  using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
  using var k=hive.OpenSubKey(root+"\\"+section);
  if(k==null||!k.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase))return null;
  return k.GetValueKind(name)!=RegistryValueKind.String?null:k.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
 }
 public RegistryEntry Inspect(string section,string name){Check(section,name);using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);using var k=hive.OpenSubKey(root+"\\"+section);if(k==null||!k.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase))return new(null,false,true);if(k.GetValueKind(name)!=RegistryValueKind.DWord)return new(null,true,false);return new((int?)k.GetValue(name),true,true);}
 public void WriteVerified(string section,string name,int value)=>StorageAudit.Run(StorageTarget.Registry,[section+"."+name],()=>WriteVerifiedCore(section,name,value),changed=>changed,auditSink);
 private bool WriteVerifiedCore(string section,string name,int value)
 {
  Check(section,name);
  if(name is "CharacterRace" or "CharacterJob")throw new InvalidOperationException("This setting's semantic mapping remains unverified.");
  // DWORD is unsigned; quality enum labels/ranges are not established by one export.
  if(LegionCommanderFeatureSettings.IsQualityLevel(section,name))return StoreVerified(section,name,value);
  if(LegionCommanderRegistrySchema.Find(section,name) is { } definition){if(!definition.IsValid(value))throw new InvalidDataException("Unsupported registry value.");}
  else if(name=="AutoUpgradeMaxBattlePowerK"){if(value<1||value>100)throw new InvalidDataException("Maximum battle power must be 1–100K.");}
  else if(value is not (0 or 1))throw new InvalidDataException("Boolean registry values must be 0 or 1.");
  return StoreVerified(section,name,value);
 }
 public void MirrorAutoTaskNumber(string name,int value)=>StorageAudit.Run(StorageTarget.Registry,["AutoTask."+name],()=>MirrorAutoTaskNumberCore(name,value),changed=>changed,auditSink);
 private bool MirrorAutoTaskNumberCore(string name,int value)
 {
  bool Valid(int number)=>name switch{"CharacterRace"=>number is 1 or 2,"CharacterJob"=>number>=0&&number<=8,_=>false};
  if(!Valid(value))throw new InvalidDataException("Unknown AutoTask numeric encoding preserved.");
  var before=Inspect("AutoTask",name);
  if(before.Value is int existing&&!Valid(existing))throw new InvalidDataException("Unknown existing numeric value preserved.");
  return StoreVerified("AutoTask",name,value);
 }
 private bool StoreVerified(string section,string name,int value)
 {
  var before=Inspect(section,name);if(before.Exists&&!before.Supported)throw new InvalidDataException("Existing unsupported registry type preserved.");
  if(before.Value==value)return false;
  using(var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64))
  using(var k=hive.CreateSubKey(root+"\\"+section)){k.SetValue(name,value,RegistryValueKind.DWord);}
  if(Read(section,name)!=value)throw new IOException("Registry read-back verification failed.");
  LegionSharedSettings.SyncFromRegistry(section,name,root);
  Diagnostics.Log("registry","single value verified");return true;
 }
}
public interface IConfigurationDiscoveryService {Task<IReadOnlyList<ConfigurationCandidate>> DiscoverAsync(string? selected,CancellationToken token=default);}
public sealed class ConfigurationDiscoveryService(IJsonConfigurationService json) : IConfigurationDiscoveryService
{
 public async Task<IReadOnlyList<ConfigurationCandidate>> DiscoverAsync(string? selected,CancellationToken token=default)
 {
  var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Aion2Updater"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LegionCommander"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"LegionCommander"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"LegionCommander")};
  if(!string.IsNullOrWhiteSpace(selected))paths.Add(selected);
  // Bounded, nonrecursive root enumeration: inspect only GUID-named folders with known signatures.
  try{foreach(var p in Directory.EnumerateDirectories(Path.GetPathRoot(Environment.SystemDirectory)!))if(Guid.TryParse(Path.GetFileName(p),out _))paths.Add(p);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Diagnostics.Log("discovery",e.GetType().Name);}
  var found=new List<ConfigurationCandidate>();
  foreach(var folder in paths){token.ThrowIfCancellationRequested();if(!Directory.Exists(folder))continue;try{foreach(var file in Directory.EnumerateFiles(folder,"*.json").Take(80)){var obj=await json.ReadObjectAsync(file,token);if(obj?["AutoCombatConfig"] is JsonObject&&obj["GatherConfig"] is JsonObject&&obj["AuctionConfig"] is JsonObject){found.Add(new(folder,"Observed character-settings schema: AutoCombatConfig, GatherConfig, AuctionConfig. Active character unverified."));break;}}}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Diagnostics.Log("discovery",e.GetType().Name);}}
  Diagnostics.Log("discovery",$"{found.Count} schema candidates");return found;
 }
}
public interface IGameInstallationDiscoveryService {string IdentifyClient(string executablePath);Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken token=default);}
public sealed class GameInstallationDiscoveryService : IGameInstallationDiscoveryService
{
 public string IdentifyClient(string path){var full=Path.GetFullPath(path);var dir=new DirectoryInfo(Path.GetDirectoryName(full)!);for(var i=0;dir!=null&&i<6;i++,dir=dir.Parent){if(dir.Name.Equals("common",StringComparison.OrdinalIgnoreCase)&&dir.Parent?.Name.Equals("steamapps",StringComparison.OrdinalIgnoreCase)==true)return "Steam";}return "Unknown (launcher unverified)";}
 public Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken token=default)=>Task.Run<IReadOnlyList<string>>(()=>{var roots=new List<string>();using(var steam=Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam")){if(steam?.GetValue("SteamPath") is string path)roots.Add(path);}var results=new List<string>();foreach(var root in roots){token.ThrowIfCancellationRequested();var libraries=new List<string>{root};var vdf=Path.Combine(root,"steamapps","libraryfolders.vdf");if(File.Exists(vdf)){try{foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf),"\"path\"\\s+\"([^\"]+)\""))libraries.Add(m.Groups[1].Value.Replace("\\\\","\\"));}catch(IOException){}}foreach(var lib in libraries.Distinct()){var common=Path.Combine(lib,"steamapps","common");if(!Directory.Exists(common))continue;try{results.AddRange(Directory.EnumerateDirectories(common).Where(p=>Path.GetFileName(p).Equals("AION2",StringComparison.OrdinalIgnoreCase)));}catch(UnauthorizedAccessException){}}}return results;},token);
}
public interface ICharacterDetectionService {Task<Detection> DetectAsync(CancellationToken token=default);}
public sealed class CharacterDetectionService(IGameInstallationDiscoveryService installations) : ICharacterDetectionService
{
 private readonly Runtime.ReadOnlyRuntimeDetector detector = new(installations, new Runtime.WindowsGameProcessSource(), new Runtime.UnavailableIdentitySource());
 public Task<Detection> DetectAsync(CancellationToken token=default)=>detector.DetectAsync(token);
}
public interface ILanguageSettingsService {void SetUi(Preferences p,string language);void SetGamePreference(Preferences p,string language);bool OriginalGameLanguageVerified {get;}}
public sealed class LanguageSettingsService : ILanguageSettingsService
{public bool OriginalGameLanguageVerified=>true;public void SetUi(Preferences p,string language)=>p.UiLanguage=language;public void SetGamePreference(Preferences p,string language)=>p.GameLanguage=language;}

