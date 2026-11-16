using System.IO;
using System.Text.Json.Nodes;
using Hexor;
using Hexor.Compatibility;

internal static class StorageAuditTests
{
    public static void Run(string root,string registryRoot,Action<bool,string> check)
    {
        var events=new List<string>();
        var folder=Path.Combine(root,"audit-logging");Directory.CreateDirectory(Path.Combine(folder,"config"));
        var path=Path.Combine(folder,"config","settings.json");
        var envelope=new JsonObject { ["FormatVersion"]=1,["Values"]=new JsonObject { ["PrivateUnknown"]="secret-value" },
            ["Workflow"]=WorkflowPlan.Default().ToJson(),["WorkflowDefaultVersion"]=2 };
        File.WriteAllText(path,envelope.ToJsonString());
        var service=new LegionConfigurationService(folder,auditSink:events.Add);
        var snapshot=service.Read();
        check(events.Count==0,"Read-only loading does not emit configuration write events");
        service.SaveScalars(snapshot,new Dictionary<string,string> { ["GameLanguage"]="ko-KR" });
        var attempt=JsonNode.Parse(events[0])!;var verified=JsonNode.Parse(events[1])!;
        check(events.Count==2 && attempt["Outcome"]!.GetValue<string>()=="Attempt" && verified["Outcome"]!.GetValue<string>()=="ReadBackVerified",
            "JSON audit distinguishes write attempt from successful storage validation");
        check(attempt["OperationId"]!.GetValue<string>()==verified["OperationId"]!.GetValue<string>() && Guid.TryParseExact(attempt["OperationId"]!.GetValue<string>(),"N",out _)
            && DateTimeOffset.Parse(verified["TimestampUtc"]!.GetValue<string>()).Offset==TimeSpan.Zero,"Write records correlate by operation and UTC timestamp");
        check(verified["ValidationScope"]!.GetValue<string>()=="StorageOnly" && verified["EngineApplication"]!.GetValue<string>()=="Unverified",
            "Storage success is never logged as engine validation");
        check(!string.Join(" ",events).Contains("secret-value") && !string.Join(" ",events).Contains(folder) && !string.Join(" ",events).Contains("ko-KR"),
            "Audit excludes configuration values and paths");
        events.Clear();snapshot=service.Read();
        service.SaveScalars(snapshot,new Dictionary<string,string> { ["GameLanguage"]="ko-KR" });
        check(JsonNode.Parse(events[^1])!["Outcome"]!.GetValue<string>()=="NoChange","Unchanged configuration is logged without claiming another write");
        events.Clear();
        try { service.SaveScalars(snapshot,new Dictionary<string,string> { ["secret-key\npassword"]="secret" }); } catch(InvalidDataException) { }
        check(events.Count==2 && events[^1].Contains("Rejected") && !string.Join(" ",events).Contains("password"),"Rejected unknown settings are audited without exposing arbitrary keys");
        events.Clear();File.AppendAllText(path," ");
        try {service.SaveScalars(snapshot,new Dictionary<string,string> { ["GameLanguage"]="en-US" });}catch(ConfigurationConflictException) { }
        check(events[^1].Contains("ConfigurationConflict") && events[^1].Contains("Rejected"),"External-edit conflict produces a categorical rejected result");
        events.Clear();
        var failed=new LegionConfigurationService(folder,new DeniedBackup(),events.Add);
        try {failed.SaveScalars(failed.Read(),new Dictionary<string,string> { ["GameLanguage"]="en-US" });}catch(IOException) { }
        check(events[^1].Contains("Failed") && !string.Join(" ",events).Contains("private-token"),"Backup failure logs category without sensitive exception message");
        events.Clear();
        var registry=new RegistrySettingsService(registryRoot+@"\Audit",events.Add);
        registry.WriteVerified("MiniGame","AutoEnter",0);registry.WriteVerified("MiniGame","AutoEnter",0);
        check(events.Count==4 && events[1].Contains("ReadBackVerified") && events[3].Contains("NoChange") && events[1].Contains("MiniGame.AutoEnter"),
            "Registry audit records allowlisted setting plus read-back and no-change outcomes");
        var throwingSink=new LegionConfigurationService(folder,auditSink:_=>throw new IOException("logging unavailable"));
        check(throwingSink.SaveScalars(throwingSink.Read(),new Dictionary<string,string> { ["GameLanguage"]="en-US" }).Value("GameLanguage")=="en-US",
            "Logging failure cannot prevent successful configuration persistence");
        var sanitized=StorageAudit.Format(StorageTarget.HexorPreferences,["CharacterName","LicenseKey"],"private-token",StorageOutcome.WriteCompleted,new Exception("private-token"));
        check(!sanitized.Contains("LicenseKey") && !sanitized.Contains("CharacterName") && !sanitized.Contains("private-token") && sanitized.Contains("WriteCompleted"),
            "Local preference audit excludes manual profile fields and does not claim read-back");
    }
    private sealed class DeniedBackup:ISettingsBackupService { public string Backup(string path)=>throw new IOException("private-token"); }
}
