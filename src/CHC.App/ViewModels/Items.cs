using System.Collections.ObjectModel;
using System.Data;
using CHC.App.Converters;
using CHC.App.Services;
using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Presentation;
using CHC.Core.Reporting;
using CHC.Core.Sensors;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace CHC.App.ViewModels;

/// <summary>Severity values for x:Static in XAML.</summary>
public static class SeverityValues
{
    public static Severity Good => Severity.Good;
    public static Severity Info => Severity.Info;
    public static Severity Update => Severity.Update;
    public static Severity Warning => Severity.Warning;
    public static Severity Critical => Severity.Critical;
}

public static class Icons
{
    public static SymbolRegular For(ComponentKind kind) => kind switch
    {
        ComponentKind.Cpu => SymbolRegular.DeveloperBoard24,
        ComponentKind.Memory => SymbolRegular.Ram20,
        ComponentKind.Storage => SymbolRegular.HardDrive20,
        ComponentKind.Gpu => SymbolRegular.Games24,
        ComponentKind.Battery => SymbolRegular.Battery1024,
        ComponentKind.Motherboard => SymbolRegular.Board24,
        ComponentKind.Stability => SymbolRegular.HeartPulse24,
        ComponentKind.Performance => SymbolRegular.TopSpeed24,
        ComponentKind.Network => SymbolRegular.Globe24,
        _ => SymbolRegular.Info24,
    };

    public static string Key(ComponentKind kind) => kind.ToString().ToLowerInvariant();
}

public sealed partial class NavItem(string key, SymbolRegular icon, Action<NavItem> onSelected) : ObservableObject
{
    private bool _isSelected;

    public string Key { get; } = key;
    public SymbolRegular Icon { get; } = icon;
    public string Title { get; } = Loc.T("nav." + key);

    /// <summary>The name of the group this item starts ("Check-up", "Tests"...), or null inside a group.</summary>
    public string? Header { get; init; }

    [ObservableProperty]
    private Severity? _severity;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value) onSelected(this);
        }
    }

    internal void SetSelectedSilently(bool value)
    {
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>One step of a finding's instructions.</summary>
public sealed record GuideStep(int Number, string Text);

public sealed class FindingViewModel(Finding finding, bool showComponent = false)
{
    public string Key => finding.Key;

    /// <summary>Comes from Windows' records of a time CHC wasn't measuring.</summary>
    public bool IsNonChc => finding.Key.StartsWith("game.past.", StringComparison.Ordinal);

    public string NonChcText => Loc.T("source.nonchc");

    public string NonChcTip => Loc.T("source.nonchc.tip");

    public bool ShowComponent { get; } = showComponent;
    public Severity Severity { get; } = finding.Severity;

    /// <summary>Shown with the updates and with their icon, never as a tip.</summary>
    public bool IsUpdate { get; } = finding.IsUpdate;
    public SymbolRegular Symbol { get; } = finding.IsUpdate ? SymbolRegular.ArrowDownload24 : SeveritySymbolConverter.For(finding.Severity);
    public SymbolRegular AdviceSymbol { get; } = finding.IsUpdate ? SymbolRegular.ArrowDownload20 : SymbolRegular.Lightbulb20;
    public string SeverityText { get; } = ReportWriter.SeverityName(finding.IsUpdate ? Severity.Update : finding.Severity);

    /// <summary>Opens the maker's page or Windows Update. CHC itself never downloads or installs anything.</summary>
    public bool HasAction { get; } = finding.ActionUrl is not null;
    public string ActionText { get; } = finding.ActionText;
    public IRelayCommand OpenCommand { get; } = new RelayCommand(() => ShellService.Open(finding.ActionUrl!));
    public string Component { get; } = Loc.T(SectionBuilder.TitleKey(finding.Component));
    public ComponentKind Kind { get; } = finding.Component;
    public string Title { get; } = finding.Title;
    public string Detail { get; } = finding.Detail;
    public string Recommendation { get; } = finding.Recommendation;

    /// <summary>The numbered list under "How to do it, and why".</summary>
    public IReadOnlyList<GuideStep> Steps { get; } = finding.Steps.Select((s, i) => new GuideStep(i + 1, s)).ToList();
    public string Why { get; } = finding.Why;
    public bool HasGuide => Steps.Count > 0 || Why.Length > 0;
}

public sealed partial class HighlightViewModel(Highlight highlight) : ObservableObject
{
    public string Label { get; } = highlight.Label;

    [ObservableProperty]
    private string _value = highlight.Value;

    public void UpdateLive(SensorSnapshot sensors)
    {
        if (highlight.SensorId is not null && sensors.Get(highlight.SensorId) is { } reading)
            Value = Fmt.Compact(highlight.SensorType ?? reading.Type, reading.Value);
    }
}

public sealed partial class MeterViewModel(Meter meter) : ObservableObject
{
    private readonly bool _captionIsPercent = meter.Caption == Fmt.Percent(meter.Percent);

    public string Label { get; } = meter.Label;
    public Severity Severity { get; } = meter.Severity;
    public string? SensorId { get; } = meter.SensorId;

    [ObservableProperty]
    private double _percent = meter.Percent;

    [ObservableProperty]
    private string _caption = meter.Caption;

    public void UpdateLive(SensorSnapshot sensors)
    {
        if (meter.SensorId is null || sensors.Get(meter.SensorId)?.Value is not { } v) return;
        Percent = v;
        if (_captionIsPercent) Caption = Fmt.Percent(v);
    }
}

public sealed partial class SensorRowViewModel(SensorReading reading) : ObservableObject
{
    public string Id { get; } = reading.Id;
    public string Name { get; } = reading.Name;
    public string Type { get; } = reading.Type;
    public string TypeName { get; } = Loc.T("sensortype." + reading.Type);

    [ObservableProperty]
    private string _value = Fmt.Sensor(reading.Type, reading.Value);

    [ObservableProperty]
    private string _min = Fmt.Sensor(reading.Type, reading.Min);

    [ObservableProperty]
    private string _max = Fmt.Sensor(reading.Type, reading.Max);

    [ObservableProperty]
    private bool _isVisible = true;

    public void Update(SensorReading r)
    {
        Value = Fmt.Sensor(r.Type, r.Value);
        Min = Fmt.Sensor(r.Type, r.Min);
        Max = Fmt.Sensor(r.Type, r.Max);
    }
}

public sealed class TableViewModel
{
    public TableViewModel(TableBlock table)
    {
        Title = table.Title;
        RowCount = table.Rows.Count;
        IsExpanded = table.Expanded;
        var data = new DataTable();
        var names = new HashSet<string>();
        foreach (var column in table.Columns)
        {
            var name = column;
            for (var i = 2; !names.Add(name); i++) name = $"{column} {i}";
            data.Columns.Add(name, typeof(string));
        }
        foreach (var row in table.Rows)
            data.Rows.Add(row.Take(data.Columns.Count).Cast<object>().ToArray());
        View = data.DefaultView;
    }

    public string Title { get; }
    public int RowCount { get; }
    public bool IsExpanded { get; }
    public DataView View { get; }
}

public sealed class DeviceViewModel
{
    public DeviceViewModel(DeviceSection device, SensorSnapshot sensors)
    {
        Title = device.Title;
        Subtitle = device.Subtitle;
        Severity = device.Severity;
        SeverityText = device.Severity is { } s ? ReportWriter.SeverityName(s) : "";
        Highlights = new(device.Highlights.Select(h => new HighlightViewModel(h)));
        Meters = new(device.Meters.Select(m => new MeterViewModel(m)));
        Specs = device.Specs;
        Tables = device.Tables.Select(t => new TableViewModel(t)).ToList();
        SensorDeviceIds = device.SensorDeviceIds;
        Sensors = new(device.SensorDeviceIds.Select(sensors.Device).OfType<SensorDevice>()
            .SelectMany(d => d.Sensors).OrderBy(r => r.Type).Select(r => new SensorRowViewModel(r)));
        SensorsHeader = Loc.T("component.livesensors", Sensors.Count);
    }

    public string Title { get; }
    public string Subtitle { get; }
    public Severity? Severity { get; }
    public string SeverityText { get; }
    public ObservableCollection<HighlightViewModel> Highlights { get; }
    public ObservableCollection<MeterViewModel> Meters { get; }
    public IReadOnlyList<SpecGroup> Specs { get; }
    public IReadOnlyList<TableViewModel> Tables { get; }
    public IReadOnlyList<string> SensorDeviceIds { get; }
    public ObservableCollection<SensorRowViewModel> Sensors { get; }
    public string SensorsHeader { get; }

    public void UpdateLive(SensorSnapshot sensors)
    {
        foreach (var h in Highlights) h.UpdateLive(sensors);
        foreach (var m in Meters) m.UpdateLive(sensors);
        foreach (var row in Sensors)
            if (sensors.Get(row.Id) is { } r) row.Update(r);
    }
}

public sealed class ComponentViewModel
{
    public ComponentViewModel(ComponentSection section, SensorSnapshot sensors)
    {
        Kind = section.Kind;
        Title = section.Title;
        Summary = section.Summary;
        Severity = section.Severity;
        SeverityText = ReportWriter.SeverityName(section.Severity);
        Score = section.HasScore ? section.Score : 0;
        ScoreText = section.HasScore ? section.Score.ToString() : Fmt.Dash;
        Icon = Icons.For(section.Kind);
        Findings = section.Findings.Select(f => new FindingViewModel(f)).ToList();
        Updates = Findings.Where(f => f.IsUpdate).ToList();
        OtherFindings = Findings.Where(f => !f.IsUpdate).ToList();
        Devices = section.Devices.Select(d => new DeviceViewModel(d, sensors)).ToList();
        FirstDeviceTitle = section.Devices.FirstOrDefault()?.Title ?? "";
    }

    public ComponentKind Kind { get; }
    public string Title { get; }
    public string Summary { get; }
    public Severity Severity { get; }
    public string SeverityText { get; }

    /// <summary>The gauge's colour: a part with only tips is healthy, so green; the "Tip" label says the rest.</summary>
    public Severity GaugeSeverity => Severity is Severity.Info or Severity.Update ? Severity.Good : Severity;
    public int Score { get; }
    public string ScoreText { get; }
    public SymbolRegular Icon { get; }
    public IReadOnlyList<FindingViewModel> Findings { get; }

    /// <summary>A part's page shows these on their own, under <see cref="OtherFindings"/>.</summary>
    public IReadOnlyList<FindingViewModel> Updates { get; }
    public IReadOnlyList<FindingViewModel> OtherFindings { get; }
    public IReadOnlyList<DeviceViewModel> Devices { get; }
    public string FirstDeviceTitle { get; }

    public void UpdateLive(SensorSnapshot sensors)
    {
        foreach (var d in Devices) d.UpdateLive(sensors);
    }
}
