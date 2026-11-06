using CHC.App.Controls;
using CHC.App.Services;
using CHC.Core.Collection;
using CHC.Core.Demo;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Past;
using CHC.Core.Performance;
using CHC.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CHC.App.ViewModels;

/// <summary>One program in the selected hour.</summary>
public sealed record LookBackRow(string Name, string Front, string Running, string Processor, string Disk, string Source);

/// <summary>A number at the top of the page.</summary>
public sealed record LookBackTile(string Label, string Value, string Detail);

/// <summary>
/// The "Look back" page: when the PC was on and asleep from Windows' log, and from Windows' usage records what ran at each hour,
/// also from before CHC was installed and while it was closed.
/// </summary>
public sealed partial class LookBackViewModel : ObservableObject
{
    /// <summary>What the page shows; Windows' usage records go back about this far.</summary>
    public const int Days = 60;

    /// <summary>In demo mode: as if CHC had been installed this many days ago, to show the difference.</summary>
    private const int DemoRecordedDays = 35;

    private readonly MainViewModel _main;
    private IReadOnlyList<PastBlock> _blocks = [];
    private int _run;

    public LookBackViewModel(MainViewModel main)
    {
        _main = main;
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    [ObservableProperty]
    private ActivityMap? _map;

    [ObservableProperty]
    private IReadOnlyList<LookBackTile> _tiles = [];

    /// <summary>The hours on per day: what CHC recorded itself, and what only Windows' log knows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOnChart))]
    private IReadOnlyList<ChartSeries> _onSeries = [];

    public bool HasOnChart => OnSeries.Count > 0;
    public TimeSpan ChartSlot { get; } = TimeSpan.FromDays(1);
    public Func<double, string> HoursFormat { get; } = v => Loc.T("lookback.perday.value", v);

    [ObservableProperty]
    private DateTime _chartFrom;

    [ObservableProperty]
    private DateTime _chartTo;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRead))]
    private bool _isReading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRead))]
    private bool _hasRecords;

    [ObservableProperty]
    private int _selectedDay = -1;

    [ObservableProperty]
    private int _selectedHour = -1;

    [ObservableProperty]
    private string _selectionTitle = "";

    [ObservableProperty]
    private IReadOnlyList<LookBackRow> _rows = [];

    [ObservableProperty]
    private bool _noRows;

    public bool CanRead => !IsReading && !_main.IsDemo;

    public bool ShowRead => !_main.IsDemo;

    public bool AutoRead
    {
        get => _main.ReadRecordsAutomatically;
        set
        {
            _main.ReadRecordsAutomatically = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The user asked for this in the welcome guide.</summary>
    public void ReadFromGuide()
    {
        OnPropertyChanged(nameof(AutoRead));
        if (ReadCommand.CanExecute(null)) ReadCommand.Execute(null);
    }

    public async void Refresh()
    {
        var run = ++_run;
        var history = _main.History;
        var demo = _main.IsDemo;
        var known = _main.Snapshot?.Gaming.KnownGames ?? [];
        var now = DateTime.Now;
        var first = now.Date.AddDays(-(Days - 1));
        IReadOnlyList<PastBlock> blocks = [];
        IReadOnlyList<PowerSpan> power = [];
        DateTime? oldest = null;
        IReadOnlyList<DateTime> recordedHours = [];
        IReadOnlyList<PastDay> recordedDays = [];
        await Task.Run(() =>
        {
            try
            {
                if (history is not null)
                {
                    blocks = history.PastBlocks(first, now.AddMinutes(1));
                    oldest = history.PastOldest();
                    var recordedFrom = demo ? now.Date.AddDays(-DemoRecordedDays) : first;
                    recordedHours = history.RecordedHours(recordedFrom, now.AddMinutes(1));
                    recordedDays = history.RecordedMinutesDaily(recordedFrom, now.AddMinutes(1));
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* a locked history: the page still shows what the log says */ }
            power = demo ? DemoData.PowerSpans(Days) : PastCollector.PowerSpans(Days);
        });
        if (run != _run) return;

        _blocks = blocks;
        HasRecords = blocks.Count > 0;
        var games = PastAnalyzer.Games(blocks, known);
        Map = BuildMap(first, power, games, recordedHours);
        ChartFrom = first;
        ChartTo = now.Date.AddDays(1);
        OnSeries = BuildOnSeries(power, recordedDays, first);
        Tiles = BuildTiles(power, games, first, now);
        Status = !HasRecords
            ? Loc.T("lookback.status.none")
            : _main.IsDemo
                ? Loc.T("lookback.status.demo")
                : Loc.T("lookback.status.read", Fmt.DateAndTime(_main.RecordsReadAt), Fmt.Date(oldest));
        if (SelectedDay >= 0) Select(SelectedDay, SelectedHour);
    }

    [RelayCommand]
    private async Task ReadAsync()
    {
        if (_main.History is not { } history || IsReading) return;
        IsReading = true;
        Status = Loc.T("lookback.status.reading");
        var outcome = await RecordsService.ReadAsync(history, _main.IsElevated);
        IsReading = false;
        switch (outcome.Result)
        {
            case RecordsResult.Done:
                _main.RecordsWereRead();
                Refresh();
                break;
            case RecordsResult.Declined:
                Status = Loc.T("lookback.status.declined");
                break;
            default:
                Status = Loc.T("lookback.status.failed", outcome.Error ?? "");
                break;
        }
    }

    public void Select(int day, int hour)
    {
        if (Map is not { } map || day >= map.Days) return;
        SelectedDay = day;
        SelectedHour = hour;
        var from = map.FirstDay.AddDays(day).AddHours(hour);
        SelectionTitle = Loc.T("lookback.selection", from.ToString("D", Loc.Instance.Culture), from.ToString("t", Loc.Instance.Culture), from.AddHours(1).ToString("t", Loc.Instance.Culture));
        var to = from.AddHours(1);

        // CHC's own numbers where it was recording; Windows' records for everything else.
        IReadOnlyList<ProgramUsage> own = [];
        try { own = _main.History?.Programs(from, to) ?? []; }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* the records still say something */ }
        var ownRows = own.Where(ProgramAdvisor.IsOwnProgram).Select(p => new PastProgramRow(p.Name, p.FrontSeconds, p.Seconds, p.CpuAverage * p.Seconds / 3600, p.ReadBytes + p.WriteBytes, 0)).ToList();
        var seen = ownRows.Select(r => r.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chc = Loc.T("source.chc.short");
        var nonChc = Loc.T("source.nonchc");
        Rows = ownRows.Select(r => (Row: r, Source: chc))
            .Concat(PastAnalyzer.Programs(_blocks, from, to).Where(r => !seen.Contains(r.Exe)).Select(r => (Row: r, Source: nonChc)))
            .OrderByDescending(x => x.Row.Focus).ThenByDescending(x => x.Row.CpuPercent ?? 0)
            .Take(12)
            .Select(x => new LookBackRow(
                PastAnalyzer.NameOf(x.Row.Exe),
                x.Row.Focus >= 30 ? Fmt.Duration(TimeSpan.FromSeconds(x.Row.Focus)) : Fmt.Dash,
                Fmt.Duration(TimeSpan.FromSeconds(x.Row.Running)),
                x.Row.CpuPercent is { } cpu ? Fmt.Percent(cpu, cpu < 10 ? 1 : 0) : Fmt.Dash,
                x.Row.DiskBytes >= 1024 * 1024 ? Fmt.Bytes((ulong)x.Row.DiskBytes) : Fmt.Dash,
                x.Source))
            .ToList();
        NoRows = Rows.Count == 0;
    }

    private static List<ChartSeries> BuildOnSeries(IReadOnlyList<PowerSpan> power, IReadOnlyList<PastDay> recorded, DateTime first)
    {
        static List<HistoryPoint> Hours(IEnumerable<PastDay> days) => days.Select(d => new HistoryPoint(d.Date, d.Minutes / 60, null, null)).ToList();
        var nonChc = PastAnalyzer.BeyondMeasured(PastAnalyzer.MinutesOn(power, first, Days), recorded);
        var series = new List<ChartSeries>();
        if (recorded.Count > 0) series.Add(new(Loc.T("source.chc.recorded"), Palette.BrandColor, Hours(recorded)));
        if (nonChc.Count > 0) series.Add(new(Loc.T("source.nonchc"), HistoryViewModel.PcColor, Hours(nonChc), Dashed: true));
        return series.Sum(s => s.Points.Count) >= 2 ? series : [];
    }

    private static ActivityMap BuildMap(DateTime first, IReadOnlyList<PowerSpan> power, IReadOnlyList<PastGameUse> games, IReadOnlyList<DateTime> recordedHours)
    {
        var map = new ActivityMap(first, Days);
        foreach (var hour in recordedHours)
        {
            var day = (hour.Date - first).Days;
            if (day >= 0 && day < Days)
            {
                map.SetRecorded(day, hour.Hour);
                map[day, hour.Hour] = ActivityState.On;
            }
        }
        foreach (var span in power) Paint(map, first, span.Start, span.End, span.State == PowerState.On ? ActivityState.On : ActivityState.Asleep);
        foreach (var game in games)
            foreach (var (start, end) in game.Stretches)
                Paint(map, first, start, end, ActivityState.Game);
        return map;
    }

    /// <summary>The hours a stretch of time touches. What the PC did last wins, which is why games come after being on.</summary>
    private static void Paint(ActivityMap map, DateTime first, DateTime start, DateTime end, ActivityState state)
    {
        for (var hour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0); hour < end; hour = hour.AddHours(1))
        {
            var day = (hour.Date - first).Days;
            if (day < 0 || day >= map.Days) continue;
            // Being on beats being asleep within one hour, and a game beats both.
            if (state > map[day, hour.Hour]) map[day, hour.Hour] = state;
        }
    }

    private static List<LookBackTile> BuildTiles(IReadOnlyList<PowerSpan> power, IReadOnlyList<PastGameUse> games, DateTime first, DateTime now)
    {
        var onSpans = power.Where(p => p.State == PowerState.On && p.End > first).ToList();
        var hours = onSpans.Sum(p => ((p.End < now ? p.End : now) - (p.Start > first ? p.Start : first)).TotalHours);
        var days = onSpans.SelectMany(p => Enumerable.Range(0, (p.End.Date - p.Start.Date).Days + 1).Select(i => p.Start.Date.AddDays(i))).Where(d => d >= first).Distinct().Count();
        var played = games.Sum(g => g.FocusSeconds) / 3600;
        var tiles = new List<LookBackTile>
        {
            new(Loc.T("lookback.tile.on"), Fmt.Duration(TimeSpan.FromHours(hours)), Loc.T("lookback.tile.on.detail", days, Days)),
            new(Loc.T("lookback.tile.perday"), days > 0 ? Fmt.Duration(TimeSpan.FromHours(hours / days)) : Fmt.Dash, Loc.T("lookback.tile.perday.detail")),
        };
        if (games.Count > 0)
        {
            tiles.Add(new(Loc.T("lookback.tile.games"), Fmt.Duration(TimeSpan.FromHours(played)), Loc.T("lookback.tile.games.detail", games.Count)));
            tiles.Add(new(Loc.T("lookback.tile.top"), PastAnalyzer.TitleOf(games[0].Exe, games[0].Path), Fmt.Duration(TimeSpan.FromSeconds(games[0].FocusSeconds))));
        }
        return tiles;
    }
}
