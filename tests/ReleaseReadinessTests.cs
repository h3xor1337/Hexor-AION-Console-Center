using System.IO;
using System.Text.Json.Nodes;
using Hexor;
using Hexor.Compatibility;
using Hexor.Runtime;
using Microsoft.Win32;

internal static class ReleaseReadinessTests
{
    public static void Run(string root, string testRegistryRoot, Action<bool,string> check)
    {
        var folder = Path.Combine(root,"release-audit"); Directory.CreateDirectory(Path.Combine(folder,"config"));
        var path = Path.Combine(folder,"config","settings.json");
        var envelope = new JsonObject { ["FormatVersion"]=1,["Values"]=new JsonObject { ["FutureSetting"]="retained" },
            ["Workflow"]=WorkflowPlan.Default().ToJson(),["WorkflowDefaultVersion"]=2,["FutureRoot"]=42 };
        File.WriteAllText(path,envelope.ToJsonString());
        var registryPath = testRegistryRoot + @"\Completeness";
        var registry = new RegistrySettingsService(registryPath);
        var service = new LegionConfigurationService(folder);
        var session = new VerifiedSettingsSession(service,registry);
        foreach (var name in new[] { "ClearSideQuestsBeforeDungeon","ExploreCavesBeforeDungeon","SwitchToGatherAfterTasks" })
        {
            session.SaveAutoTask(name,false);
            var restarted = new VerifiedSettingsSession(new LegionConfigurationService(folder),new RegistrySettingsService(registryPath));
            check(restarted.ReadAutoTask(name)==0 && registry.Read("AutoTask",name)==0,
                "Audit restart: AutoTask JSON/registry " + name);
        }
        foreach (var (section,name,value) in new[] { ("Enhance","AutoBuyOdEnergy",0),
            ("Enhance","AutoBuyReviveStone",0),("Enhance","AutoDungeonQuickJoinEnabled",0),("Enhance","DestroyInsteadOfExtract",1),("MiniGame","AutoEnter",0),("MiniGame","UseTaiwanVillageRules",1) })
        {
            using (var key=Registry.CurrentUser.CreateSubKey(registryPath+"\\"+section)) key.SetValue("FutureValue","retain",RegistryValueKind.String);
            registry.WriteVerified(section,name,value);
            using var after=Registry.CurrentUser.OpenSubKey(registryPath+"\\"+section);
            check(new RegistrySettingsService(registryPath).Read(section,name)==value && (string?)after?.GetValue("FutureValue")=="retain",
                "Audit restart and unrelated registry preservation: " + name);
        }
        session.SaveSchedule(new(true,5,80,90,10,15));
        session.SaveLanguage("UserLanguage","en-US"); session.SaveLanguage("GameLanguage","ko-KR");
        var restartedDocument=new LegionConfigurationService(folder).Read();
        check(ScheduleSettings.Read(restartedDocument)==new ScheduleSettings(true,5,80,90,10,15)
            && restartedDocument.Value("UserLanguage")=="en-US" && restartedDocument.Value("GameLanguage")=="ko-KR",
            "Audit restart: all schedule scalars and original language preferences");
        check(restartedDocument.Value("FutureSetting")=="retained" && restartedDocument.Document!["FutureRoot"]!.GetValue<int>()==42,
            "Audit cross-tab saves preserve unknown JSON settings and root properties");
        using var vm = new MainViewModel(new JsonConfigurationService(Path.Combine(root,"readiness-preferences.json"),new SettingsBackupService()),registry,service);
        check(!vm.CanControlAutomation && vm.AutomationConnectionStatus.StartsWith("Unavailable") && vm.AutomationRunningState.StartsWith("Unknown"),
            "Customer release cannot equate saved configuration with automation connection or enable remote controls");
        check(Enumerable.Range(0,6).Select(AutomationCapabilities.SettingsEffect).All(s=>s.Length>60),"All six tabs expose storage/application limitations");
        check(AutomationCapabilities.SettingsEffect(4).Contains("in-process") && AutomationCapabilities.SettingsEffect(2).Contains("unverified"),
            "Schedule notification and engine registry timing limitations stay explicit");
    }
}
