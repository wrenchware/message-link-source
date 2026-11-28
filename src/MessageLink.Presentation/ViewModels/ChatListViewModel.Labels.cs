using MessageLink.Core.Messaging;
using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

// Selected labels match any of the chosen labels on the displayed row, ANDed with the service and kind filters.
public sealed partial class ChatListViewModel
{
    private IReadOnlyList<string> labelFilters = Array.Empty<string>();
    private int labelsSeen = -1;

    /// <summary>Replaces the selections with one label; null clears all label filters. Multiple choices have no single id.</summary>
    public string? LabelFilter
    {
        get => labelFilters.Count == 1 ? labelFilters[0] : null;
        set => SetLabelFilters(value is null ? [] : [value]);
    }
    public IReadOnlyList<string> LabelFilters => labelFilters;
    public void SetLabelFilters(IEnumerable<string> choices)
    {
        var next = choices.Where(id => labels?.Find(id) is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (labelFilters.SequenceEqual(next)) return;
        labelFilters = Array.AsReadOnly(next);
        NotifyLabelFilters(); Rebuild();
    }
    public void ToggleLabelFilter(string id) => SetLabelFilters(labelFilters.Contains(id) ? labelFilters.Where(value => value != id) : labelFilters.Append(id));
    private void NotifyLabelFilters()
    {
        OnPropertyChanged(nameof(LabelFilters)); OnPropertyChanged(nameof(LabelFilter)); OnPropertyChanged(nameof(HasLabelFilter));
        OnPropertyChanged(nameof(LabelFilterName)); OnPropertyChanged(nameof(FilterDescription));
    }
    public bool HasLabelFilter => labelFilters.Count > 0;
    public string? LabelFilterName => HasLabelFilter ? string.Join(", ", labelFilters.Select(id => labels?.Find(id)?.Name)) : null;
    /// <summary>Whether any label exists: the filter button is offered for them even with a single service.</summary>
    public bool HasLabels => labels?.Count > 0;

    /// <summary>Shows a change to the labels: a row's pills, a filter on a label that is gone, and the list itself.</summary>
    public void ApplyLabels() => Rebuild();

    // Run at the start of every rebuild, so whatever changed the labels — a menu, a dialog, loading another profile —
    // the list never filters by a label that no longer exists.
    private void SyncLabels()
    {
        if (labels is null || labels.Version == labelsSeen) return;
        labelsSeen = labels.Version;
        var remaining = labelFilters.Where(id => labels.Find(id) is not null).ToArray();
        if (remaining.Length != labelFilters.Count)
        {
            labelFilters = Array.AsReadOnly(remaining);
            NotifyLabelFilters();
        }
        OnPropertyChanged(nameof(HasLabels)); OnPropertyChanged(nameof(LabelFilterName)); OnPropertyChanged(nameof(FilterDescription));
    }

    private IReadOnlyList<Label> LabelsOf(ChatSummary chat) => labels?.For(chat.Id) ?? [];
    private bool MatchesLabel(ChatSummary chat) => !HasLabelFilter || labelFilters.Any(id => labels?.Has(chat.Id, id) == true);
}
