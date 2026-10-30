using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TrelloExtension.Pages;

/// <summary>
/// Filter dropdown for CardsPage: All | Completed | Incomplete | Overdue.
/// 
/// The SelectedFilterIdChanged callback lets CardsPage apply the filter in
/// GetItems() and RaiseItemsChanged(-2) - display-only, never mutating _cards
/// or pos, so card-order editing (move up/down) keeps operating on the true
/// board order even while a filter is active.
/// </summary>
internal sealed partial class CardFilters : Filters
{
    public const string AllId = "all";
    public const string CompletedId = "completed";
    public const string IncompleteId = "incomplete";
    public const string OverdueId = "overdue";

    private readonly Action _onFilterChanged;

    public CardFilters(Action onFilterChanged)
    {
        _onFilterChanged = onFilterChanged;

        // React to filter selection changes by subscribing to our OWN public
        // PropChanged event (part of the INotifyPropChanged IDL contract). This
        // is used instead of overriding OnPropertyChanged because the Toolkit's
        // BaseObservable.OnPropertyChanged is protected but NOT virtual in the
        // compiled NuGet package (verified via compile error CS0506; the
        // main-branch source shows the same non-virtual signature).
        PropChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CurrentFilterId))
            {
                _onFilterChanged();
            }
        };

        // Default to the first entry - mirrors what the host dropdown shows as
        // initially selected (verified: FiltersViewModel presents GetFilters()
        // in order; the first item is the presented default).
        // Note: assignment below raises PropChanged too, but the subscription
        // above is installed first, so the callback fires once during
        // construction - harmless (CardsPage treats it as a no-op refresh).
        CurrentFilterId = AllId;
    }

    /// <summary>
    /// Display name of the currently selected filter - for user-facing text
    /// (e.g. the "No cards match the 'X' filter" empty state). Falls back to
    /// the raw id for unknown values so the message never breaks.
    /// </summary>
    public string CurrentFilterName =>
        CurrentFilterId switch
        {
            AllId => "All",
            CompletedId => "Completed",
            IncompleteId => "Incomplete",
            OverdueId => "Overdue",
            _ => CurrentFilterId,
        };

    /// <summary>
    /// The filter entries shown in the host's FiltersDropDown.
    /// </summary>
    public override IFilterItem[] GetFilters() =>
    [
        new Filter { Id = AllId, Name = "All", Icon = new IconInfo("\uE8FD") }, // BulletedList
        new Filter { Id = CompletedId, Name = "Completed", Icon = new IconInfo("\uE73A") }, // CheckboxComposite
        new Filter { Id = IncompleteId, Name = "Incomplete", Icon = new IconInfo("\uE739") }, // Checkbox
        new Filter { Id = OverdueId, Name = "Overdue", Icon = new IconInfo("\uE814") }, // Important
    ];

}
