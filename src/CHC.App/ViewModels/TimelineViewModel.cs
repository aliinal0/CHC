using System.Collections.ObjectModel;
using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;
using CHC.Core.Timeline;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace CHC.App.ViewModels;

/// <summary>Which entries the timeline shows.</summary>
public enum TimelineFilter
{
    All,
    DriversAndUpdates,
    Programs,
    HardwareAndSettings,
    Problems,
}

/// <summary>One line on the timeline: a change, or a freeze or crash.</summary>
public sealed record TimelineItem(SymbolRegular Icon, string Title, string Detail, string Time, bool IsProblem, string? Note)
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasNote => Note is { Length: > 0 };
}

/// <summary>A day of the timeline, newest first.</summary>
public sealed record TimelineDay(string Title, IReadOnlyList<TimelineItem> Items);

/// <summary>
/// The "Changes" page: drivers, updates, programs, BIOS, parts and settings as they changed, with the freezes and
/// crashes in between, so a problem that began after a change stands out.
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    private const int PeriodDays = 180;
    private readonly MainViewModel _main;

    public TimelineViewModel(MainViewModel main)
    {
        _main = main;
        BuildFilters();
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            BuildFilters();
            Refresh();
        };
    }

    public ObservableCollection<Option<TimelineFilter>> Filters { get; } = [];

    [ObservableProperty]
    private Option<TimelineFilter>? _selectedFilter;

    [ObservableProperty]
    private IReadOnlyList<TimelineDay> _days = [];

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>The change the problems most likely started with (also a finding on the Stability page).</summary>
    [ObservableProperty]
    private string? _suspect;

    private bool _building;

    partial void OnSelectedFilterChanged(Option<TimelineFilter>? value)
    {
        if (!_building) Refresh();
    }

    public void Refresh()
    {
        var filter = SelectedFilter?.Value ?? TimelineFilter.All;
        var now = DateTime.Now;
        var changes = _main.History?.Changes(now.AddDays(-PeriodDays), now.AddDays(1)) ?? [];
        var incidents = _main.Snapshot?.Stability.Incidents ?? [];

        var suspect = ChangeSuspects.Find(changes, incidents);
        Suspect = suspect is { } s ? Loc.T("stability.afterchange.title", ChangeTexts.Title(s.Change)) : null;

        var entries = new List<(DateTime Time, TimelineItem Item)>();
        foreach (var c in changes.Where(c => Matches(c, filter)))
        {
            string? note = null;
            if (suspect is { } hit && hit.Change.Key == c.Key) note = Loc.T("timeline.suspect", hit.After);
            entries.Add((c.Time, new TimelineItem(Icon(c), ChangeTexts.Title(c), ChangeTexts.Detail(c), When(c), false, note)));
        }
        if (filter is TimelineFilter.All or TimelineFilter.Problems)
            foreach (var i in incidents)
                entries.Add((i.Time, new TimelineItem(SymbolRegular.ErrorCircle24, SectionBuilder.IncidentText(i),
                    string.Join(" · ", new[] { Loc.T("timeline.cause", Loc.T("cause." + i.Cause)), SectionBuilder.MomentText(i.Moment) }.Where(t => t.Length > 0)), Fmt.Clock(i.Time), true, null)));

        Days = entries
            .OrderByDescending(e => e.Time)
            .GroupBy(e => e.Time.Date)
            .Select(g => new TimelineDay(DayTitle(g.Key), g.Select(e => e.Item).ToList()))
            .ToList();
        IsEmpty = Days.Count == 0;
    }

    /// <summary>The newest changes (for the overview), without the noise of program installs.</summary>
    public IReadOnlyList<TimelineItem> Latest(int count)
    {
        var now = DateTime.Now;
        var changes = _main.History?.Changes(now.AddDays(-PeriodDays), now.AddDays(1), 50) ?? [];
        var suspect = ChangeSuspects.Find(changes, _main.Snapshot?.Stability.Incidents ?? []);
        return changes
            .Where(c => c.Kind is not (ChangeKind.AppInstalled or ChangeKind.AppUpdated or ChangeKind.AppRemoved))
            .Take(count)
            .Select(c => new TimelineItem(Icon(c), ChangeTexts.Title(c), ChangeTexts.Detail(c), RelativeDay(c.Time), false,
                suspect is { } hit && hit.Change.Key == c.Key ? Loc.T("timeline.suspect", hit.After) : null))
            .ToList();
    }

    /// <summary>The time Windows logged; nothing when only the day is known (a program's install date); "noticed at a scan" for what CHC found itself.</summary>
    private static string When(SystemChange c) =>
        c.Exact ? Fmt.Clock(c.Time) : c.Time.TimeOfDay == TimeSpan.Zero ? "" : Loc.T("timeline.noticed");

    private static string RelativeDay(DateTime time)
    {
        var days = (DateTime.Today - time.Date).Days;
        return days switch { 0 => Loc.T("timeline.today"), 1 => Loc.T("timeline.yesterday"), _ => Loc.T("timeline.daysago", days) };
    }

    private static bool Matches(SystemChange c, TimelineFilter filter) => filter switch
    {
        TimelineFilter.DriversAndUpdates => c.Kind is ChangeKind.Driver or ChangeKind.WindowsUpdate or ChangeKind.Windows or ChangeKind.Bios,
        TimelineFilter.Programs => c.Kind is ChangeKind.AppInstalled or ChangeKind.AppUpdated or ChangeKind.AppRemoved,
        TimelineFilter.HardwareAndSettings => c.Kind is ChangeKind.HardwareAdded or ChangeKind.HardwareRemoved or ChangeKind.HardwareChanged or ChangeKind.Setting or ChangeKind.Bios,
        TimelineFilter.Problems => false,
        _ => true,
    };

    private static SymbolRegular Icon(SystemChange c) => c.Kind switch
    {
        ChangeKind.WindowsUpdate => SymbolRegular.ArrowSync24,
        ChangeKind.Windows => SymbolRegular.WindowApps24,
        ChangeKind.Driver => SymbolRegular.PlugConnected24,
        ChangeKind.AppInstalled => SymbolRegular.AppsAddIn24,
        ChangeKind.AppUpdated => SymbolRegular.ArrowCircleUp24,
        ChangeKind.AppRemoved => SymbolRegular.Delete24,
        ChangeKind.Bios => SymbolRegular.DeveloperBoard24,
        ChangeKind.Setting => SymbolRegular.Settings24,
        _ => SymbolRegular.Toolbox24,
    };

    private static string DayTitle(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return Loc.T("timeline.today");
        if (day == today.AddDays(-1)) return Loc.T("timeline.yesterday");
        return day.ToString(day.Year == today.Year ? "dddd, d MMMM" : "D", Loc.Instance.Culture);
    }

    private void BuildFilters()
    {
        var selected = SelectedFilter?.Value ?? TimelineFilter.All;
        Filters.Clear();
        foreach (var f in Enum.GetValues<TimelineFilter>())
            Filters.Add(new(f, Loc.T("timeline.filter." + f.ToString().ToLowerInvariant())));
        _building = true;
        try { SelectedFilter = Filters.First(f => f.Value == selected); }
        finally { _building = false; }
    }
}
