using System.Collections.ObjectModel;
using CHC.App.Controls;
using CHC.App.Services;
using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Power;
using CHC.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CHC.App.ViewModels;

/// <summary>One of the supply's outputs on the Power page.</summary>
public sealed record RailRow(string Name, string Now, string Lowest, string Allowed, string Status, Severity Severity);

/// <summary>
/// The "Power" page: the power supply (read from the supply itself when it reports, else as the user describes it),
/// how much of it the PC used at its peak, its rails, and the power drawn over the last week.
/// </summary>
public sealed partial class PowerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _settings;
    private bool _loading;

    public PowerViewModel(MainViewModel main, AppSettings settings)
    {
        _main = main;
        _settings = settings;
        BuildRatings();
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            BuildRatings();
            Refresh();
        };
    }

    public ObservableCollection<Option<PsuRating>> Ratings { get; } = [];

    /// <summary>What the user typed about their supply (only used when the supply can't be read).</summary>
    [ObservableProperty]
    private string _model = "";

    [ObservableProperty]
    private double? _watts;

    [ObservableProperty]
    private Option<PsuRating>? _selectedRating;

    [ObservableProperty]
    private bool _reports;

    [ObservableProperty]
    private string _supplyTitle = "";

    [ObservableProperty]
    private IReadOnlyList<HistoryTile> _readings = [];

    [ObservableProperty]
    private bool _hasPeak;

    [ObservableProperty]
    private string _peakText = "";

    [ObservableProperty]
    private string _peakDetail = "";

    [ObservableProperty]
    private double _load;

    [ObservableProperty]
    private string _loadText = "";

    [ObservableProperty]
    private Severity _loadSeverity = Severity.Good;

    [ObservableProperty]
    private string _recommended = "";

    [ObservableProperty]
    private string _efficiency = "";

    [ObservableProperty]
    private IReadOnlyList<RailRow> _rails = [];

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _powerSeries = [];

    /// <summary>False until power was recorded (the processor's power needs administrator rights).</summary>
    [ObservableProperty]
    private bool _hasPowerHistory;

    [ObservableProperty]
    private double? _reference;

    [ObservableProperty]
    private string? _referenceLabel;

    [ObservableProperty]
    private DateTime _from;

    [ObservableProperty]
    private DateTime _to;

    [ObservableProperty]
    private IReadOnlyList<FindingViewModel> _findings = [];

    public bool IsLaptop => _main.Snapshot?.Board.IsLaptop == true;
    public TimeSpan Slot { get; } = TimeSpan.FromMinutes(30);
    public Func<double, string> WattsFormat { get; } = v => v.ToString("0", Loc.Instance.Culture) + " W";

    partial void OnModelChanged(string value) => Save();

    partial void OnWattsChanged(double? value) => Save();

    partial void OnSelectedRatingChanged(Option<PsuRating>? value) => Save();

    private void Save()
    {
        // The demo PC has its own supply; trying the fields there must not overwrite the real settings.
        if (_loading || _main.IsDemo) return;
        _settings.PsuModel = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim();
        _settings.PsuWatts = Watts is >= 100 and <= 3000 ? (int)Math.Round(Watts.Value) : null;
        _settings.PsuRating = SelectedRating is { Value: not PsuRating.Unknown } r ? r.Value.ToString() : null;
        SettingsService.Save(_settings);
        // The findings use the supply too; the overview and the Motherboard page follow.
        _main.ReanalyzePowerSupply();
        Refresh();
    }

    /// <summary>The supply as configured: the demo's own one, or what the user entered.</summary>
    public (int? Watts, PsuRating Rating, string? Model) Configured() =>
        _main.IsDemo
            ? (850, PsuRating.Gold, "Corsair RM850x")
            : (_settings.PsuWatts, Enum.TryParse<PsuRating>(_settings.PsuRating, out var r) ? r : PsuRating.Unknown, _settings.PsuModel);

    public void Refresh()
    {
        var (watts, rating, model) = Configured();
        _loading = true;
        try
        {
            Model = model ?? "";
            Watts = watts;
            SelectedRating = Ratings.FirstOrDefault(o => o.Value == rating) ?? Ratings[0];
        }
        finally
        {
            _loading = false;
        }

        var psu = PowerSupplyMath.Build(_main.LiveSensors, watts, rating, model, _main.History);
        Reports = psu.Reports;
        SupplyTitle = psu.Model ?? Loc.T("power.supply.unknown");
        var c = Loc.Instance.Culture;
        var readings = new List<HistoryTile>();
        if (psu.OutputW is { } output) readings.Add(new(Loc.T("power.reading.output"), Fmt.Watts(Math.Round(output)), ""));
        if (psu.InputW is { } input) readings.Add(new(Loc.T("power.reading.input"), Fmt.Watts(Math.Round(input)), ""));
        if (psu.OutputW is { } o && psu.InputW is { } i && i > 0) readings.Add(new(Loc.T("power.reading.efficiency"), Fmt.Percent(o / i * 100), ""));
        if (psu.Temperature is { } t) readings.Add(new(Loc.T("power.reading.temperature"), Fmt.Temp(t), ""));
        if (psu.FanRpm is { } fan) readings.Add(new(Loc.T("power.reading.fan"), fan.ToString("0", c) + " rpm", ""));
        Readings = readings;

        HasPeak = psu.PeakW is not null;
        if (psu.PeakW is { } peak)
        {
            PeakText = Fmt.Watts(Math.Round(peak));
            var when = psu.PeakTime is { } pt ? pt.ToString("ddd d MMM", c) + ", " + pt.ToString("t", c) : "";
            PeakDetail = psu.PeakApp is { } app ? Loc.T("history.tile.when.app", when, app) : when;
            Recommended = Loc.T("power.recommended", Fmt.Watts(psu.RecommendedWatts));
            if (psu.PeakLoad is { } load && psu.RatedWatts is { } rated)
            {
                Load = load * 100;
                LoadSeverity = load > 1 ? Severity.Critical : load > 0.85 ? Severity.Warning : Severity.Good;
                LoadText = Loc.T("power.load", Fmt.Percent(load * 100), Fmt.Watts(rated));
                var eff = PowerSupplyMath.Efficiency(rating, load);
                var wall = peak / eff;
                Efficiency = rating == PsuRating.Unknown ? "" : Loc.T("power.efficiency", Fmt.Watts(Math.Round(wall)), Fmt.Watts(Math.Round(wall - peak)), Fmt.Percent(eff * 100));
            }
            else
            {
                Load = 0;
                LoadText = Loc.T("power.load.unknown");
                Efficiency = "";
            }
        }

        Rails = psu.Rails.Select(r => new RailRow(
            r.Name,
            r.Now is { } now ? now.ToString("0.00", c) + " V" : Fmt.Dash,
            r.Lowest is { } low ? low.ToString("0.00", c) + " V" : Fmt.Dash,
            r.LowLimit.ToString("0.00", c) + " – " + r.HighLimit.ToString("0.00", c) + " V",
            Loc.T(r.InSpec == false ? "power.rail.low" : "power.rail.ok"),
            r.InSpec == false ? Severity.Warning : Severity.Good)).ToList();

        To = DateTime.Today.AddDays(1);
        From = To.AddDays(-7);
        var series = _main.History?.Series(HistoryMetric.SystemPower, From, To, Slot) ?? [];
        PowerSeries = [new(Loc.T("history.series.pc"), HistoryViewModel.PcColor, series, ChartValue.Max, Fill: true)];
        HasPowerHistory = series.Any(p => p.Max is not null);
        Reference = psu.RatedWatts;
        ReferenceLabel = psu.RatedWatts is { } w ? Loc.T("power.chart.rating", Fmt.Watts(w)) : null;

        Findings = (_main.Health.For(ComponentKind.Motherboard)?.Findings ?? [])
            // "Enter the wattage on the Power page" says nothing new here, next to the fields.
            .Where(f => f.Key.StartsWith("psu.", StringComparison.Ordinal) && f.Key != "psu.unknown")
            .Select(f => new FindingViewModel(f)).ToList();
        OnPropertyChanged(nameof(IsLaptop));
    }

    private void BuildRatings()
    {
        _loading = true;
        try
        {
            var selected = SelectedRating?.Value ?? PsuRating.Unknown;
            Ratings.Clear();
            foreach (var r in Enum.GetValues<PsuRating>())
                Ratings.Add(new(r, Loc.T("power.rating." + r.ToString().ToLowerInvariant())));
            SelectedRating = Ratings.First(o => o.Value == selected);
        }
        finally
        {
            _loading = false;
        }
    }
}
