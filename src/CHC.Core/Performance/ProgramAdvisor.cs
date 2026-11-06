using CHC.Core.Diagnostics;
using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Models;
using CHC.Core.Presentation;

namespace CHC.Core.Performance;

/// <summary>
/// What the record of each program's use says: programs that keep working in the background, what fills the memory,
/// and what the PC was doing right before a freeze or crash.
/// </summary>
public static class ProgramAdvisor
{
    private const ComponentKind K = ComponentKind.Performance;

    /// <summary>The week behind the advice: long enough to be typical, short enough to follow a change.</summary>
    public const int AdviceDays = 7;

    /// <summary>A program has to have run this long before its average means anything.</summary>
    private static readonly double MinSeconds = TimeSpan.FromHours(1).TotalSeconds;

    /// <summary>"Google Chrome", or the exe's name without ".exe" when it has no name of its own.</summary>
    public static string NameOf(ProgramUsage p) => p.Title is { Length: > 1 } title ? title : Path.GetFileNameWithoutExtension(p.Name);

    /// <summary>Whether it is part of Windows, and whether it is mostly in front or behind.</summary>
    public static string RoleKey(ProgramUsage p) =>
        Catalog.IsWindowsComponent(p.Name) ? "programs.role.windows"
        : p.FrontSeconds >= p.Seconds * 0.5 ? "programs.role.front"
        : p.FrontSeconds > 0 ? "programs.role.both"
        : "programs.role.background";

    /// <summary>Not the user's to close: part of Windows, or CHC itself.</summary>
    public static bool IsOwnProgram(ProgramUsage p) =>
        !Catalog.IsWindowsComponent(p.Name) && !p.Name.Equals("CHC.exe", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<Finding> Advise(HistoryStore store, SystemSnapshot snapshot)
    {
        var now = DateTime.Now;
        var from = now.AddDays(-AdviceDays);
        var to = now.AddMinutes(1);
        var programs = store.Programs(from, to);
        var list = new List<Finding>();
        // Mostly behind other windows: otherwise the work is what the user is doing with it.
        var background = programs.Where(p => IsOwnProgram(p) && p.Seconds >= MinSeconds && p.FrontSeconds < p.Seconds * 0.2).ToList();

        foreach (var p in background.Where(p => p.CpuAverage >= 5).OrderByDescending(p => p.CpuAverage).Take(2))
            list.Add(new(K, p.CpuAverage >= 15 ? Severity.Warning : Severity.Info, "program.background.cpu", NameOf(p), p.CpuAverage, Fmt.Duration(TimeSpan.FromSeconds(p.Seconds))));

        foreach (var p in programs.Where(p => Catalog.IsWindowsComponent(p.Name) && p.Seconds >= MinSeconds && p.CpuAverage >= 10 && p.Name != "System").OrderByDescending(p => p.CpuAverage).Take(1))
            list.Add(new(K, Severity.Info, "program.windows.busy", NameOf(p), p.CpuAverage));

        foreach (var p in background.Where(p => p.GpuAverage >= 8).OrderByDescending(p => p.GpuAverage).Take(2))
            list.Add(new(K, Severity.Info, p.ComputeAverage >= p.GpuAverage / 2 ? "program.gpu.compute" : "program.gpu.background", NameOf(p), p.GpuAverage));

        var days = Math.Max(1, store.RecordedDays(from, to));
        foreach (var p in background.Where(p => p.WriteBytes / days >= 30L * 1024 * 1024 * 1024).OrderByDescending(p => p.WriteBytes).Take(1))
            list.Add(new(K, Severity.Info, "program.disk.writes", NameOf(p), Fmt.Bytes((ulong)(p.WriteBytes / days), 0)));

        // Memory often full: name who has it, so "close some programs" says which.
        if (store.ShareAbove(HistoryMetric.RamLoad, 85, from, to) is >= 20 and var share)
        {
            var top = programs.Where(p => IsOwnProgram(p) && p.Seconds >= MinSeconds / 2).OrderByDescending(p => p.MemoryAverageMb).Take(3).ToList();
            if (top.Count > 0)
                list.Add(new(K, share >= 50 ? Severity.Warning : Severity.Info, "program.memory.pressure", share, MemoryList(top), Fmt.Bytes(snapshot.Memory.TotalBytes, 0)));
        }
        return list.OrderByDescending(f => f.Severity).ToList();
    }

    /// <summary>"Google Chrome (3.4 GB), Discord (620 MB)".</summary>
    public static string MemoryList(IEnumerable<ProgramUsage> programs) =>
        string.Join(", ", programs.Select(p => $"{NameOf(p)} ({Fmt.Bytes((ulong)(p.MemoryAverageMb * 1024 * 1024), 1)})"));

    /// <summary>Adds what the history saw right before each freeze or crash, for its reasoning and for the tables.</summary>
    public static void AddMoments(HistoryStore store, SystemSnapshot snapshot)
    {
        foreach (var incident in snapshot.Stability.Incidents)
            incident.Moment = store.MomentBefore(incident.Time);
    }
}
