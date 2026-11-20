namespace Hexor;

public sealed record InstructionOption(InstructionType Value,string Label) { public override string ToString() => Label; }
public sealed record CompletionOption(string Value,string Label) { public override string ToString() => Label; }
public static class InstructionPresentation
{
    public static IReadOnlyList<InstructionOption> Options { get; } = [
        new(InstructionType.Gathering,"Gathering"), new(InstructionType.KinahFarming,"Kinah Farming"),
        new(InstructionType.AutoQuest,"Auto Dungeon"), new(InstructionType.AutoKraoDungeon,"Auto Krao Dungeon"),
        new(InstructionType.SwitchCharacter,"Switch Character")];
    public static IReadOnlyList<CompletionOption> CompletionOptions { get; } = [
        new("GatherOnCharacter","Gather on current character"), new("NextAccount","Move to next account")];
}
