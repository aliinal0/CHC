using System.Collections.ObjectModel;
using System.Windows.Media;
using CHC.App.Controls;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CHC.App.ViewModels;

/// <summary>A highlight above the charts: the hottest moment, the highest power, the data downloaded.</summary>
public sealed record HistoryTile(string Title, string Value, string Detail);

/// <summary>One program on the history page.</summary>
public sealed record HistoryApp(string Name, string Time, double Share, string Temperatures, string Power);

/// <summary>The "Trends" page: what the recorder kept, as charts for a day, a week, a month or a year.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    // Chart colours; one per kind of value, the same on every chart.
    public static readonly Color CpuColor = Color.FromRgb(0x3B, 0x8E, 0xF5);
    public static readonly Color GpuColor = Color.FromRgb(0x2E, 0xC4, 0x8C);
    public static readonly Color RamColor = Color.FromRgb(0xA7, 0x8B, 0xFA);
    public static readonly Color PcColor = Color.FromRgb(0xF0, 0xA0, 0x2C);
    public static readonly Color UpColor = Color.FromRgb(0xE8, 0x63, 0x8C);
    public static readonly Color FanColor = Color.FromRgb(0x22, 0xC5, 0xE0);

    private readonly MainViewModel _main;
    private int _offset;

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        BuildRanges();
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            BuildRanges();
            Refresh();
        };
    }

    public ObservableCollection<Option<HistoryRange>> Ranges { get; } = [];

    [ObservableProperty]
    private Option<HistoryRange>? _selectedRange;

    [ObservableProperty]
    private DateTime _from;

    [ObservableProperty]
    private DateTime _to;

    [ObservableProperty]
    private TimeSpan _slot;

    [ObservableProperty]
    private string _periodLabel = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _canGoNext;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _temperatures = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _loads = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _power = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _network = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _fans = [];

    [ObservableProperty]
    private IReadOnlyList<HistoryTile> _tiles = [];

    [ObservableProperty]
    private IReadOnlyList<HistoryApp> _apps = [];

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool _hasFans;

    /// <summary>Recording, paused or off; shown at the top so an empty chart is never a mystery.</summary>
    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isRecordingOff;

    public Func<double, string> Celsius { get; } = v => Fmt.Temp(v);
    public Func<double, string> Percent { get; } = v => Fmt.Percent(v);
    public Func<double, string> Watts { get; } = v => v.ToString("0", Loc.Instance.Culture) + " W";
    public Func<double, string> Rate { get; } = Mbps;
    public Func<double, string> Rpm { get; } = v => v.ToString("0", Loc.Instance.Culture) + " rpm";

    partial void OnSelectedRangeChanged(Option<HistoryRange>? value)
    {
        if (_building) return;
        _offset = 0;
        Refresh();
    }

    [RelayCommand]
    private void Previous()
    {
        _offset++;
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        _offset = Math.Max(0, _offset - 1);
        Refresh();
    }

    [RelayCommand]
    private void OpenSettings() => _main.Navigate("settings");

    /// <summary>Reads the selected period from the history again (on navigation and when a new minute is written).</summary>
    public void Refresh()
    {
        var range = SelectedRange?.Value ?? HistoryRange.Day;
        var (from, to) = Period(range, _offset);
        var slot = range.Slot();
        From = from;
        To = to;
        Slot = slot;
        PeriodLabel = Label(range, from, to);
        CanGoNext = _offset > 0;
        IsRecordingOff = !_main.RecordHistory;
        Status = Loc.T(!_main.RecordHistory ? "history.off" : _main.RecordingPaused ? "history.paused" : "history.recording");

        if (_main.History is not { } h)
        {
            HasData = false;
            return;
        }
        IReadOnlyList<HistoryPoint> S(HistoryMetric m) => h.Series(m, from, to, slot);
        var cpu = Loc.T("history.series.cpu");
        var gpu = Loc.T("history.series.gpu");
        Temperatures =
        [
            new(cpu, CpuColor, S(HistoryMetric.CpuTemp), ChartValue.Max),
            new(gpu, GpuColor, S(HistoryMetric.GpuTemp), ChartValue.Max),
        ];
        Loads =
        [
            new(cpu, CpuColor, S(HistoryMetric.CpuLoad)),
            new(gpu, GpuColor, S(HistoryMetric.GpuLoad)),
            new(Loc.T("history.series.ram"), RamColor, S(HistoryMetric.RamLoad)),
        ];
        Power =
        [
            new(Loc.T("history.series.pc"), PcColor, S(HistoryMetric.SystemPower), ChartValue.Max, Fill: true),
            new(cpu, CpuColor, S(HistoryMetric.CpuPower), ChartValue.Max),
            new(gpu, GpuColor, S(HistoryMetric.GpuPower), ChartValue.Max),
        ];
        Network =
        [
            new(Loc.T("history.series.down"), CpuColor, S(HistoryMetric.NetDown), Fill: true),
            new(Loc.T("history.series.up"), UpColor, S(HistoryMetric.NetUp)),
        ];
        var fans = S(HistoryMetric.Fan);
        HasFans = fans.Any(p => p.Max > 0);
        Fans = [new(Loc.T("history.series.fan"), FanColor, fans, ChartValue.Max)];

        var minutes = h.RecordedMinutes(from, to);
        HasData = minutes > 0;
        Tiles = BuildTiles(h, from, to, minutes);
        var apps = h.Apps(from, to, 10);
        var most = Math.Max(1, apps.Count > 0 ? apps.Max(a => a.Minutes) : 1);
        Apps = apps.Select(a => new HistoryApp(
            a.Title ?? Path(a.Name),
            Fmt.Duration(TimeSpan.FromMinutes(a.Minutes)),
            100.0 * a.Minutes / most,
            Loc.T("history.app.temps", Fmt.Temp(a.GpuTempMax), Fmt.Temp(a.CpuTempMax)),
            a.SystemPowerMax is { } w ? Loc.T("history.app.power", Fmt.Watts(Math.Round(w))) : "")).ToList();
    }

    private static List<HistoryTile> BuildTiles(HistoryStore h, DateTime from, DateTime to, int minutes)
    {
        string When(HistoryPeak p) => p.App is { } app
            ? Loc.T("history.tile.when.app", When2(p.Time), app)
            : When2(p.Time);
        // A time of day within a day, the day and time within a week, only the day beyond.
        string When2(DateTime t) => (to - from).TotalDays switch
        {
            <= 1.5 => t.ToString("t", Loc.Instance.Culture),
            <= 8 => t.ToString("ddd", Loc.Instance.Culture) + " " + t.ToString("t", Loc.Instance.Culture),
            _ => t.ToString("d MMM", Loc.Instance.Culture),
        };

        var tiles = new List<HistoryTile>();
        if (h.Peak(HistoryMetric.CpuTemp, from, to) is { } cpu)
            tiles.Add(new(Loc.T("history.tile.cpu"), Fmt.Temp(cpu.Value), When(cpu)));
        if (h.Peak(HistoryMetric.GpuTemp, from, to) is { } gpu)
            tiles.Add(new(Loc.T("history.tile.gpu"), Fmt.Temp(gpu.Value), When(gpu)));
        if (h.Peak(HistoryMetric.SystemPower, from, to) is { } power)
            tiles.Add(new(Loc.T("history.tile.power"), Fmt.Watts(Math.Round(power.Value)), When(power)));
        var (down, up) = h.Traffic(from, to);
        if (down > 0)
            tiles.Add(new(Loc.T("history.tile.data"), Fmt.Bytes((ulong)down), Loc.T("history.tile.uploaded", Fmt.Bytes((ulong)up))));
        if (minutes > 0)
            tiles.Add(new(Loc.T("history.tile.time"), Fmt.Duration(TimeSpan.FromMinutes(minutes)), Loc.T("history.tile.time.detail")));
        return tiles;
    }

    /// <summary>
    /// Today (or an earlier day), or the last 7, 30 or 365 days ending today: always full of data, unlike a calendar
    /// month that has just begun. <paramref name="offset"/> steps back one whole period at a time.
    /// </summary>
    private static (DateTime From, DateTime To) Period(HistoryRange range, int offset)
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var days = range switch { HistoryRange.Day => 1, HistoryRange.Week => 7, HistoryRange.Month => 30, _ => 365 };
        var to = tomorrow.AddDays(-days * offset);
        return (to.AddDays(-days), to);
    }

    private static string Label(HistoryRange range, DateTime from, DateTime to)
    {
        var c = Loc.Instance.Culture;
        var last = to.AddDays(-1);
        return range switch
        {
            HistoryRange.Day => from.ToString("D", c),
            HistoryRange.Year => from.ToString("MMM yyyy", c) + " – " + last.ToString("MMM yyyy", c),
            _ => from.ToString("d MMM", c) + " – " + last.ToString("d MMM yyyy", c),
        };
    }

    private void BuildRanges()
    {
        var selected = SelectedRange?.Value ?? HistoryRange.Day;
        Ranges.Clear();
        foreach (var r in Enum.GetValues<HistoryRange>())
            Ranges.Add(new(r, Loc.T("history.range." + r.ToString().ToLowerInvariant())));
        _building = true;
        try { SelectedRange = Ranges.First(r => r.Value == selected); }
        finally { _building = false; }
    }

    private bool _building;

    private static string Path(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    /// <summary>Bytes per second as megabits per second, like the network page.</summary>
    private static string Mbps(double bytesPerSecond)
    {
        var mbps = bytesPerSecond * 8 / 1_000_000;
        return mbps.ToString(mbps < 10 ? "0.0" : "0", Loc.Instance.Culture) + " Mbps";
    }
}
