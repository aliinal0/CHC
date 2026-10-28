using System.Collections.ObjectModel;
using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;
using CHC.Core.Reporting;
using CHC.Core.Sensors;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace CHC.App.ViewModels;

public sealed record SummaryRow(SymbolRegular Icon, string Label, string Value);

public sealed partial class DashboardViewModel(MainViewModel main) : ObservableObject
{
    private const int HistoryLength = 60;
    private readonly Dictionary<string, List<double>> _history = [];

    public MainViewModel Main { get; } = main;

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
    private bool _hasIssues;

    public ObservableCollection<SummaryRow> SystemRows { get; } = [];
    public ObservableCollection<ComponentCardViewModel> Cards { get; } = [];
    public ObservableCollection<FindingViewModel> Issues { get; } = [];

    public void Load(SystemSnapshot snapshot, HealthReport health, IReadOnlyList<ComponentViewModel> components)
    {
        Score = health.Score;
        Severity = health.Severity;
        RatingText = Loc.T(health.RatingKey);
        Headline = Loc.T(health.RatingKey + ".headline");
        LastScanText = Loc.T("dashboard.lastscan", Fmt.DateAndTime(snapshot.TakenAt));
        CriticalCount = health.CriticalCount;
        WarningCount = health.WarningCount;
        InfoCount = health.InfoCount;

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

        UpdateLive(snapshot.Sensors);
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
