using CHC.Core.Models;

namespace CHC.Core.Diagnostics;

/// <summary>
/// Turns the raw event lists into incidents (one per freeze, crash or sudden shutdown) and
/// decides the most likely cause of each from the clues logged shortly before it.
/// </summary>
public static class IncidentBuilder
{
    public const int MaxIncidents = 25;
    private const int MaxClues = 6;

    /// <summary>How far before the last sign of life we look for clues.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    public static List<Incident> Build(StabilityInfo s)
    {
        var incidents = new List<Incident>();
        var used = new HashSet<BugcheckRecord>();

        foreach (var shutdown in s.Shutdowns.OrderByDescending(x => x.RestartTime))
        {
            var kind = shutdown.Kind;
            var code = shutdown.BugcheckCode;

            // A blue screen writes its own event close to the restart; merge the two.
            var bugcheck = s.Bugchecks.FirstOrDefault(b => !used.Contains(b) && Math.Abs((b.Time - shutdown.RestartTime).TotalMinutes) <= 15);
            if (bugcheck is not null)
            {
                used.Add(bugcheck);
                kind = ShutdownKind.Crash;
                if (code == 0) code = bugcheck.Code;
            }
            incidents.Add(Make(s, kind, code, shutdown.LastActivity ?? shutdown.RestartTime, shutdown.RestartTime));
        }

        foreach (var bugcheck in s.Bugchecks.Where(b => !used.Contains(b)))
            incidents.Add(Make(s, ShutdownKind.Crash, bugcheck.Code, bugcheck.Time, bugcheck.Time));

        // An overheating shutdown is often an orderly one (no Kernel-Power 41), so it may not belong to any incident yet.
        var explained = incidents.SelectMany(i => i.Clues).Where(c => c.Kind == "thermal").Select(c => c.Time).ToHashSet();
        foreach (var time in s.ThermalShutdowns.Where(t => !explained.Contains(t)))
            incidents.Add(Make(s, ShutdownKind.Thermal, 0, time, time));

        return incidents.OrderByDescending(i => i.RestartTime).Take(MaxIncidents).ToList();
    }

    private static Incident Make(StabilityInfo s, ShutdownKind kind, uint code, DateTime time, DateTime restart)
    {
        var from = time - Window;
        bool In(DateTime t) => t >= from && t <= restart;

        var clues = new List<Clue>();
        // Windows may log the thermal event only while starting again, so look a little past the restart too.
        clues.AddRange(s.ThermalShutdowns.Where(t => t >= from && t <= restart.AddMinutes(15)).Select(t => new Clue { Kind = "thermal", Time = t, Hint = "heat" }));
        clues.AddRange(s.GpuDriverEvents.Where(In).Select(t => new Clue { Kind = "gpu", Time = t, Hint = "gpu" }));
        clues.AddRange(s.LiveKernelEvents.Where(e => In(e.Time)).Select(e =>
        {
            var info = Catalog.LiveKernelEvent(e.Code);
            return new Clue { Kind = "livekernel", Time = e.Time, Detail = info.Name, Hint = info.Cause };
        }));
        clues.AddRange(s.Whea.Where(w => In(w.Time)).Select(w => new Clue { Kind = "whea", Time = w.Time, Detail = w.Component, Hint = w.Component == "other" ? "hardware" : w.Component == "pcie" ? "hardware" : w.Component }));
        clues.AddRange(s.StorageResets.Where(In).Select(t => new Clue { Kind = "storage", Time = t, Hint = "storage" }));
        clues.AddRange(s.LowMemoryEvents.Where(In).Select(t => new Clue { Kind = "lowmem", Time = t, Hint = "lowmemory" }));
        clues.AddRange(s.SleepEntries.Where(In).Select(t => new Clue { Kind = "sleep", Time = t, Hint = "sleep" }));
        clues.AddRange(s.NetworkResets.Where(n => In(n.Time)).Select(n => new Clue { Kind = "network", Time = n.Time, Detail = n.Adapter, Hint = "driver" }));
        clues.AddRange(s.AppEvents.Where(a => In(a.Time)).Select(a =>
        {
            var vendor = a.IsHang ? null : Catalog.GraphicsDriverVendor(a.Module);
            return new Clue
            {
                Kind = a.IsHang ? "apphang" : "appcrash",
                Time = a.Time,
                Detail = a.App,
                Hint = vendor is null ? "" : "gpu",
            };
        }));

        // One clue per kind and detail, newest first; an overheating record is never cut off.
        clues = clues
            .GroupBy(c => (c.Kind, c.Detail))
            .Select(g => g.OrderByDescending(c => c.Time).First())
            .OrderByDescending(c => c.Kind == "thermal")
            .ThenByDescending(c => c.Time)
            .Take(MaxClues)
            .OrderByDescending(c => c.Time)
            .ToList();

        // Nothing logged for a long time before the restart: the PC was frozen, or off, for that long.
        var silence = restart - time;
        if (kind is ShutdownKind.PowerLoss or ShutdownKind.ForcedOff && silence >= TimeSpan.FromMinutes(5))
            clues.Add(new Clue { Kind = "gap", Time = time, Detail = ((int)silence.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) });

        return new Incident
        {
            Time = time,
            RestartTime = restart,
            Kind = kind,
            BugcheckCode = code,
            Cause = Classify(kind, code, clues),
            Clues = clues,
        };
    }

    /// <summary>Most specific evidence wins: overheating, a known stop code, then the clues, then the way the PC went down.</summary>
    public static string Classify(ShutdownKind kind, uint code, IReadOnlyList<Clue> clues)
    {
        bool Has(string hint) => clues.Any(c => c.Hint == hint);
        if (kind == ShutdownKind.Thermal || Has("heat")) return "heat";

        var fromCode = code != 0 ? Catalog.Bugcheck(code).Cause : null;
        if (fromCode is not null and not "unknown" and not "driver") return fromCode;

        if (Has("gpu")) return "gpu";
        if (Has("cpu")) return "cpu";
        if (Has("memory")) return "memory";
        if (Has("hardware")) return "hardware";
        if (Has("storage")) return "storage";
        if (Has("lowmemory")) return "lowmemory";
        if (kind == ShutdownKind.Sleep || Has("sleep")) return "sleep";
        if (fromCode == "driver" || Has("driver")) return "driver";
        if (Has("system")) return "system";

        // No crash record and no clue: a power problem, or a freeze so complete that Windows could not log it.
        return kind is ShutdownKind.PowerLoss or ShutdownKind.ForcedOff ? "silent" : "unknown";
    }

    /// <summary>The cause most incidents point to (ignoring "unknown"), with how many did.</summary>
    public static (string Cause, int Count)? MainCause(IReadOnlyList<Incident> incidents)
    {
        var top = incidents
            .Where(i => i.Cause != "unknown")
            .GroupBy(i => i.Cause)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        return top is null ? null : (top.Key, top.Count());
    }
}
