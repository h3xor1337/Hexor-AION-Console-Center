using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hexor.Compatibility;

// Independently implemented from HEXOR_VERIFIED_SETTINGS_MAPPING.md.
// These names are the on-disk contract, not Hexor's UI enum ordinals.
public enum WorkflowAction { Gather, FloorGold, SwitchCharacter, AutoTask, KraoDungeon }
public enum CompletionAction { GatherOnCharacter, NextAccount }

public sealed record WorkflowStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public WorkflowAction Action { get; init; }
    public bool Enabled { get; init; } = true;
    public int MinimumMinutes { get; init; } = 180;
    public int MaximumMinutes { get; init; } = 300;
    public bool SmartPatrol { get; init; } = true;
    public bool ResetTimer { get; init; }
    public JsonObject Preserved { get; init; } = new();
}

public sealed record WorkflowPlan
{
    public string Revision { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];
    public CompletionAction Finish { get; init; }
    public JsonObject Preserved { get; init; } = new();

    public WorkflowPlan Normalize()
    {
        if (string.IsNullOrWhiteSpace(Revision) || Steps.Count > 100 || !Enum.IsDefined(Finish)
            || Steps.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || !Enum.IsDefined(s.Action))
            || Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Steps.Count)
            throw new InvalidDataException("Workflow must have a revision, unique step IDs, and at most 100 supported steps.");
        foreach (var step in Steps.Where(s => s.Action == WorkflowAction.Gather))
            if (step.MinimumMinutes < 1 || step.MaximumMinutes < step.MinimumMinutes || step.MaximumMinutes > 10080)
                throw new InvalidDataException("Gathering ranges must satisfy 1 ≤ minimum ≤ maximum ≤ 10080 minutes.");

        if (Steps.Count == 0) return this;
        var finalSwitch = (Steps.FirstOrDefault(s => s.Action == WorkflowAction.SwitchCharacter)
            ?? new WorkflowStep { Action = WorkflowAction.SwitchCharacter }) with { Enabled = true };
        var ordered = Steps.Where(s => s.Action != WorkflowAction.SwitchCharacter).Append(finalSwitch).ToArray();
        if (ordered.Length > 100 || ordered.Where(s => s.Enabled).All(s => s.Action == WorkflowAction.SwitchCharacter))
            throw new InvalidDataException("A workflow needs an enabled activity and room for its final Switch Character step.");
        return this with { Steps = ordered };
    }

    public static WorkflowPlan Default() => new()
    {
        Steps = new[] { WorkflowAction.AutoTask, WorkflowAction.KraoDungeon, WorkflowAction.FloorGold, WorkflowAction.SwitchCharacter }
            .Select(a => new WorkflowStep { Action = a }).ToArray()
    };

    public static WorkflowPlan Read(JsonObject node)
    {
        var revision = node["Revision"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(revision)) throw new InvalidDataException("Workflow revision is missing.");
        if (node.ContainsKey("Steps") && node["Steps"] is not JsonArray) throw new InvalidDataException("Invalid workflow steps.");
        var steps = (node["Steps"] as JsonArray ?? []).Select(n =>
        {
            if (n is not JsonObject s || string.IsNullOrWhiteSpace(s["Id"]?.GetValue<string>()))
                throw new InvalidDataException("A step ID is missing.");
            foreach (var key in new[] { "Kind", "Enabled", "MinMinutes", "MaxMinutes", "SmartPatrol", "ResetTimer" })
                if (s.ContainsKey(key) && s[key] == null) throw new InvalidDataException("Step fields cannot be null.");
            return new WorkflowStep
            {
                Id = s["Id"]!.GetValue<string>(), Action = ReadEnum(s["Kind"], WorkflowAction.Gather),
                Enabled = s["Enabled"]?.GetValue<bool>() ?? true,
                MinimumMinutes = s["MinMinutes"]?.GetValue<int>() ?? 180,
                MaximumMinutes = s["MaxMinutes"]?.GetValue<int>() ?? 300,
                SmartPatrol = s["SmartPatrol"]?.GetValue<bool>() ?? true,
                ResetTimer = s["ResetTimer"]?.GetValue<bool>() ?? false,
                Preserved = (JsonObject)s.DeepClone()
            };
        }).ToArray();
        // Reading never normalizes or modifies the file. Normalize is applied to a working copy.
        if (node.ContainsKey("Finish") && node["Finish"] == null) throw new InvalidDataException("Completion kind cannot be null.");
        var result = new WorkflowPlan { Revision = revision, Steps = steps,
            Finish = ReadEnum(node["Finish"], CompletionAction.GatherOnCharacter), Preserved = (JsonObject)node.DeepClone() };
        _ = result.Normalize();
        return result;
    }

    private static T ReadEnum<T>(JsonNode? node, T fallback) where T : struct, Enum
    {
        if (node == null) return fallback;
        // The documented converter permits defined numeric enum inputs as well as names.
        var value = JsonSerializer.Deserialize<T>(node.ToJsonString(), new JsonSerializerOptions
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        return Enum.IsDefined(value) ? value : throw new InvalidDataException("Unsupported workflow enum value.");
    }

    public JsonObject ToJson()
    {
        var plan = Normalize();
        var json = (JsonObject)Preserved.DeepClone();
        json["Revision"] = plan.Revision;
        json["Finish"] = plan.Finish.ToString();
        var steps = new JsonArray();
        foreach (var step in plan.Steps)
        {
            var s = (JsonObject)step.Preserved.DeepClone();
            s["Id"] = step.Id; s["Kind"] = step.Action.ToString(); s["Enabled"] = step.Enabled;
            s["MinMinutes"] = step.MinimumMinutes; s["MaxMinutes"] = step.MaximumMinutes;
            s["SmartPatrol"] = step.SmartPatrol; s["ResetTimer"] = step.ResetTimer;
            steps.Add(s);
        }
        json["Steps"] = steps;
        return json;
    }
}
