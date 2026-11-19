using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Hexor.Compatibility;

namespace Hexor;

public sealed class GatheringResourceViewModel : Observable
{
    private bool? enabled;
    private readonly Action<int, bool> save;
    public int LineIndex { get; }
    public string Identifier { get; }
    public string DisplayName { get; }
    public int Ordinal { get; internal set; }
    private readonly bool canEdit;
    public bool IsEditable => canEdit && enabled.HasValue;
    public bool? Enabled
    {
        get => enabled;
        set
        {
            if (!IsEditable || value == null || enabled == value) return;
            // The owner reloads saved state after both success and failure.
            save(LineIndex, value.Value);
        }
    }
    public GatheringResourceViewModel(GatheringTarget target, bool canEdit, Action<int, bool> save)
    {
        LineIndex = target.LineIndex; Identifier = target.Identifier; DisplayName = target.DisplayName;
        enabled = target.Enabled; this.canEdit = canEdit; this.save = save;
    }
    internal void ReloadValue(bool? value) { enabled = value; Changed(nameof(Enabled)); Changed(nameof(IsEditable)); }
}

public sealed class GatheringSettingsViewModel(IRegistrySettingsService registry, Action<string> report) : Observable
{
    private GatheringRegistrySnapshot? snapshot;
    private RegistryEntry? quality;
    private string qualityLevel = "";
    private string summary = "";
    private string qualityDetail = "";
    private bool canEditQuality;
    public ObservableCollection<GatheringResourceViewModel> Resources { get; } = [];
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public string QualityLevel { get => qualityLevel; set => Set(ref qualityLevel, value); }
    public string QualityDetail { get => qualityDetail; private set => Set(ref qualityDetail, value); }
    public bool CanEditQuality { get => canEditQuality; private set => Set(ref canEditQuality, value); }

    public void Reload() => ReloadCore(includeQuality: true);

    public void RestoreDefaults()
    {
        try
        {
            if (snapshot == null) throw new InvalidDataException("Refresh the resource list before restoring.");
            registry.WriteGatheringTargetsVerified(snapshot, GatheringDefaults.TargetsText());
            report("Vendor default gather list restored and verified.");
        }
        catch (ConfigurationConflictException) { report("Gathering settings changed externally. The list has been refreshed; try again."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or NotSupportedException)
        { report("Could not restore the default gather list (" + e.GetType().Name + "). Refresh and try again."); }
        finally { ReloadCore(includeQuality: false); }
    }

    private void ReloadCore(bool includeQuality)
    {
        snapshot = null;
        try
        {
            snapshot = registry.ReadGathering();
            if (snapshot.CanEditDefaults)
            {
                // Vendor parity: the installed build falls back to its built-in default selection
                // when GatherTargets was never saved. Show it editable; the first save persists it.
                var defaults = new GatheringDocument(snapshot.Names.Value!, GatheringDefaults.TargetsText());
                Resources.Clear();
                foreach (var target in defaults.Targets)
                {
                    var row = new GatheringResourceViewModel(target, true, SaveSelection);
                    row.Ordinal = Resources.Count + 1;
                    Resources.Add(row);
                }
                Summary = $"Vendor default resource list (not saved yet) · {defaults.Targets.Count(x => x.Enabled == true)} selected";
                return;
            }
            if (!snapshot.CanEdit)
            {
                Resources.Clear();
                Summary = snapshot.Names.Exists || snapshot.Targets.Exists
                    ? "The saved resource lists are incomplete or unsupported. They have been preserved."
                    : "No resource list is saved yet. Configure gathering in Legion Commander, then refresh.";
            }
            else
            {
                var document = new GatheringDocument(snapshot.Names.Value!, snapshot.Targets.Value!);
                // Keep row instances for ordinary edits so focus and scroll position survive.
                if (Resources.Count == document.Targets.Count && Resources.Zip(document.Targets).All(pair =>
                    pair.First.LineIndex == pair.Second.LineIndex && pair.First.Identifier == pair.Second.Identifier && pair.First.DisplayName == pair.Second.DisplayName))
                    for (var index = 0; index < Resources.Count; index++) Resources[index].ReloadValue(document.Targets[index].Enabled);
                else
                {
                    Resources.Clear();
                    foreach (var target in document.Targets)
                    {
                        var row = new GatheringResourceViewModel(target, true, SaveSelection);
                        row.Ordinal = Resources.Count + 1;
                        Resources.Add(row);
                    }
                }
                Summary = $"{Resources.Count} saved resources · {document.Targets.Count(x => x.Enabled == true)} selected";
                if (document.UnrecognizedLines > 0) Summary += $" · {document.UnrecognizedLines} unsupported entries preserved";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or NotSupportedException)
        { Resources.Clear(); Summary = "Unable to read resource selections. Refresh to try again."; }

        if (!includeQuality) return;
        try
        {
            quality = registry.Inspect("ResourceGrab", "GatherQualityFilterLevel");
            CanEditQuality = quality.Supported;
            QualityLevel = quality.Value is int value ? unchecked((uint)value).ToString(CultureInfo.InvariantCulture) : "";
            QualityDetail = !quality.Supported ? "Unsupported saved value preserved."
                : !quality.Exists ? "No quality level saved. Select a quality to set it."
                : LegionCommanderFeatureSettings.Qualities.Any(option => option.Value == QualityLevel)
                    ? "Saved quality level."
                    : $"Saved custom quality value {QualityLevel}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or NotSupportedException)
        { quality = null; CanEditQuality = false; QualityLevel = ""; QualityDetail = "Unable to read the saved quality level."; }
    }

    private void SaveSelection(int lineIndex, bool enabled)
    {
        try
        {
            if (snapshot == null) throw new InvalidDataException("Refresh the resource list before saving.");
            registry.WriteGatheringSelectionVerified(snapshot, lineIndex, enabled);
            report("Saved and verified: gathering resource selection.");
        }
        catch (ConfigurationConflictException) { report("Gathering settings changed externally. The list has been refreshed; select the resource again."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or NotSupportedException)
        { report("Could not save gathering selection (" + e.GetType().Name + "). Refresh and try again."); }
        finally { ReloadCore(includeQuality: false); }
    }

    public void SaveQualityLevel()
    {
        if (!CanEditQuality || !uint.TryParse(QualityLevel, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        { report("Enter a numeric quality level from 0 to 4294967295. No registry changes were made."); return; }
        try
        {
            if (registry.Inspect("ResourceGrab", "GatherQualityFilterLevel") != quality) throw new ConfigurationConflictException();
            registry.WriteVerified("ResourceGrab", "GatherQualityFilterLevel", unchecked((int)value));
            report("Saved and verified: gathering quality level.");
        }
        catch (ConfigurationConflictException) { report("Quality level changed externally. The value has been refreshed; enter it again."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or NotSupportedException)
        { report("Could not save gathering quality level (" + e.GetType().Name + ")."); }
        finally { Reload(); }
    }
}
