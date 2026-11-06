using System.Globalization;
using CHC.Core.Devices;

namespace CHC.Core.Collection;

/// <summary>What Windows logged about devices that failed to start and Wi-Fi that kept dropping. It reads no network names.</summary>
internal static class DeviceLogCollector
{
    private const string PnpLog = "Microsoft-Windows-Kernel-PnP/Configuration";
    private const string WifiLog = "Microsoft-Windows-WLAN-AutoConfig/Operational";

    // Connected, failed to connect, disconnected.
    private const int WifiConnected = 8001;
    private const int WifiFailed = 8002;
    private const int WifiDisconnected = 8003;

    public static DeviceLogInfo Collect()
    {
        var info = new DeviceLogInfo { StartFailures = StartFailures() };
        (info.WifiDrops, info.WifiFailures) = Wifi();
        return info;
    }

    /// <summary>Devices Windows failed to start, apart from those that were merely gone again by then.</summary>
    private static List<DeviceStartFailure> StartFailures()
    {
        var byDevice = new Dictionary<string, (int Count, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in StabilityCollector.Read(PnpLog, "EventID=411", TimeSpan.FromDays(DeviceAdvisor.LogDays)))
        {
            var data = StabilityCollector.Data(record);
            if (!data.TryGetValue("DeviceInstanceId", out var id) || !IsPeripheral(id)) continue;
            if (data.TryGetValue("Problem", out var problem) && IsGoneAgain(problem)) continue;
            byDevice.TryGetValue(id, out var known);
            var time = record.TimeCreated ?? DateTime.MinValue;
            byDevice[id] = (known.Count + 1, time > known.Last ? time : known.Last);
        }
        return byDevice
            .GroupBy(d => NameOf(d.Key))
            .Select(g => new DeviceStartFailure(g.Key, g.Sum(d => d.Value.Count), g.Max(d => d.Value.Last)))
            .OrderByDescending(f => f.Count)
            .ToList();
    }

    private static bool IsPeripheral(string id) =>
        id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);

    private static bool IsGoneAgain(string problem)
    {
        var text = problem.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? problem[2..] : problem;
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) && code is 24 or 45 or 47;
    }

    /// <summary>The device itself, not the numbered part of it the event names.</summary>
    private static string NameOf(string id)
    {
        var device = id;
        var current = id;
        for (var i = 0; i < 5 && Native.ParentDevice(current) is { } parent; i++)
        {
            current = parent;
            if (current.StartsWith(@"USB\VID", StringComparison.OrdinalIgnoreCase) || current.StartsWith(@"BTHENUM\DEV", StringComparison.OrdinalIgnoreCase) || current.StartsWith(@"BTHLE\DEV", StringComparison.OrdinalIgnoreCase))
                device = current;
        }
        return Native.BusReportedName(device) ?? Native.DeviceName(device) ?? Native.DeviceName(id) ?? "";
    }

    /// <summary>
    /// A disconnect the connection comes back from within moments wasn't the user switching off, and wasn't the PC going to sleep.
    /// Windows' reasons are worded in the user's language and many in number; this needs neither.
    /// </summary>
    private static (int Drops, int Failures) Wifi()
    {
        var events = new List<(DateTime Time, int Id)>();
        var filter = string.Create(CultureInfo.InvariantCulture, $"EventID={WifiConnected} or EventID={WifiFailed} or EventID={WifiDisconnected}");
        foreach (var record in StabilityCollector.Read(WifiLog, filter, TimeSpan.FromDays(DeviceAdvisor.DropDays)))
            events.Add((record.TimeCreated ?? DateTime.MinValue, record.Id));
        return CountWifi(events);
    }

    internal static (int Drops, int Failures) CountWifi(List<(DateTime Time, int Id)> events)
    {
        events = events.OrderBy(e => e.Time).ToList();
        var drops = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Id != WifiDisconnected) continue;
            for (var j = i + 1; j < events.Count && events[j].Time - events[i].Time <= DeviceAdvisor.WifiBack; j++)
            {
                if (events[j].Id != WifiConnected) continue;
                drops++;
                break;
            }
        }
        return (drops, events.Count(e => e.Id == WifiFailed));
    }
}
