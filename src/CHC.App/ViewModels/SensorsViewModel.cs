using System.Collections.ObjectModel;
using CHC.Core.Localization;
using CHC.Core.Sensors;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CHC.App.ViewModels;

public sealed partial class SensorGroupViewModel(SensorDevice device) : ObservableObject
{
    public string Title { get; } = device.Name;
    public string KindText { get; } = Loc.Instance.Has("hwtype." + device.Kind) ? Loc.T("hwtype." + device.Kind) : device.Kind;
    public ObservableCollection<SensorRowViewModel> Rows { get; } = new(device.Sensors.OrderBy(s => s.Type).Select(s => new SensorRowViewModel(s)));

    [ObservableProperty]
    private bool _isVisible = true;
}

/// <summary>Every LibreHardwareMonitor sensor, grouped by hardware: the most technical view.</summary>
public sealed partial class SensorsViewModel : ObservableObject
{
    public ObservableCollection<SensorGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _countText = "";

    public void Load(SensorSnapshot sensors)
    {
        Groups.Clear();
        foreach (var device in sensors.Devices.Where(d => d.Sensors.Count > 0))
            Groups.Add(new SensorGroupViewModel(device));
        CountText = Loc.T("sensors.count", Groups.Sum(g => g.Rows.Count), Groups.Count);
        ApplyFilter();
    }

    public void UpdateLive(SensorSnapshot sensors)
    {
        foreach (var group in Groups)
            foreach (var row in group.Rows)
                if (sensors.Get(row.Id) is { } reading) row.Update(reading);
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var text = Filter.Trim();
        foreach (var group in Groups)
        {
            var groupMatch = text.Length == 0 || group.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase);
            foreach (var row in group.Rows)
                row.IsVisible = groupMatch
                    || row.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || row.TypeName.Contains(text, StringComparison.CurrentCultureIgnoreCase);
            group.IsVisible = group.Rows.Any(r => r.IsVisible);
        }
    }
}
