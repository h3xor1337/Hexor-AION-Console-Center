using System.IO;
using System.Text.Json;
using Hexor.Compatibility;

namespace Hexor;

public enum StorageTarget { PortableJson, MachineJson, Registry, HexorPreferences }
public enum StorageOutcome { Attempt, WriteCompleted, ReadBackVerified, NoChange, Rejected, Failed }

public static class StorageAudit
{
    private static readonly HashSet<string> RegistryNames = new(StringComparer.Ordinal)
    {
        "AutoTask.CharacterRace", "AutoTask.CharacterJob", "AutoTask.ClearSideQuestsBeforeDungeon",
        "AutoTask.ExploreCavesBeforeDungeon", "AutoTask.SwitchToGatherAfterTasks",
        "AutoTask.AutoDailyQuests",
        "Enhance.AutoEnchantMaxPower", "Enhance.AutoBuyOdEnergy", "Enhance.AutoBuyReviveStone",
        "Enhance.AutoDungeonQuickJoinEnabled", "MiniGame.AutoEnter", "MiniGame.UseTaiwanVillageRules"
    };
    public static T Run<T>(StorageTarget target, IEnumerable<string> settings, Func<T> action,
        Func<T,bool> changed, Action<string>? sink = null, StorageOutcome completed = StorageOutcome.ReadBackVerified)
    {
        var names = settings.ToArray(); var operation = Guid.NewGuid().ToString("N");
        void Record(StorageOutcome outcome, Exception? error = null)
        {
            // Audit failure must never change settings behavior or mask the original exception.
            try
            {
                var json = Format(target,names,operation,outcome,error);
                if (sink != null) sink(json); else Diagnostics.Log("storage-audit",json, outcome is StorageOutcome.Failed or StorageOutcome.Rejected ? DiagnosticSeverity.Error : DiagnosticSeverity.Information);
            }
            catch { }
        }
        Record(StorageOutcome.Attempt);
        try { var result = action(); Record(changed(result) ? completed : StorageOutcome.NoChange); return result; }
        catch (Exception e)
        {
            Record(e is InvalidDataException or ConfigurationConflictException or ArgumentException or InvalidOperationException
                ? StorageOutcome.Rejected : StorageOutcome.Failed,e);
            throw;
        }
    }
    public static string Format(StorageTarget target, IEnumerable<string> settings, string operation,
        StorageOutcome outcome, Exception? error = null)
    {
        bool Allowed(string key) => target switch
        {
            StorageTarget.PortableJson => LegionConfigurationService.PortableKeys.Contains(key) || key == "Workflow",
            StorageTarget.MachineJson => LegionConfigurationService.MachineKeys.Contains(key),
            StorageTarget.Registry => RegistryNames.Contains(key) || LegionCommanderRegistrySchema.Definitions.Any(d => key == d.Section + "." + d.Name) || LegionCommanderFeatureSettings.IsSetting(key),
            _ => false
        };
        return JsonSerializer.Serialize(new
        {
            TimestampUtc = DateTimeOffset.UtcNow, OperationId = Guid.TryParseExact(operation,"N",out var id) ? id.ToString("N") : "invalid",
            Target = target.ToString(), Settings = settings.Where(Allowed).Distinct(StringComparer.Ordinal).Order().ToArray(),
            Outcome = outcome.ToString(), ErrorCategory = error == null ? null : Category(error),
            ValidationScope = "StorageOnly", EngineApplication = "Unverified"
        });
    }
    private static string Category(Exception e) => e switch
    {
        ConfigurationConflictException => "ConfigurationConflict", InvalidDataException => "InvalidData",
        UnauthorizedAccessException => "AccessDenied", System.Security.SecurityException => "Security",
        IOException => "IO", ArgumentException => "Argument", InvalidOperationException => "InvalidOperation", _ => "Other"
    };
}
