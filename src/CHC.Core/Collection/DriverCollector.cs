using CHC.Core.Models;

namespace CHC.Core.Collection;

/// <summary>
/// The drivers in use, from Windows' own list: the ones that came from a maker rather than with Windows, and every
/// Intel device, whose driver can be compared with Intel's list of current ones.
/// </summary>
public static class DriverCollector
{
    public static List<InstalledDriver> Collect()
    {
        var seen = new HashSet<(string, string)>();
        var list = new List<InstalledDriver>();
        foreach (var d in Wmi.Query("SELECT DeviceName, DeviceID, DriverVersion, DriverDate, DeviceClass, DriverProviderName FROM Win32_PnPSignedDriver"))
        {
            var name = d.Str("DeviceName");
            var version = d.Str("DriverVersion");
            var provider = d.Str("DriverProviderName");
            var hardwareId = HardwareId(d.Str("DeviceID"));
            if (name.Length == 0 || version.Length == 0) continue;
            var fromWindows = provider.Length == 0 || provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);
            if (fromWindows && !Updates.IntelDrivers.IsIntel(hardwareId)) continue;
            if (!seen.Add((name, version))) continue;
            list.Add(new InstalledDriver
            {
                Device = name,
                Class = d.Str("DeviceClass").ToUpperInvariant(),
                Provider = provider,
                Version = version,
                Date = d.Date("DriverDate"),
                HardwareId = hardwareId,
            });
        }
        return list.OrderBy(d => d.Class, StringComparer.Ordinal).ThenBy(d => d.Device, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// "PCI\VEN_8086&amp;DEV_56B0&amp;SUBSYS_...\3&amp;11583659&amp;0&amp;10" without its last part: that names this very
    /// device on this very PC, and for a USB device it can be a serial number.
    /// </summary>
    private static string HardwareId(string deviceId)
    {
        var at = deviceId.LastIndexOf('\\');
        return (at > 0 ? deviceId[..at] : deviceId).ToUpperInvariant();
    }
}
