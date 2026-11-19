using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
namespace Hexor;
public class Observable : INotifyPropertyChanged
{
 public event PropertyChangedEventHandler? PropertyChanged;
 protected bool Set<T>(ref T field,T value,[CallerMemberName]string? name=null) { if(EqualityComparer<T>.Default.Equals(field,value))return false;field=value;Changed(name);return true; }
 protected void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
}
public sealed class RelayCommand(Action execute,Func<bool>? can=null) : ICommand
{ public event EventHandler? CanExecuteChanged { add { CommandManager.RequerySuggested+=value; } remove { CommandManager.RequerySuggested-=value; } } public bool CanExecute(object? p)=>can?.Invoke()??true;public void Execute(object? p)=>execute(); }
public enum InstructionType { Gathering, KinahFarming, AutoQuest, AutoKraoDungeon, SwitchCharacter }
public sealed class Instruction : Observable
{
 private bool enabled=true; private InstructionType type;
 public bool Enabled {get=>enabled;set=>Set(ref enabled,value);}
 public InstructionType Type { get => type; set { if (Set(ref type, value)) Changed(nameof(ActivityLabel)); } }
 [JsonIgnore]public string ActivityLabel => InstructionPresentation.Options.FirstOrDefault(option => option.Value == Type)?.Label ?? Type.ToString();
 private int sequenceNumber;
 [JsonIgnore]public int SequenceNumber {get=>sequenceNumber;set=>Set(ref sequenceNumber,value);}
 public int DurationMinutes {get;set;}=30;
 public string Id {get;set;}=Guid.NewGuid().ToString("N");
 public int MinimumMinutes {get;set;}=180;
 public int MaximumMinutes {get;set;}=300;
 public bool SmartPatrol {get;set;}=true;
 public bool ResetTimer {get;set;}
 [JsonIgnore]public string MinimumText {get;set;}="180";
 [JsonIgnore]public string MaximumText {get;set;}="300";
 [JsonIgnore]public System.Text.Json.Nodes.JsonObject? CompatibleFields {get;set;}
 [JsonExtensionData]public Dictionary<string,JsonElement>? Extra {get;set;}
 public Instruction Copy()=>new(){Enabled=Enabled,Type=Type,DurationMinutes=DurationMinutes,MinimumMinutes=MinimumMinutes,MaximumMinutes=MaximumMinutes,MinimumText=MinimumText,MaximumText=MaximumText,SmartPatrol=SmartPatrol,ResetTimer=ResetTimer,CompatibleFields=(System.Text.Json.Nodes.JsonObject?)CompatibleFields?.DeepClone(),Extra=Extra?.ToDictionary(p=>p.Key,p=>p.Value.Clone())};
}
public sealed class Preferences
{
 public int SchemaVersion {get;set;}=1;
 public string UiLanguage {get;set;}="English";
 public string GameLanguage {get;set;}="English";
 public string? ConfigurationFolder {get;set;}
 public List<ManualCharacterProfile> ManualProfiles {get;set;}=[];
 public string? SelectedManualProfileId {get;set;}
 public ObservableCollection<Instruction> Instructions {get;set;}=[new(){Type=InstructionType.Gathering}];
 public int GatherMin {get;set;}=20; public int GatherMax {get;set;}=40;
 public bool ResetTimer {get;set;} public bool PrepareCombat {get;set;}=true;
 public string AfterFinish {get;set;}="Do nothing";
 public bool ScheduleEnabled {get;set;} public int MaxAccounts {get;set;}=1;
 public int WorkMin {get;set;}=60;public int WorkMax {get;set;}=120;
 public int RestMin {get;set;}=10;public int RestMax {get;set;}=20;
 public bool CaptchaNotifications {get;set;}=true;
 [JsonExtensionData]public Dictionary<string,JsonElement>? Extra {get;set;}
}
public record Detection(string Character,string Server,string Client,string Process,string? Installation)
{
 public int? ProcessId {get;init;}
 public string? ExecutablePath {get;init;}
 public string? Launcher {get;init;}
 public IReadOnlyList<Runtime.GameProcessObservation> Processes {get;init;}=[];
 public DateTimeOffset? ObservedAt {get;init;}
 public string CharacterSource {get;init;}="Unavailable: no authorized live source.";
 public string ServerSource {get;init;}="Unavailable: no authorized live source.";
 public bool RuntimeIdentityAvailable {get;init;}
 public string IdentityStatus {get;init;}="Unavailable: no authorized live identity source configured.";
 public DateTimeOffset? LastSuccessfulIdentityUpdate {get;init;}
 public IReadOnlyList<string> Errors {get;init;}=[];
}
public record ConfigurationCandidate(string Folder,string Evidence);
public sealed record ManualCharacterProfile(string Id,string Character,string Server)
{ public override string ToString()=>Character+" • "+Server+" (manual)"; }
public sealed record MachinePathProposal(int ProcessId,string ExecutablePath,DateTimeOffset ProcessStartedAt,
 string Key,Compatibility.ConfigurationSnapshot Expected);
