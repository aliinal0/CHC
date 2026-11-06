using System.Collections.ObjectModel;
using CHC.App.Controls;
using CHC.App.Services;
using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;
using CHC.Core.Reporting;
using CHC.Core.Sensors;
using CHC.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace CHC.App.ViewModels;

public sealed record SummaryRow(SymbolRegular Icon, string Label, string Value);

/// <summary>A program with a newer version.</summary>
public sealed record ProgramUpdateRow(string Name, string Versions);

/// <summary>Something CHC can't look up itself, and the maker's page for it.</summary>
public sealed record ManualCheck(string Title, string Detail, string ButtonText, IRelayCommand Open);

public sealed partial class DashboardViewModel(MainViewModel main) : ObservableObject
{
    private const int HistoryLength = 60;
    private readonly Dictionary<string, List<double>> _history = [];

    public MainViewModel Main { get; } = main;

    // ---- "Lately": the last week from the history, the newest changes, what heated the PC.

    [ObservableProperty]
    private bool _hasLately;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _weekTemperatures = [];

    [ObservableProperty]
    private DateTime _weekFrom;

    [ObservableProperty]
    private DateTime _weekTo;

    public TimeSpan WeekSlot { get; } = TimeSpan.FromHours(2);
    public Func<double, string> Celsius { get; } = v => Fmt.Temp(v);

    [ObservableProperty]
    private string _weekPeak = "";

    [ObservableProperty]
    private IReadOnlyList<TimelineItem> _recentChanges = [];

    [ObservableProperty]
    private IReadOnlyList<HistoryApp> _heatApps = [];

    /// <summary>At least an hour of the week is recorded: enough for the chart to say something.</summary>
    [ObservableProperty]
    private bool _hasWeek;

    [ObservableProperty]
    private bool _hasHeat;

    /// <summary>Reads the last week from the history (cheap: a few hundred rows).</summary>
    public void LoadLately(HistoryStore? history, TimelineViewModel timeline)
    {
        if (history is null)
        {
            HasLately = false;
            return;
        }
        WeekTo = DateTime.Today.AddDays(1);
        WeekFrom = WeekTo.AddDays(-7);
        WeekTemperatures =
        [
            new(Loc.T("history.series.cpu"), HistoryViewModel.CpuColor, history.Series(HistoryMetric.CpuTemp, WeekFrom, WeekTo, WeekSlot), ChartValue.Max),
            new(Loc.T("history.series.gpu"), HistoryViewModel.GpuColor, history.Series(HistoryMetric.GpuTemp, WeekFrom, WeekTo, WeekSlot), ChartValue.Max),
        ];
        var gpu = history.Peak(HistoryMetric.GpuTemp, WeekFrom, WeekTo);
        var cpu = history.Peak(HistoryMetric.CpuTemp, WeekFrom, WeekTo);
        var hottest = new[] { (gpu, "history.series.gpu"), (cpu, "history.series.cpu") }.OrderByDescending(p => p.Item1?.Value ?? double.MinValue).First();
        WeekPeak = hottest.Item1 is not { } peak ? ""
            : peak.App is { } app
                ? Loc.T("dashboard.lately.peak", Loc.T(hottest.Item2), Fmt.Temp(peak.Value), peak.Time.ToString("dddd", Loc.Instance.Culture), app)
                : Loc.T("dashboard.lately.peak.noapp", Loc.T(hottest.Item2), Fmt.Temp(peak.Value), peak.Time.ToString("dddd", Loc.Instance.Culture));
        RecentChanges = timeline.Latest(3);
        // The programs that ran the PC warmest on average (a peak can be the heat a game left behind),
        // among those used for at least half an hour.
        var apps = history.Apps(WeekFrom, WeekTo, 15)
            .Where(a => a.Minutes >= 30 && (a.GpuTempAverage is not null || a.CpuTempAverage is not null))
            .OrderByDescending(a => (a.GpuTempAverage ?? 0) + (a.CpuTempAverage ?? 0))
            .Take(3).ToList();
        var most = Math.Max(1, apps.Count > 0 ? apps.Max(a => a.Minutes) : 1);
        HeatApps = apps.Select(a => new HistoryApp(
            a.Title ?? a.Name,
            Fmt.Duration(TimeSpan.FromMinutes(a.Minutes)),
            100.0 * a.Minutes / most,
            Loc.T("dashboard.lately.temps", Fmt.Temp(a.GpuTempMax), Fmt.Temp(a.CpuTempMax)),
            "")).ToList();
        HasHeat = HeatApps.Count > 0;
        HasWeek = history.RecordedMinutes(WeekFrom, WeekTo) >= 60;
        // The row is there as soon as there is a history: the changes are worth seeing from the first scan.
        HasLately = true;
    }

    [ObservableProperty]
    private int _score;

    [ObservableProperty]
    private Severity _severity;

    [ObservableProperty]
    private string _ratingText = "";

    [ObservableProperty]
    private string _headline = "";

    [ObservableProperty]
    private string _lastScanText = "";

    [ObservableProperty]
    private int _criticalCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _infoCount;

    [ObservableProperty]
    private int _updateCount;

    [ObservableProperty]
    private bool _hasIssues;

    [ObservableProperty]
    private bool _hasUpdates;

    public ObservableCollection<SummaryRow> SystemRows { get; } = [];
    public ObservableCollection<ComponentCardViewModel> Cards { get; } = [];
    public ObservableCollection<FindingViewModel> Issues { get; } = [];
    public ObservableCollection<FindingViewModel> Updates { get; } = [];
    public ObservableCollection<ProgramUpdateRow> Runtimes { get; } = [];
    public ObservableCollection<ProgramUpdateRow> Programs { get; } = [];
    public ObservableCollection<ManualCheck> ManualChecks { get; } = [];

    private UpdateInfo? _updateInfo;

    [ObservableProperty]
    private string _updatesStatus = "";

    [ObservableProperty]
    private bool _showUpToDate;

    [ObservableProperty]
    private string _runtimesTitle = "";

    [ObservableProperty]
    private string _programsTitle = "";

    [ObservableProperty]
    private bool _hasSuggestions;

    [ObservableProperty]
    private bool _hasRuntimes;

    [ObservableProperty]
    private bool _hasPrograms;

    public void RefreshUpdateStatus()
    {
        UpdatesStatus = Main.IsCheckingUpdates
            ? Loc.T("dashboard.updates.checking")
            : _updateInfo?.CheckedAt is { } at
                ? Loc.T("dashboard.updates.checked", Fmt.DateAndTime(at)) + (_updateInfo.Incomplete ? " " + Loc.T("dashboard.updates.incomplete") : "")
                : _updateInfo is { Incomplete: true } ? Loc.T("dashboard.updates.offline") : Loc.T("dashboard.updates.never");
    }

    private void LoadUpdates(SystemSnapshot snapshot, HealthReport health)
    {
        _updateInfo = snapshot.Updates;
        RefreshUpdateStatus();

        Updates.Clear();
        foreach (var f in health.Updates)
            Updates.Add(new FindingViewModel(f, showComponent: true));
        HasUpdates = Updates.Count > 0;
        ShowUpToDate = !HasUpdates && _updateInfo.CheckedAt is not null;

        static ProgramUpdateRow Row(AvailableUpdate u) => new(u.Name, Loc.T("dashboard.updates.versions", u.Installed, u.Latest));
        Runtimes.Clear();
        foreach (var u in _updateInfo.Runtimes) Runtimes.Add(Row(u));
        Programs.Clear();
        foreach (var u in _updateInfo.Programs) Programs.Add(Row(u));
        RuntimesTitle = Loc.T("dashboard.updates.runtimes", Runtimes.Count);
        ProgramsTitle = Loc.T("dashboard.updates.programs", Programs.Count);
        HasRuntimes = Runtimes.Count > 0;
        HasPrograms = Programs.Count > 0;
        HasSuggestions = HasRuntimes || HasPrograms;

        ManualChecks.Clear();
        if (_updateInfo.CheckedAt is null) return;
        foreach (var gpu in snapshot.Gpus.Where(g => !g.IsVirtual && !g.UsesBasicDriver && !_updateInfo.KnownGraphics.Contains(g.Name)))
        {
            if (MakerPages.Graphics(gpu.Name) is not { } page) continue;
            ManualChecks.Add(new ManualCheck(
                Loc.T("dashboard.updates.graphics", gpu.Name),
                Loc.T("dashboard.updates.graphics.detail", Fmt.Or(gpu.DriverVersion), gpu.DriverDate is { } date ? Fmt.Date(date) : Fmt.Dash),
                Loc.T("update.action.page"),
                new RelayCommand(() => ShellService.Open(page))));
        }
        if (MakerPages.Board(snapshot.Board) is { } board)
        {
            ManualChecks.Add(new ManualCheck(
                Loc.T("dashboard.updates.board", board.Maker),
                Loc.T("dashboard.updates.board.detail", board.Maker, Fmt.Or(snapshot.Board.BiosVersion), snapshot.Board.BiosDate is { } bios ? Fmt.Date(bios) : Fmt.Dash),
                Loc.T("update.action.board"),
                new RelayCommand(() => ShellService.Open(board.Url))));
        }
    }

    public void Load(SystemSnapshot snapshot, HealthReport health, IReadOnlyList<ComponentViewModel> components, SensorSnapshot live)
    {
        Score = health.Score;
        Severity = health.RatingSeverity;
        RatingText = Loc.T(health.RatingKey);
        Headline = Loc.T(health.RatingKey + ".headline");
        LastScanText = Loc.T("dashboard.lastscan", Fmt.DateAndTime(snapshot.TakenAt));
        CriticalCount = health.CriticalCount;
        WarningCount = health.WarningCount;
        InfoCount = health.InfoCount;
        UpdateCount = health.UpdateCount;

        SymbolRegular[] icons = [SymbolRegular.Desktop24, SymbolRegular.DeveloperBoard24, SymbolRegular.Ram20, SymbolRegular.Games24, SymbolRegular.HardDrive20, SymbolRegular.Window24];
        SystemRows.Clear();
        foreach (var ((label, value), icon) in ReportWriter.SystemSummary(snapshot).Zip(icons))
            SystemRows.Add(new SummaryRow(icon, label, value));

        Cards.Clear();
        foreach (var c in components)
            Cards.Add(new ComponentCardViewModel(c, Main, this));

        Issues.Clear();
        foreach (var f in health.Issues)
            Issues.Add(new FindingViewModel(f, showComponent: true));
        HasIssues = Issues.Count > 0;

        LoadUpdates(snapshot, health);

        UpdateLive(live);
    }

    public void UpdateLive(SensorSnapshot sensors)
    {
        foreach (var card in Cards)
        {
            card.UpdateLive(sensors);
            if (card.SparkSensorId is { } id && sensors.Get(id)?.Value is { } v)
            {
                if (!_history.TryGetValue(id, out var list)) _history[id] = list = [];
                list.Add(v);
                if (list.Count > HistoryLength) list.RemoveAt(0);
                card.History = list.ToArray();
            }
        }
    }

    internal IReadOnlyList<double>? HistoryFor(string? id) =>
        id is not null && _history.TryGetValue(id, out var list) ? list.ToArray() : null;

    [RelayCommand]
    private void OpenComponent(ComponentKind kind) => Main.Navigate(Icons.Key(kind));
}

public sealed partial class ComponentCardViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ComponentCardViewModel(ComponentViewModel component, MainViewModel main, DashboardViewModel dashboard)
    {
        _main = main;
        Kind = component.Kind;
        Title = component.Title;
        Icon = component.Icon;
        Severity = component.Severity;
        SeverityText = component.SeverityText;
        Score = component.Score;
        var first = component.Devices.FirstOrDefault();
        var disks = component.Devices.Count(d => d.Highlights.Count > 0);
        Subtitle = component.Kind == ComponentKind.Storage && disks > 1
            ? Loc.T("dashboard.disks", disks)
            : first?.Title ?? "";
        Metrics = first?.Highlights.Take(2).ToList() ?? [];
        SparkSensorId = component.Kind is ComponentKind.Cpu or ComponentKind.Memory or ComponentKind.Gpu
            ? component.Devices.SelectMany(d => d.Meters).Select(MeterSensor).FirstOrDefault(id => id is not null)
            : null;
        _history = dashboard.HistoryFor(SparkSensorId);
    }

    private static string? MeterSensor(MeterViewModel m) => m.SensorId;

    public ComponentKind Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public SymbolRegular Icon { get; }
    public Severity Severity { get; }
    public string SeverityText { get; }
    public int Score { get; }
    public IReadOnlyList<HighlightViewModel> Metrics { get; }
    public string? SparkSensorId { get; }
    public bool HasSpark => SparkSensorId is not null;

    [ObservableProperty]
    private IReadOnlyList<double>? _history;

    public void UpdateLive(SensorSnapshot sensors)
    {
        foreach (var m in Metrics) m.UpdateLive(sensors);
    }

    [RelayCommand]
    private void Open() => _main.Navigate(Icons.Key(Kind));
}
