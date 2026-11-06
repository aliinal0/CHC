using CHC.Core.Devices;
using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace CHC.App.ViewModels;

/// <summary>One device on the Devices page.</summary>
public sealed record DeviceRow(SymbolRegular Icon, string Name, string Connection, string Battery, Severity Severity, string SeverityText)
{
    public bool HasBattery => Battery.Length > 0;
}

/// <summary>The devices of one kind.</summary>
public sealed record DeviceGroup(string Title, IReadOnlyList<DeviceRow> Rows);

/// <summary>The "Devices" page: what is connected by cable or wirelessly, and what is wrong with it.</summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public DevicesViewModel(MainViewModel main)
    {
        _main = main;
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    [ObservableProperty]
    private IReadOnlyList<DeviceGroup> _groups = [];

    [ObservableProperty]
    private IReadOnlyList<FindingViewModel> _findings = [];

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _hasWifi;

    [ObservableProperty]
    private string _wifiSignal = "";

    [ObservableProperty]
    private string _wifiLowest = "";

    [ObservableProperty]
    private string _wifiDrops = "";

    [ObservableProperty]
    private string _wifiFailed = "";

    public void Refresh()
    {
        if (_main.Snapshot is not { } snapshot) return;
        Findings = DeviceAdvisor.Advise(snapshot).OrderByDescending(f => f.Severity).Select(f => new FindingViewModel(f)).ToList();
        Groups = snapshot.Devices
            .GroupBy(d => d.Kind)
            .OrderBy(g => g.Key)
            .Select(g => new DeviceGroup(
                Loc.T("device.kind." + g.Key.ToString().ToLowerInvariant()),
                g.OrderBy(d => d.BuiltIn).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToRow).ToList()))
            .ToList();
        IsEmpty = Groups.Count == 0;

        var wifi = snapshot.Wifi;
        var logs = snapshot.DeviceLogs;
        HasWifi = wifi is not null || logs.WifiDrops > 0 || logs.WifiFailures > 0;
        WifiSignal = wifi is null ? "–" : Fmt.Percent(wifi.AverageSignal);
        WifiLowest = wifi is null ? "–" : Fmt.Percent(wifi.LowestSignal);
        WifiDrops = logs.WifiDrops.ToString();
        WifiFailed = logs.WifiFailures.ToString();
    }

    private static DeviceRow ToRow(ConnectedDevice d)
    {
        var problem = d.UsbPortStatus != 0 || d.ProblemCode is not (0 or 22);
        var severity = problem ? Severity.Warning : d.ProblemCode == 22 ? Severity.Info : Severity.Good;
        var parts = new List<string> { DeviceAdvisor.ConnectionOf(d) };
        if (d.Manufacturer.Length > 0) parts.Add(d.Manufacturer);
        return new DeviceRow(
            IconOf(d.Kind),
            d.Name.Length > 0 ? d.Name : Loc.T("device.unknown"),
            string.Join(" · ", parts),
            d.BatteryPercent is { } battery ? Fmt.Percent(battery) : "",
            severity,
            Loc.T(problem ? "device.status.problem" : d.ProblemCode == 22 ? "device.status.disabled" : "device.status.ok"));
    }

    private static SymbolRegular IconOf(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => SymbolRegular.CursorClick24,
        DeviceKind.Keyboard => SymbolRegular.Keyboard24,
        DeviceKind.GameController => SymbolRegular.Games24,
        DeviceKind.Headset => SymbolRegular.Headphones24,
        DeviceKind.Audio => SymbolRegular.Speaker224,
        DeviceKind.Camera => SymbolRegular.Video24,
        DeviceKind.Storage => SymbolRegular.HardDrive20,
        DeviceKind.Display => SymbolRegular.Desktop24,
        DeviceKind.Network => SymbolRegular.Globe24,
        DeviceKind.BluetoothAdapter => SymbolRegular.Bluetooth24,
        DeviceKind.Printer => SymbolRegular.Print24,
        DeviceKind.Phone => SymbolRegular.Phone24,
        _ => SymbolRegular.PlugConnected24,
    };
}
