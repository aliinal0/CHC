using CHC.Core.Localization;

namespace CHC.Core.Presentation;

/// <summary>Culture aware formatting for values shown to the user.</summary>
public static class Fmt
{
    public const string Dash = "—";

    private static IFormatProvider C => Loc.Instance.Culture;

    public static string Bytes(ulong bytes, int decimals = 1)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 ? "0" : "0." + new string('#', decimals);
        return value.ToString(format, C) + " " + units[unit];
    }

    /// <summary>Disk makers use decimal units (1 TB = 10^12 bytes); show what is printed on the box.</summary>
    public static string DiskSize(ulong bytes) =>
        bytes >= 1_000_000_000_000 ? (bytes / 1e12).ToString("0.##", C) + " TB" : (bytes / 1e9).ToString("0", C) + " GB";

    public static string Gigabytes(ulong gb) =>
        gb >= 1000 ? (gb / 1000.0).ToString("0.##", C) + " TB" : gb.ToString("N0", C) + " GB";

    public static string Temp(double? celsius) => celsius is { } c ? c.ToString("0", C) + " °C" : Dash;

    /// <summary>Follows the language's convention: "16%" in English, "%16" in Turkish.</summary>
    public static string Percent(double? value, int decimals = 0)
    {
        if (value is not { } v) return Dash;
        var number = v.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), C);
        return Loc.Instance.Culture.NumberFormat.PercentPositivePattern switch
        {
            1 => number + "%",
            2 => "%" + number,
            3 => "% " + number,
            _ => number + " %",
        };
    }

    public static string Number(double? value, string format = "N0") => value is { } v ? v.ToString(format, C) : Dash;

    public static string Watts(double? watts) => watts is { } w ? w.ToString("0.#", C) + " W" : Dash;

    public static string MHz(double? mhz) =>
        mhz is not { } m || m <= 0 ? Dash : m >= 1000 ? (m / 1000).ToString("0.00", C) + " GHz" : m.ToString("0", C) + " MHz";

    public static string Date(DateTime? date) => date is { } d ? d.ToString("d", C) : Dash;

    public static string DateAndTime(DateTime? date) => date is { } d ? d.ToString("g", C) : Dash;

    public static string YesNo(bool? value) => value switch { true => Loc.T("common.yes"), false => Loc.T("common.no"), _ => Dash };

    public static string OnOff(bool? value) => value switch { true => Loc.T("common.on"), false => Loc.T("common.off"), _ => Dash };

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } s) return Dash;
        if (s.TotalDays >= 1) return Loc.T("common.duration.days", (int)s.TotalDays, s.Hours);
        if (s.TotalHours >= 1) return Loc.T("common.duration.hours", (int)s.TotalHours, s.Minutes);
        return Loc.T("common.duration.minutes", Math.Max(1, (int)s.TotalMinutes));
    }

    public static string Hours(ulong? hours) =>
        hours is { } h ? Loc.T("common.hours", h) + (h >= 24 ? $" ({Loc.T("common.days", h / 24)})" : "") : Dash;

    public static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? Dash : value.Trim();

    /// <summary>Hides a serial number completely in reports that will be shared.</summary>
    public static string Mask(string? serial) =>
        string.IsNullOrWhiteSpace(serial) ? Dash : "••••••••";

    /// <summary>Short form for headline tiles ("54 °C", "3.71 GHz"); unreadable zeros become a dash.</summary>
    public static string Compact(string? type, float? value)
    {
        if (value is not { } v || float.IsNaN(v)) return Dash;
        return type switch
        {
            "Temperature" => v > 0 ? Temp(v) : Dash,
            "Load" or "Level" or "Control" => Percent(v),
            "Clock" => v > 0 ? MHz(v) : Dash,
            "Power" => v > 0 ? Watts(v) : Dash,
            "Data" => Bytes((ulong)(Math.Max(0, v) * 1024 * 1024 * 1024)),
            "SmallData" => Bytes((ulong)(Math.Max(0, v) * 1024 * 1024)),
            _ => Sensor(type ?? "", v),
        };
    }

    /// <summary>
    /// A PnP device id ends with an instance id that is unique to this computer
    /// (PCI\VEN_10DE&amp;DEV_...\&lt;instance&gt;); only that last part is passed to <paramref name="serial"/>.
    /// </summary>
    public static string MaskInstance(string? deviceId, Func<string?, string> serial)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return Dash;
        var cut = deviceId.LastIndexOf('\\');
        return cut < 0 ? serial(deviceId) : deviceId[..(cut + 1)] + serial(deviceId[(cut + 1)..]);
    }

    /// <summary>Formats a raw LibreHardwareMonitor sensor value with its unit.</summary>
    public static string Sensor(string type, float? value)
    {
        if (value is not { } v) return Dash;
        return type switch
        {
            "Temperature" => v.ToString("0.0", C) + " °C",
            "Load" or "Control" or "Level" or "Humidity" => Percent(v, 1),
            "Clock" => v.ToString("0", C) + " MHz",
            "Voltage" => v.ToString("0.000", C) + " V",
            "Current" => v.ToString("0.000", C) + " A",
            "Power" => v.ToString("0.0", C) + " W",
            "Fan" => v.ToString("0", C) + " RPM",
            "Flow" => v.ToString("0.0", C) + " L/h",
            "Frequency" => v.ToString("0", C) + " Hz",
            "Data" => v.ToString("0.00", C) + " GB",
            "SmallData" => v.ToString("0", C) + " MB",
            "Throughput" => Bytes((ulong)Math.Max(0, v)) + "/s",
            "Energy" => v.ToString("0", C) + " mWh",
            "Noise" => v.ToString("0", C) + " dBA",
            "TimeSpan" => Duration(TimeSpan.FromSeconds(v)),
            "Timing" => v.ToString("0.0", C) + " ns",
            _ => v.ToString("0.##", C),
        };
    }
}
