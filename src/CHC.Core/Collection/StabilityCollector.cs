using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CHC.Core.Diagnostics;
using CHC.Core.Models;

namespace CHC.Core.Collection;

/// <summary>
/// Reads the System and Application event logs for everything that explains freezes,
/// crashes and sudden shutdowns: how the PC went down, blue screen codes, overheating,
/// graphics driver resets, hardware errors, disk and network adapter resets, sleep,
/// low memory, program crashes and failed updates.
/// </summary>
internal static partial class StabilityCollector
{
    private const int MaxRecentEvents = 40;

    public static StabilityInfo Collect()
    {
        var info = new StabilityInfo();
        var recent = new List<EventEntry>();
        var period = TimeSpan.FromDays(StabilityInfo.PeriodDays);
        try
        {
            // Kernel-Power 41 is written at the next start and says how the PC went down.
            foreach (var r in Read("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41", period))
            {
                var d = Data(r);
                var code = ParseNumber(d.GetValueOrDefault("BugcheckCode"));
                var kind = code != 0 ? ShutdownKind.Crash
                    : IsTrue(d, "LongPowerButtonPressDetected") || ParseNumber(d.GetValueOrDefault("PowerButtonTimestamp")) != 0 ? ShutdownKind.ForcedOff
                    : ParseNumber(d.GetValueOrDefault("SleepInProgress")) != 0 || IsTrue(d, "ConnectedStandbyInProgress") ? ShutdownKind.Sleep
                    : ShutdownKind.PowerLoss;
                info.Shutdowns.Add(new ShutdownRecord { RestartTime = Time(r), Kind = kind, BugcheckCode = (uint)code });
                AddRecent(recent, r, "shutdown");
            }

            foreach (var r in Read("System", "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001", period))
            {
                info.Bugchecks.Add(new BugcheckRecord { Time = Time(r), Code = FirstStopCode(Data(r).Values) });
                AddRecent(recent, r, "bluescreen");
            }

            foreach (var r in Read("System", "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3)", period))
            {
                var fatal = r.Level is 1 or 2;
                info.Whea.Add(new WheaRecord { Time = Time(r), Fatal = fatal, Component = Catalog.WheaComponent(r.Id) });
                AddRecent(recent, r, fatal ? "whea" : "whea-corrected");
            }
            info.WheaErrors = info.Whea.Count(w => w.Fatal);
            info.WheaCorrected = info.Whea.Count(w => !w.Fatal);

            // "Display driver stopped responding and has recovered" plus errors from the GPU drivers themselves.
            var gpuTimes = new List<DateTime>();
            foreach (var r in Read("System",
                "(Provider[@Name='Display'] and EventID=4101) or ((Provider[@Name='nvlddmkm'] or Provider[@Name='amdkmdag'] or Provider[@Name='amdwddmg'] or Provider[@Name='igfx'] or Provider[@Name='igfxn']) and (Level=1 or Level=2))",
                period))
            {
                gpuTimes.Add(Time(r));
                AddRecent(recent, r, "gpu");
            }
            info.GpuDriverEvents = CollapseBursts(gpuTimes, TimeSpan.FromSeconds(60));

            foreach (var r in Read("System", "Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153)", period))
            {
                info.DiskErrors++;
                AddRecent(recent, r, "disk");
            }
            foreach (var r in Read("System", "(Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs']) and EventID=55", period))
            {
                info.FileSystemErrors++;
                AddRecent(recent, r, "ntfs");
            }

            // "Reset to device": the disk stopped answering and the controller had to reset it (often seen as a freeze).
            var resetTimes = new List<DateTime>();
            foreach (var r in Read("System",
                "(Provider[@Name='stornvme'] or Provider[@Name='storahci'] or Provider[@Name='iaStorAC'] or Provider[@Name='iaStorAVC'] or Provider[@Name='iaStorA'] or Provider[@Name='secnvme']) and EventID=129",
                period))
            {
                resetTimes.Add(Time(r));
                AddRecent(recent, r, "storage-reset");
            }
            info.StorageResets = CollapseBursts(resetTimes, TimeSpan.FromSeconds(30));

            info.SleepEntries = Read("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=42", period).Select(Time).ToList();

            // 86: shut down because a thermal zone reached its critical temperature; 88: hibernated for the same reason.
            foreach (var r in Read("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=86 or EventID=88)", period))
            {
                info.ThermalShutdowns.Add(Time(r));
                AddRecent(recent, r, "thermal");
            }

            ReadNetworkResets(info, recent, period);
            info.DnsTimeouts = Read("System", "Provider[@Name='Microsoft-Windows-DNS-Client'] and EventID=1014", period).Select(Time).ToList();
            ReadUpdateFailures(info, period);

            foreach (var r in Read("System", "Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004", period))
            {
                info.LowMemoryEvents.Add(Time(r));
                AddRecent(recent, r, "lowmem");
            }

            ReadMemoryTest(info);
            ReadApplicationLog(info, recent, period);
            FindLastActivity(info, period);
            info.Available = true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            info.Available = info.Shutdowns.Count > 0;
        }

        info.Incidents = IncidentBuilder.Build(info);
        info.UnexpectedShutdowns = info.Shutdowns.Count;
        info.BlueScreens = info.Incidents.Count(i => i.Kind == ShutdownKind.Crash);
        info.RecentEvents = recent.OrderByDescending(e => e.Time).Take(MaxRecentEvents).ToList();
        return info;
    }

    private static void ReadApplicationLog(StabilityInfo info, List<EventEntry> recent, TimeSpan period)
    {
        foreach (var r in Read("Application", "Provider[@Name='Application Error'] and EventID=1000", period))
        {
            var d = Data(r);
            info.AppEvents.Add(new AppEvent { Time = Time(r), App = d.GetValueOrDefault("AppName", d.GetValueOrDefault("#0", "")), Module = d.GetValueOrDefault("ModuleName", d.GetValueOrDefault("#3", "")) });
        }
        foreach (var r in Read("Application", "Provider[@Name='Application Hang'] and EventID=1002", period))
        {
            var d = Data(r);
            info.AppEvents.Add(new AppEvent { Time = Time(r), App = d.GetValueOrDefault("AppName", d.GetValueOrDefault("#0", "")), IsHang = true });
        }
        info.AppEvents.RemoveAll(a => string.IsNullOrWhiteSpace(a.App));

        info.AppProblems = info.AppEvents
            .GroupBy(a => a.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppProblem
            {
                App = g.First().App,
                Crashes = g.Count(a => !a.IsHang),
                Hangs = g.Count(a => a.IsHang),
                Last = g.Max(a => a.Time),
                Module = g.Where(a => !a.IsHang && a.Module.Length > 0).GroupBy(a => a.Module, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(m => m.Count()).Select(m => m.Key).FirstOrDefault() ?? "",
            })
            .OrderByDescending(a => a.Crashes + a.Hangs)
            .ThenByDescending(a => a.Last)
            .ToList();

        // LiveKernelEvent: Windows caught a hardware/driver hang and recovered without a blue screen.
        const string liveKernel = "*[System[Provider[@Name='Windows Error Reporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {0}]] and EventData[Data[@Name='EventName']='LiveKernelEvent']]";
        foreach (var r in ReadXPath("Application", string.Format(CultureInfo.InvariantCulture, liveKernel, (long)period.TotalMilliseconds)))
        {
            var p1 = Data(r).GetValueOrDefault("P1", "");
            if (!uint.TryParse(p1, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) continue;
            // Windows often writes two reports for one hang; keep one per code within two minutes.
            var time = Time(r);
            if (info.LiveKernelEvents.Any(e => e.Code == code && (e.Time - time).Duration() <= TimeSpan.FromMinutes(2))) continue;
            info.LiveKernelEvents.Add(new LiveKernelRecord { Time = time, Code = code });
            AddRecent(recent, r, "livekernel");
        }
    }

    /// <summary>"The network interface has begun resetting": the adapter (or its driver) stopped responding.</summary>
    private static void ReadNetworkResets(StabilityInfo info, List<EventEntry> recent, TimeSpan period)
    {
        var resets = new List<(DateTime Time, string Adapter)>();
        foreach (var r in Read("System", "Provider[@Name='Microsoft-Windows-NDIS'] and EventID=10400", period))
            resets.Add((Time(r), Data(r).GetValueOrDefault("AdapterName", "")));

        // A hung adapter is reset every few seconds until it recovers; one burst is one problem.
        foreach (var adapter in resets.GroupBy(x => x.Adapter, StringComparer.OrdinalIgnoreCase))
        {
            NetworkReset? burst = null;
            var last = DateTime.MinValue;
            foreach (var (time, name) in adapter.OrderBy(x => x.Time))
            {
                var gap = time - last;
                last = time;
                if (burst is not null && gap <= TimeSpan.FromMinutes(1))
                {
                    burst.Resets++;
                    continue;
                }
                burst = new NetworkReset { Time = time, Adapter = name, Resets = 1 };
                info.NetworkResets.Add(burst);
                recent.Add(new EventEntry { Time = time, Source = "Microsoft-Windows-NDIS", EventId = 10400, Level = "Warning", Category = "network" });
            }
        }
        info.NetworkResets.Sort((a, b) => b.Time.CompareTo(a.Time));
    }

    /// <summary>Failed Windows and driver updates. Store app updates fail all the time while the app is open, so they are skipped.</summary>
    private static void ReadUpdateFailures(StabilityInfo info, TimeSpan period)
    {
        foreach (var r in Read("System", "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=20", period))
        {
            var d = Data(r);
            var title = d.GetValueOrDefault("updateTitle", "").Trim();
            if (title.Length == 0 || StoreAppPattern().IsMatch(title)) continue;
            info.UpdateFailures.Add(new UpdateFailure { Time = Time(r), Title = title, ErrorCode = d.GetValueOrDefault("errorCode", "") });
        }
    }

    /// <summary>Store apps are listed by their 12-character product id, e.g. "9NBLGGH4NNS1-Microsoft.DesktopAppInstaller".</summary>
    [GeneratedRegex(@"^[0-9A-Z]{12}-")]
    private static partial Regex StoreAppPattern();

    private static void ReadMemoryTest(StabilityInfo info)
    {
        foreach (var last in Read("System", "Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results']", TimeSpan.FromDays(365)))
        {
            info.MemoryTestDate = Time(last);
            info.MemoryTestFoundErrors = last.Level is 1 or 2;
            return;
        }
    }

    /// <summary>
    /// The shutdown event is written only after the restart, so find the last thing Windows
    /// logged before that start: it tells roughly when the PC froze or lost power.
    /// </summary>
    private static void FindLastActivity(StabilityInfo info, TimeSpan period)
    {
        if (info.Shutdowns.Count == 0) return;
        var boots = new List<DateTime>();
        foreach (var r in Read("System", "Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12", period + TimeSpan.FromDays(1)))
        {
            var start = Data(r).GetValueOrDefault("StartTime");
            boots.Add(DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
                ? utc.ToLocalTime()
                : Time(r));
        }
        foreach (var s in info.Shutdowns)
        {
            var boot = boots.Where(b => b <= s.RestartTime.AddSeconds(5)).DefaultIfEmpty(s.RestartTime).Max();
            var before = boot.AddSeconds(-1);
            var lastSystem = LastEventBefore("System", before);
            var lastApp = LastEventBefore("Application", before);
            s.LastActivity = Max(lastSystem, lastApp);
        }
    }

    /// <summary>The last thing the PC wrote in its main logs before a time; a restart's own first events are just after it.</summary>
    internal static DateTime? LastActivityBefore(DateTime localTime) =>
        Max(LastEventBefore("System", localTime.AddSeconds(-1)), LastEventBefore("Application", localTime.AddSeconds(-1)));

    private static DateTime? LastEventBefore(string log, DateTime localTime)
    {
        var utc = localTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return ReadXPath(log, $"*[System[TimeCreated[@SystemTime < '{utc}']]]").Select(r => (DateTime?)Time(r)).FirstOrDefault();
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Several events within a few seconds describe one reset; count it once.</summary>
    private static List<DateTime> CollapseBursts(IEnumerable<DateTime> times, TimeSpan gap)
    {
        var result = new List<DateTime>();
        foreach (var t in times.OrderBy(t => t))
            if (result.Count == 0 || t - result[^1] > gap) result.Add(t);
        result.Reverse();
        return result;
    }

    internal static IEnumerable<EventRecord> Read(string log, string filter, TimeSpan period) =>
        ReadXPath(log, string.Format(CultureInfo.InvariantCulture, "*[System[({0}) and TimeCreated[timediff(@SystemTime) <= {1}]]]", filter, (long)period.TotalMilliseconds));

    /// <summary>Newest first. Records are read eagerly enough to be disposed by the caller loop.</summary>
    private static IEnumerable<EventRecord> ReadXPath(string log, string xpath)
    {
        EventLogReader reader;
        try { reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true }); }
        catch (EventLogException) { yield break; }
        using (reader)
        {
            while (true)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch (EventLogException) { yield break; }
                if (record is null) yield break;
                using (record) yield return record;
            }
        }
    }

    private static DateTime Time(EventRecord r) => r.TimeCreated ?? DateTime.MinValue;

    /// <summary>EventData as name → value ("#0", "#1"... for unnamed values).</summary>
    internal static Dictionary<string, string> Data(EventRecord r)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var xml = XElement.Parse(r.ToXml());
            var i = 0;
            foreach (var element in xml.Descendants(xml.Name.Namespace + "Data"))
            {
                result.TryAdd((string?)element.Attribute("Name") ?? $"#{i}", element.Value.Trim());
                i++;
            }
        }
        catch (Exception ex) when (ex is XmlException or EventLogException) { }
        return result;
    }

    private static bool IsTrue(Dictionary<string, string> data, string key) =>
        data.TryGetValue(key, out var v) && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

    private static ulong ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : 0
            : ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : 0;
    }

    /// <summary>The BugCheck event text starts with the stop code, e.g. "0x00000116 (0xffff..., ...)".</summary>
    private static uint FirstStopCode(IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            var m = StopCodePattern().Match(v);
            if (m.Success) return uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return 0;
    }

    [GeneratedRegex(@"0x([0-9a-fA-F]{8})\b")]
    private static partial Regex StopCodePattern();

    private static void AddRecent(List<EventEntry> recent, EventRecord r, string category) => recent.Add(new EventEntry
    {
        Time = Time(r),
        Source = r.ProviderName ?? "",
        EventId = r.Id,
        Level = r.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", 4 => "Information", _ => "" },
        Category = category,
    });
}
