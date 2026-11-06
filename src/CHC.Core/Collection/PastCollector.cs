using System.Xml.Linq;
using CHC.Core.Models;
using CHC.Core.Past;
using Microsoft.Win32;

namespace CHC.Core.Collection;

/// <summary>When Windows ran out of memory, and the programs it named as using the most.</summary>
public sealed record LowMemoryEvent(DateTime Time, IReadOnlyList<string> Programs);

/// <summary>What Windows' logs remember, a good while back and without administrator rights.</summary>
public sealed class PastEvents
{
    public List<AppEvent> AppEvents { get; set; } = [];
    public List<LowMemoryEvent> LowMemory { get; set; } = [];
}

/// <summary>Reads Windows' logs and its list of games. Nothing in here needs CHC to have been running at the time.</summary>
public static class PastCollector
{
    private const string PowerFilter =
        "(Provider[@Name='Microsoft-Windows-Kernel-General'] and (EventID=12 or EventID=13))" +
        " or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=506 or EventID=507))" +
        " or (Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)";

    public static PastEvents Events(int days)
    {
        var period = TimeSpan.FromDays(days);
        var events = new PastEvents();
        foreach (var r in StabilityCollector.Read("Application", "Provider[@Name='Application Error'] and EventID=1000", period))
        {
            var d = StabilityCollector.Data(r);
            events.AppEvents.Add(new AppEvent { Time = r.TimeCreated ?? DateTime.MinValue, App = d.GetValueOrDefault("AppName", d.GetValueOrDefault("#0", "")), Module = d.GetValueOrDefault("ModuleName", d.GetValueOrDefault("#3", "")) });
        }
        foreach (var r in StabilityCollector.Read("Application", "Provider[@Name='Application Hang'] and EventID=1002", period))
        {
            var d = StabilityCollector.Data(r);
            events.AppEvents.Add(new AppEvent { Time = r.TimeCreated ?? DateTime.MinValue, App = d.GetValueOrDefault("AppName", d.GetValueOrDefault("#0", "")), IsHang = true });
        }
        foreach (var r in StabilityCollector.Read("System", "Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004", period))
            events.LowMemory.Add(new LowMemoryEvent(r.TimeCreated ?? DateTime.MinValue, ProgramsIn(r.ToXml())));
        return events;
    }

    /// <summary>The event lists the biggest users of memory under ProcessInfo, each with its name.</summary>
    internal static List<string> ProgramsIn(string xml)
    {
        var programs = new List<string>();
        var root = XElement.Parse(xml);
        var info = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProcessInfo");
        if (info is null) return programs;
        foreach (var process in info.Elements())
        {
            var name = process.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) programs.Add(Path.GetFileName(name.Trim()));
        }
        return programs;
    }

    /// <summary>When the PC was on and asleep, as far back as the log goes within the period.</summary>
    public static IReadOnlyList<PowerSpan> PowerSpans(int days)
    {
        var events = new List<(DateTime, PowerEvent)>();
        foreach (var r in StabilityCollector.Read("System", PowerFilter, TimeSpan.FromDays(days)))
        {
            var time = r.TimeCreated ?? DateTime.MinValue;
            var kind = (r.ProviderName, r.Id) switch
            {
                ("Microsoft-Windows-Kernel-General", 12) => PowerEvent.Started,
                ("Microsoft-Windows-Kernel-General", 13) => PowerEvent.ShutDown,
                ("Microsoft-Windows-Kernel-Power", 42 or 506) => PowerEvent.Sleeping,
                _ => PowerEvent.Woke,
            };
            events.Add((time, kind));
        }
        return PastAnalyzer.PowerSpansFrom(events, DateTime.Now, StabilityCollector.LastActivityBefore);
    }

    /// <summary>
    /// The program files Windows recognised as games, for its Game Bar and Game Mode, and those it noted reading a game
    /// controller. The first list misses many games; the second holds a few launchers, which the analysis leaves out by name.
    /// </summary>
    public static List<string> KnownGames()
    {
        var games = new List<string>();
        using (var store = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children"))
        {
            foreach (var name in store?.GetSubKeyNames() ?? [])
            {
                using var key = store!.OpenSubKey(name);
                if (key?.GetValue("MatchedExeFullPath") is string path && path.Length > 0) games.Add(Path.GetFileName(path));
            }
        }
        using (var input = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\DirectInput"))
        {
            foreach (var name in input?.GetSubKeyNames() ?? [])
                if (ExeOfDirectInputKey(name) is { } exe) games.Add(exe);
        }
        return games;
    }

    /// <summary>"DCS.EXE_00000000_5A3F" is DCS.exe.</summary>
    internal static string? ExeOfDirectInputKey(string key)
    {
        var at = key.IndexOf(".EXE", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? key[..at] + ".exe" : null;
    }
}
