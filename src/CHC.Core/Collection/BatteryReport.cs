using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using CHC.Core.Models;

namespace CHC.Core.Collection;

/// <summary>
/// Windows records the battery's full charge capacity every day (older days merged into weeks).
/// "powercfg /batteryreport /xml" writes that history; only the capacity numbers are read from it.
/// </summary>
internal static class BatteryReport
{
    public static List<BatteryHistoryPoint> ReadHistory()
    {
        var file = Path.Combine(Path.GetTempPath(), $"chc-battery-{Environment.ProcessId}.xml");
        try
        {
            var start = new ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /output \"{file}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(start))
            {
                if (process is null) return [];
                process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(20_000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    return [];
                }
            }
            return File.Exists(file) ? Parse(XDocument.Load(file)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Xml.XmlException)
        {
            return [];
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>History entries, oldest first, cut to the battery fitted now (a new battery restarts the history).</summary>
    public static List<BatteryHistoryPoint> Parse(XDocument report)
    {
        var points = new List<BatteryHistoryPoint>();
        foreach (var entry in report.Descendants().Where(e => e.Name.LocalName == "HistoryEntry"))
        {
            var date = Date(Value(entry, "LocalStartDate") ?? Value(entry, "StartDate"));
            var full = Number(Value(entry, "FullChargeCapacity"));
            var design = Number(Value(entry, "DesignCapacity"));
            if (date is null || full is not > 0 || design is not > 0) continue;
            points.Add(new BatteryHistoryPoint(date.Value, full.Value, design.Value, Number(Value(entry, "CycleCount")) is > 0 and var c ? c : null));
        }
        points.Sort((a, b) => a.Date.CompareTo(b.Date));
        var current = points.LastOrDefault()?.DesignMWh;
        var since = points.FindLastIndex(p => p.DesignMWh != current);
        return since >= 0 ? points.Skip(since + 1).ToList() : points;
    }

    /// <summary>Reads an attribute or, failing that, a child element of the same name.</summary>
    private static string? Value(XElement entry, string name) =>
        entry.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value
        ?? entry.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static uint? Number(string? text) =>
        uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DateTime? Date(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
}
