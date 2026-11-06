using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Models;
using CHC.Core.Past;
using CHC.Core.Performance;
using CHC.Core.Timeline;

namespace CHC.Core.Reporting;

/// <summary>What the report tells about how the PC is used: from CHC's own history, and from Windows' records where CHC wasn't looking.</summary>
public sealed record ReportUsage(
    IReadOnlyList<GameReport> Games,
    IReadOnlyList<ProgramUsage> Programs,
    IReadOnlyList<PastProgramRow> NonChcPrograms,
    IReadOnlyList<Finding> ProgramFindings,
    IReadOnlyList<PastDay> OnMinutes,
    IReadOnlyList<PastDay> RecordedMinutes,
    DateTime FirstDay,
    IReadOnlyList<SystemChange> Changes)
{
    /// <summary>How far back the report looks at when the PC was on, and at what changed.</summary>
    public const int Days = 60;

    /// <summary>The period behind the list of programs.</summary>
    public const int ProgramDays = 30;

    private const int MaxPrograms = 12;
    private const int MaxNonChcPrograms = 8;
    private const int MaxChanges = 12;

    /// <summary>A program that only ran for moments isn't worth a line.</summary>
    private const double MinNonChcSeconds = 3600;

    public static ReportUsage Build(HistoryStore store, SystemSnapshot snapshot, IReadOnlyList<PowerSpan> power)
    {
        var now = DateTime.Now;
        var to = now.AddMinutes(1);
        var first = now.Date.AddDays(-(Days - 1));
        var programsFrom = now.AddDays(-ProgramDays);
        var programs = store.Programs(programsFrom, to);
        var seen = programs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nonChc = PastAnalyzer.Programs(store.PastBlocks(programsFrom, to), programsFrom, to)
            .Where(r => !seen.Contains(r.Exe) && r.Running >= MinNonChcSeconds)
            .OrderByDescending(r => r.Running)
            .Take(MaxNonChcPrograms)
            .ToList();
        return new ReportUsage(
            GameAdvisor.Build(store, snapshot),
            programs.Where(ProgramAdvisor.IsOwnProgram).OrderByDescending(p => p.CpuAverage).Take(MaxPrograms).ToList(),
            nonChc,
            ProgramAdvisor.Advise(store, snapshot),
            PastAnalyzer.MinutesOn(power, first, Days),
            store.RecordedMinutesDaily(first, to),
            first,
            store.Changes(first, to).OrderByDescending(c => c.Time).Take(MaxChanges).ToList());
    }
}
