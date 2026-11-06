using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Models;

namespace CHC.Core.Devices;

/// <summary>What is wrong with what is connected: failing USB ports, devices that keep dropping out, slow links, driver problems, low batteries.</summary>
public static class DeviceAdvisor
{
    private const ComponentKind K = ComponentKind.Stability;
    public const int DropDays = 7;
    public const int LogDays = 30;

    /// <summary>Back on Wi-Fi within this time, it wasn't the user switching off or the PC going to sleep.</summary>
    public static readonly TimeSpan WifiBack = TimeSpan.FromMinutes(2);

    private const int WifiWeakSignal = 40;
    private const int WifiMinMinutes = 60;
    private const int WifiWeakShare = 25;
    private const int WifiMinDrops = 5;
    private const int WifiManyDrops = 20;

    /// <summary>A person takes a while to replug a USB device; back within this time, it is usually the connection that failed.</summary>
    private static readonly TimeSpan UsbGap = TimeSpan.FromMinutes(2);

    /// <summary>Bluetooth devices sleep and reconnect by design, so only a quick return counts.</summary>
    private static readonly TimeSpan WirelessGap = TimeSpan.FromSeconds(20);

    /// <summary>Several devices going at once is the PC (sleep, a hub, a restart of the USB system), not any one device.</summary>
    private static readonly TimeSpan Together = TimeSpan.FromSeconds(10);

    /// <summary>A peripheral with a problem says little about the health of the PC, so none of this counts for the score.</summary>
    public static IReadOnlyList<Finding> Advise(SystemSnapshot snapshot)
    {
        var list = new List<Finding>();
        foreach (var d in snapshot.Devices.Where(d => d.UsbPortStatus != 0))
            list.Add(new(K, Severity.Warning, "device.usbport", Name(d), new LocKey("device.usbport." + (d.UsbPortStatus is >= 2 and <= 6 ? d.UsbPortStatus.ToString() : "other"))) { Scored = false });

        foreach (var d in snapshot.Devices.Where(d => d.ProblemCode != 0 && d.UsbPortStatus == 0))
            list.Add(new(K, d.ProblemCode == 22 ? Severity.Info : Severity.Warning, "device.problem", Name(d), new LocKey(HealthAnalyzer.DeviceErrorKey(d.ProblemCode))) { Scored = false });

        foreach (var drop in snapshot.DeviceDrops.OrderByDescending(d => d.Count))
            list.Add(new(K, drop.Count >= 10 ? Severity.Warning : Severity.Info, drop.Wireless ? "device.drops.wireless" : "device.drops.usb", drop.Name, drop.Count, DropDays) { Scored = false });

        // Speed only matters for what moves a lot of data: a mouse has no use for USB 3.
        foreach (var d in snapshot.Devices.Where(d => d is { SuperSpeedCapable: true, Speed: < UsbSpeed.Super, BuiltIn: false, UsbPortStatus: 0 }))
            list.Add(new(K, Severity.Info, "device.slowusb", Name(d)) { Scored = false });

        foreach (var d in snapshot.Devices.Where(d => d.BatteryPercent is <= 20))
            list.Add(new(K, Severity.Info, "device.battery.low", Name(d), d.BatteryPercent) { Scored = false });

        foreach (var f in snapshot.DeviceLogs.StartFailures.Where(f => f.Name.Length > 0))
            list.Add(new(K, f.Count >= 5 ? Severity.Warning : Severity.Info, "device.startfailed", f.Name, f.Count, LogDays) { Scored = false });

        AddWifi(snapshot, list);
        return list;
    }

    private static void AddWifi(SystemSnapshot snapshot, List<Finding> list)
    {
        var logs = snapshot.DeviceLogs;
        if (logs.WifiDrops >= WifiMinDrops)
            list.Add(new(K, logs.WifiDrops >= WifiManyDrops ? Severity.Warning : Severity.Info, "device.wifi.drops", logs.WifiDrops, DropDays) { Scored = false });
        if (logs.WifiFailures >= WifiMinDrops)
            list.Add(new(K, Severity.Info, "device.wifi.failed", logs.WifiFailures, DropDays) { Scored = false });
        if (snapshot.Wifi is { Minutes: >= WifiMinMinutes, WeakPercent: >= WifiWeakShare } wifi)
            list.Add(new(K, Severity.Info, "device.wifi.weak", wifi.WeakPercent, DropDays) { Scored = false });
    }

    /// <summary>"USB 2.0 · 480 Mbit/s", "Bluetooth · connected", "DisplayPort".</summary>
    public static string ConnectionOf(ConnectedDevice d) => d.Bus switch
    {
        DeviceBus.Usb when d.Speed is null => Loc.T("device.conn.usb"),
        DeviceBus.Usb => Loc.T("device.conn.usb." + d.Speed.Value.ToString().ToLowerInvariant()),
        DeviceBus.Bluetooth => Loc.T(d.Connected switch { true => "device.conn.bt.on", false => "device.conn.bt.off", _ => "device.conn.bt" }),
        DeviceBus.Video => Loc.T("device.conn.video." + (d.VideoOutput ?? "other")),
        _ => Loc.T("device.conn.internal"),
    };

    private static string Name(ConnectedDevice d) => d.Name.Length > 0 ? d.Name : Loc.T("device.unknown");

    /// <summary>Devices that went and came back within moments, at least three times.</summary>
    public static IReadOnlyList<DeviceDrops> Drops(IReadOnlyList<DeviceEvent> events)
    {
        var sorted = events.OrderBy(e => e.Time).ToList();
        var result = new Dictionary<string, (string Name, int Count, DateTime Last, bool Wireless)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sorted.Count; i++)
        {
            var gone = sorted[i];
            if (gone.Arrived) continue;

            // Some devices go in several parts: one drop, however many events it made.
            result.TryGetValue(gone.Key, out var known);
            if (known.Count > 0 && gone.Time - known.Last <= Together) continue;

            var going = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var j = i; j >= 0 && gone.Time - sorted[j].Time <= Together; j--)
                if (!sorted[j].Arrived) going.Add(sorted[j].Key);
            for (var j = i + 1; j < sorted.Count && sorted[j].Time - gone.Time <= Together; j++)
                if (!sorted[j].Arrived) going.Add(sorted[j].Key);
            if (going.Count >= 3) continue;

            var gap = gone.Wireless ? WirelessGap : UsbGap;
            DeviceEvent? back = null;
            for (var j = i + 1; back is null && j < sorted.Count && sorted[j].Time - gone.Time <= gap; j++)
                if (sorted[j].Arrived && sorted[j].Key.Equals(gone.Key, StringComparison.OrdinalIgnoreCase)) back = sorted[j];
            if (back is null) continue;

            // A device that is gone often has no name left to read; the one coming back does.
            var name = gone.Name.Length > 0 ? gone.Name : back.Name;
            result[gone.Key] = (name.Length > 0 ? name : known.Name ?? "", known.Count + 1, gone.Time, gone.Wireless);
        }
        return result.Where(r => r.Value.Count >= 3)
            .Select(r => new DeviceDrops(r.Key, r.Value.Name.Length > 0 ? r.Value.Name : Loc.T("device.unknown"), r.Value.Count, r.Value.Last, r.Value.Wireless))
            .ToList();
    }

    /// <summary>Adds what the history saw to a new snapshot.</summary>
    public static void AddDrops(HistoryStore store, SystemSnapshot snapshot)
    {
        var now = DateTime.Now;
        snapshot.DeviceDrops = Drops(store.DeviceEvents(now.AddDays(-DropDays), now.AddMinutes(1))).ToList();
        snapshot.Wifi = store.WifiSignal(now.AddDays(-DropDays), now.AddMinutes(1), WifiWeakSignal);
    }
}
