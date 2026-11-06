using System.Windows;
using CHC.App.Controls;
using CHC.Core.Demo;
using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Network;
using CHC.Core.Past;
using CHC.Core.Performance;
using CHC.Core.Presentation;
using CHC.Core.Sensors;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CHC.App.ViewModels;

/// <summary>A game on the Game performance page: how it ran lately, what holds it back, what to change.</summary>
public sealed record GameCard(string Title, string Subtitle, IReadOnlyList<HistoryTile> Tiles, IReadOnlyList<FindingViewModel> Findings,
    IReadOnlyList<ChartSeries> Series, DateTime From, DateTime To)
{
    public bool HasChart => Series.Count > 0;
    public TimeSpan Slot { get; } = TimeSpan.FromDays(1);
    public Func<double, string> FpsFormat { get; } = v => v.ToString("0", Loc.Instance.Culture) + " FPS";

    /// <summary>When it was played: by CHC's own measurement, and by Windows' records where CHC wasn't looking.</summary>
    public IReadOnlyList<ChartSeries> PlayedSeries { get; init; } = [];

    public bool HasPlayedChart => PlayedSeries.Count > 0;
    public string PlayedTitle { get; init; } = "";
    public DateTime PlayedFrom { get; init; }
    public Func<double, string> MinutesFormat { get; } = v => Loc.T("common.duration.minutes", (int)v);

    /// <summary>Only Windows' records know this game so far.</summary>
    public bool IsNonChc { get; init; }

    /// <summary>Says plainly how much of this comes from where.</summary>
    public string SourceNote { get; init; } = "";
}

/// <summary>The game performance page: start/stop a measuring session, live values, the games in the history, and the analysis.</summary>
public sealed partial class PerformanceViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 120;

    private readonly MainViewModel _main;
    private readonly DemoData? _demo;
    private readonly PerformanceMonitor _monitor;
    private readonly List<double> _fps = [];
    private readonly List<double> _frameTimes = [];
    private int _gamesRun;

    public PerformanceViewModel(MainViewModel main, Func<SensorSnapshot> readSensors, DemoData? demo)
    {
        _main = main;
        _demo = demo;
        _monitor = new PerformanceMonitor(
            readSensors,
            () => demo is null ? new EtwFrameSource() : new DemoFrameSource(),
            scanProcesses: demo is null,
            createTraffic: () => demo is null ? new RealGameTraffic() : new DemoGameTraffic(),
            // The game session already counts traffic per program, so the ping test doesn't need its own counter.
            createNetwork: () => new NetworkMonitor(id => demo is null ? new RealNetProbe(id, measurePrograms: false) : new DemoNetProbe()),
            readBattery: demo is null ? null : demo.BatteryNow);
        _monitor.Updated += (_, state) => Application.Current?.Dispatcher.BeginInvoke(() => Apply(state));
        _status = Loc.T("perf.status.idle");
    }

    public MainViewModel Main => _main;

    /// <summary>Frame times need an ETW session, which Windows only allows for administrators.</summary>
    public bool ShowAdminHint => _demo is null && !_main.IsElevated;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasLive;

    [ObservableProperty]
    private string _status;

    [ObservableProperty] private string _fpsText = Fmt.Dash;
    [ObservableProperty] private string _onePercentText = Fmt.Dash;
    [ObservableProperty] private string _frameTimeText = Fmt.Dash;
    [ObservableProperty] private string _stuttersText = Fmt.Dash;
    [ObservableProperty] private string _cpuCoreText = Fmt.Dash;
    [ObservableProperty] private string _gpuLoadText = Fmt.Dash;
    [ObservableProperty] private string _gpuTempText = Fmt.Dash;
    [ObservableProperty] private string _vramText = Fmt.Dash;
    [ObservableProperty] private string _pingText = Fmt.Dash;
    [ObservableProperty] private string _lossText = Fmt.Dash;
    [ObservableProperty] private string _powerText = Fmt.Dash;
    [ObservableProperty] private string _batteryText = Fmt.Dash;

    /// <summary>The processor (and graphics card) report their power; the power tile is shown.</summary>
    [ObservableProperty] private bool _hasPower;

    /// <summary>A laptop: the battery tile is shown.</summary>
    [ObservableProperty] private bool _hasBattery;

    /// <summary>The game was found to be online; the ping tiles are shown.</summary>
    [ObservableProperty] private bool _isOnline;

    [ObservableProperty] private IReadOnlyList<double>? _fpsHistory;
    [ObservableProperty] private double _fpsChartMax = 144;
    [ObservableProperty] private IReadOnlyList<double>? _frameTimeHistory;
    [ObservableProperty] private double _frameChartMax = 40;

    [ObservableProperty]
    private ComponentViewModel? _result;

    [ObservableProperty]
    private IReadOnlyList<GameCard> _games = [];

    /// <summary>No game in the history yet.</summary>
    [ObservableProperty]
    private bool _noGames;

    /// <summary>Why the section knows less than it could: the history is off, or no frame rates without administrator rights.</summary>
    [ObservableProperty]
    private string _gamesHint = "";

    private bool CanStart() => !IsRunning && _main.Snapshot is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (_main.Snapshot is not { } snapshot) return;
        _fps.Clear();
        _frameTimes.Clear();
        FpsHistory = null;
        FrameTimeHistory = null;
        FpsText = OnePercentText = FrameTimeText = StuttersText = CpuCoreText = GpuLoadText = GpuTempText = VramText = PingText = LossText = PowerText = BatteryText = Fmt.Dash;
        IsOnline = HasPower = HasBattery = false;
        if (_demo is not null) _demo.Gaming = true;

        _monitor.Start(snapshot.Gaming.RefreshHz, snapshot.Network, snapshot.Board.IsLaptop);
        IsRunning = true;
        HasLive = true;
        Status = Loc.T(_monitor.FrameError is null ? "perf.status.waiting" : "perf.status.nofps");
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        var session = _monitor.Stop();
        IsRunning = false;
        if (_demo is not null)
        {
            _demo.Gaming = false;
            if (session is not null)
            {
                session.Background = DemoFrameSource.Background();
                session.Settings = DemoData.GameSettings();
                session.Display = DemoData.GameDisplay();
            }
        }
        if (session is not null && _main.Snapshot is { } snapshot)
        {
            snapshot.Performance = session;
            Status = Loc.T("perf.status.done", session.Game.Length > 0 ? session.Game : Loc.T("perf.unknowngame"), Fmt.Elapsed(session.Duration));
        }
        else
        {
            Status = Loc.T("perf.status.idle");
        }
        Refresh();
        _main.Report.Refresh();
    }

    /// <summary>Rebuilds the analysis (after a session, a rescan or a language change).</summary>
    public void Refresh()
    {
        if (_main.Snapshot is not { } snapshot) return;
        Result = new ComponentViewModel(PerformanceSection.Build(snapshot, snapshot.Performance), _main.LiveSensors);
        StartCommand.NotifyCanExecuteChanged();
        if (!IsRunning && snapshot.Performance is null) Status = Loc.T("perf.status.idle");
        if (_main.CurrentPage == this) LoadGames();
    }

    /// <summary>Reads the games out of the history and judges them; a large history and the games' settings files take a moment.</summary>
    public async void LoadGames()
    {
        if (_main.Snapshot is not { } snapshot) return;
        var history = _main.History;
        var run = ++_gamesRun;
        IReadOnlyList<GameReport> reports = [];
        if (history is not null)
        {
            Func<string, GameSettings?>? readSettings = _demo is null
                ? null
                : path => path.Contains("Fortnite", StringComparison.Ordinal) ? DemoData.GameSettings() : null;
            try
            {
                reports = await Task.Run(() => GameAdvisor.Build(history, snapshot, readSettings));
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // A locked or damaged history file: the page works without this section.
            }
        }
        if (run != _gamesRun) return;
        Games = reports.Select(ToCard).ToList();
        NoGames = Games.Count == 0;
        var hints = new List<string>();
        if (!_main.RecordHistory) hints.Add(Loc.T("perf.games.off"));
        else if (ShowAdminHint) hints.Add(Loc.T("perf.games.nofps"));
        if (_demo is null && _main.RecordsReadAt is null) hints.Add(Loc.T("perf.games.lookback"));
        GamesHint = string.Join(" ", hints);
    }

    private static GameCard ToCard(GameReport r)
    {
        var u = r.Usage;
        var culture = Loc.Instance.Culture;
        var lastPlayed = r.Past is { } known && known.Last > u.LastPlayed ? known.Last : u.LastPlayed;
        var days = (DateTime.Today - lastPlayed.Date).Days;
        var last = days switch { 0 => Loc.T("timeline.today"), 1 => Loc.T("timeline.yesterday"), _ => Loc.T("timeline.daysago", days) };
        var (limit, detail) = r.Limit switch
        {
            GameLimit.GraphicsCard => ("game.limit.gpu", Loc.T("game.limit.gpu.detail", u.GpuBusyShare)),
            GameLimit.Processor => ("game.limit.cpu", Loc.T("game.limit.cpu.detail")),
            GameLimit.FrameCap => ("game.limit.cap", Loc.T("game.limit.cap.detail", u.Fps)),
            GameLimit.Nothing => ("game.limit.none", Loc.T("game.limit.none.detail", r.MonitorHz)),
            GameLimit.Balanced => ("game.limit.balanced", Loc.T("game.limit.balanced.detail")),
            _ => ("game.limit.unknown", Loc.T("game.limit.unknown.detail")),
        };
        var to = DateTime.Today.AddDays(1);
        // One day says nothing about a trend.
        IReadOnlyList<ChartSeries> series = r.DailyFps.Count < 2
            ? []
            :
            [
                new(Loc.T("perf.games.series.fps"), Palette.BrandColor, r.DailyFps),
                new(Loc.T("perf.games.series.low"), HistoryViewModel.PcColor, r.DailyFps, ChartValue.Min),
            ];
        var nonChc = r.Past is { } records ? PastAnalyzer.BeyondMeasured(records.Days, r.MeasuredMinutes) : [];
        var measuredMinutes = r.MeasuredMinutes.Sum(d => d.Minutes);
        var nonChcMinutes = nonChc.Sum(d => d.Minutes);
        var played = r.Past is null ? u.Minutes : measuredMinutes + nonChcMinutes;
        static List<HistoryPoint> Points(IEnumerable<PastDay> days) => days.Select(d => new HistoryPoint(d.Date, d.Minutes, null, null)).ToList();
        var playedSeries = new List<ChartSeries>();
        if (r.Past is not null && r.MeasuredMinutes.Count + nonChc.Count >= 2)
        {
            if (r.MeasuredMinutes.Count > 0) playedSeries.Add(new(Loc.T("source.chc"), Palette.BrandColor, Points(r.MeasuredMinutes)));
            if (nonChc.Count > 0) playedSeries.Add(new(Loc.T("source.nonchc"), HistoryViewModel.PcColor, Points(nonChc), Dashed: true));
        }
        var sourceNote = r.Past is null ? ""
            : !r.Measured ? Loc.T("perf.games.source.records")
            : nonChcMinutes >= 1 ? Loc.T("perf.games.source.both", Fmt.Duration(TimeSpan.FromMinutes(measuredMinutes)), Fmt.Duration(TimeSpan.FromMinutes(nonChcMinutes)))
            : "";
        var onlyOpen = r.Past is { OpenSeconds: >= 60 };
        if (onlyOpen) sourceNote += " " + Loc.T("perf.games.source.open");
        var notYet = Loc.T("perf.games.tile.notyet");
        return new GameCard(
            r.Title,
            r.Past switch
            {
                null => Loc.T("perf.games.subtitle", Fmt.Duration(TimeSpan.FromMinutes(u.Minutes)), last.ToLower(culture)),
                { OpenSeconds: >= 60, PlayedSeconds: >= 60 } both => Loc.T("perf.games.subtitle.both", Fmt.Duration(TimeSpan.FromSeconds(both.PlayedSeconds)), Fmt.Duration(TimeSpan.FromSeconds(both.OpenSeconds)), last.ToLower(culture)),
                { OpenSeconds: >= 60 } open => Loc.T("perf.games.subtitle.open", Fmt.Duration(TimeSpan.FromSeconds(open.OpenSeconds)), GameAdvisor.PastDays, last.ToLower(culture)),
                _ => Loc.T("perf.games.subtitle.past", Fmt.Duration(TimeSpan.FromMinutes(played)), GameAdvisor.PastDays, last.ToLower(culture), r.Past.Sessions),
            },
            r.Measured
                ?
                [
                    new(Loc.T("perf.games.tile.fps"), u.Fps is { } fps ? fps.ToString("0", culture) : Fmt.Dash,
                        u.FpsLow is { } low ? Loc.T("perf.games.tile.fps.low", low) : Loc.T("perf.games.tile.fps.none")),
                    new(Loc.T("perf.games.tile.limit"), Loc.T(limit), detail),
                    new(Loc.T("perf.games.tile.gpu"), Fmt.Percent(u.GpuLoad), Loc.T("perf.games.tile.upto", Fmt.Temp(u.GpuTempMax))),
                    new(Loc.T("perf.games.tile.cpu"), Fmt.Percent(u.CpuLoad), Loc.T("perf.games.tile.upto", Fmt.Temp(u.CpuTempMax))),
                ]
                :
                [
                    new(Loc.T("perf.games.tile.fps"), Fmt.Dash, notYet),
                    new(Loc.T("perf.games.tile.limit"), Fmt.Dash, notYet),
                    new(Loc.T("perf.games.tile.gpu"), Fmt.Dash, notYet),
                    new(Loc.T("perf.games.tile.cpu"), Fmt.Percent(u.CpuLoad), Loc.T("perf.games.tile.records")),
                ],
            r.Findings.Select(f => new FindingViewModel(f)).ToList(),
            series,
            to.AddDays(-GameAdvisor.PeriodDays),
            to)
        {
            PlayedSeries = playedSeries,
            PlayedTitle = Loc.T(onlyOpen ? "perf.games.chart.open" : "perf.games.chart.played"),
            PlayedFrom = to.AddDays(-GameAdvisor.PastDays),
            IsNonChc = !r.Measured,
            SourceNote = sourceNote,
        };
    }

    private void Apply(PerfLiveState s)
    {
        if (!IsRunning) return;
        Status = s.Game is null
            ? Loc.T(s.FpsAvailable ? "perf.status.waiting" : "perf.status.nofps")
            : Loc.T(s.Online ? "perf.status.measuring.online" : "perf.status.measuring", s.Game, Fmt.Elapsed(s.Elapsed));
        IsOnline = s.Online;
        PingText = Fmt.Ms(s.PingMs);
        LossText = s.LossPercent is { } loss ? Fmt.Percent(loss, 1) : Fmt.Dash;

        FpsText = s.Fps is { } fps ? fps.ToString("0", Loc.Instance.Culture) : Fmt.Dash;
        OnePercentText = s.OnePercentLowFps is { } low ? low.ToString("0", Loc.Instance.Culture) : Fmt.Dash;
        FrameTimeText = s.FrameTimeMs is { } ft ? ft.ToString("0.0", Loc.Instance.Culture) + " ms" : Fmt.Dash;
        StuttersText = s.FpsAvailable ? s.Stutters.ToString() : Fmt.Dash;
        CpuCoreText = Fmt.Percent(s.Sample.CpuMaxCoreLoad);
        GpuLoadText = Fmt.Percent(s.Sample.GpuLoad);
        GpuTempText = Fmt.Temp(s.Sample.GpuTemp);
        VramText = s.Sample.VramUsedMb is { } vram ? Fmt.Bytes((ulong)(vram * 1024 * 1024)) : Fmt.Dash;

        // Graphics built into the processor are already part of the processor's power.
        var discrete = _main.Snapshot?.Gpus.Any(g => !g.IsIntegrated && !g.IsVirtual && !g.UsesBasicDriver) == true;
        if (s.Sample.CpuPowerW is { } cpuW)
        {
            HasPower = true;
            PowerText = Fmt.Watts(Math.Round(cpuW + (discrete ? s.Sample.GpuPowerW ?? 0 : 0)));
        }
        if (s.Sample.OnBattery is { } onBattery)
        {
            HasBattery = true;
            var level = Fmt.Percent(s.Sample.BatteryPercent);
            BatteryText = s.Sample.BatteryRateW is { } rate && Math.Abs(rate) >= 0.5
                ? $"{level} · {(rate > 0 ? "+" : "−")}{Fmt.Watts(Math.Round(Math.Abs(rate)))}"
                : onBattery ? level : $"{level} · {Loc.T("battery.status.ac")}";
        }

        if (s.Fps is { } f)
        {
            Push(_fps, f);
            FpsHistory = _fps.ToArray();
            FpsChartMax = Math.Max(60, _fps.Max() * 1.15);
        }
        if (s.Sample.WorstFrameMs is { } worst)
        {
            Push(_frameTimes, worst);
            FrameTimeHistory = _frameTimes.ToArray();
            FrameChartMax = Math.Max(20, _frameTimes.Max() * 1.15);
        }
    }

    private static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    public void Dispose() => _monitor.Dispose();
}
