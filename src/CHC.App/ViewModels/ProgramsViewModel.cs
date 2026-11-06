using System.Collections.ObjectModel;
using CHC.Core.Diagnostics;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Past;
using CHC.Core.Performance;
using CHC.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CHC.App.ViewModels;

/// <summary>What the list is sorted by.</summary>
public enum ProgramSort
{
    Processor,
    Memory,
    Disk,
    Graphics,
}

/// <summary>One program on the Programs page.</summary>
public sealed record ProgramRow(string Title, string Subtitle, string Cpu, string CpuDetail, string Memory, string MemoryDetail, string Disk, string DiskDetail, string Gpu, string GpuDetail, string GpuTip, bool IsNonChc = false);

/// <summary>
/// The "Programs" page: what each program, in front or behind, used of the processor, memory, disk and graphics card,
/// and which programs stand out.
/// </summary>
public sealed partial class ProgramsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _building;
    private int _run;

    public ProgramsViewModel(MainViewModel main)
    {
        _main = main;
        BuildOptions();
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            BuildOptions();
            Refresh();
        };
    }

    public ObservableCollection<Option<int>> Periods { get; } = [];
    public ObservableCollection<Option<ProgramSort>> Sorts { get; } = [];

    [ObservableProperty]
    private Option<int>? _selectedPeriod;

    [ObservableProperty]
    private Option<ProgramSort>? _selectedSort;

    [ObservableProperty]
    private IReadOnlyList<ProgramRow> _rows = [];

    [ObservableProperty]
    private IReadOnlyList<FindingViewModel> _findings = [];

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Why the page has nothing to show: the history is off.</summary>
    [ObservableProperty]
    private string _hint = "";

    partial void OnSelectedPeriodChanged(Option<int>? value)
    {
        if (!_building) Refresh();
    }

    partial void OnSelectedSortChanged(Option<ProgramSort>? value)
    {
        if (!_building) Refresh();
    }

    /// <summary>Reads the period's programs on another thread: a long period adds up many hours.</summary>
    public async void Refresh()
    {
        var run = ++_run;
        var snapshot = _main.Snapshot;
        var history = _main.History;
        var days = SelectedPeriod?.Value ?? 7;
        var sort = SelectedSort?.Value ?? ProgramSort.Processor;
        IReadOnlyList<ProgramUsage> programs = [];
        IReadOnlyList<CHC.Core.Health.Finding> findings = [];
        IReadOnlyList<PastProgramRow> records = [];
        if (history is not null)
        {
            try
            {
                (programs, findings, records) = await Task.Run(() =>
                {
                    var now = DateTime.Now;
                    var from = now.AddDays(-days);
                    var to = now.AddMinutes(1);
                    return (history.Programs(from, to),
                        snapshot is null ? [] : ProgramAdvisor.Advise(history, snapshot),
                        PastAnalyzer.Programs(history.PastBlocks(from, to), from, to));
                });
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // A locked or damaged history file: an empty page, and the next visit tries again.
            }
        }
        if (run != _run) return;

        var recordOf = records.ToDictionary(r => r.Exe, StringComparer.OrdinalIgnoreCase);
        var measured = programs.Select(p => (Cpu: p.CpuAverage, Memory: p.MemoryAverageMb, Disk: (double)(p.ReadBytes + p.WriteBytes), Gpu: p.GpuMax > 0 || p.VramMaxMb > 0 ? p.GpuAverage : double.NaN, Vram: p.VramMaxMb,
            Row: ToRow(p, recordOf.GetValueOrDefault(p.Name))));
        // What only Windows' records know: the programs CHC never saw in this period.
        var seen = programs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = records.Where(r => !seen.Contains(r.Exe) && r.Running >= 60)
            .Select(r => (Cpu: r.CpuWhileRunning ?? 0, Memory: 0.0, Disk: (double)r.DiskBytes, Gpu: double.NaN, Vram: 0.0, Row: ToRow(r)));
        var all = measured.Concat(others);
        var sorted = sort switch
        {
            ProgramSort.Memory => all.OrderByDescending(p => p.Memory),
            ProgramSort.Disk => all.OrderByDescending(p => p.Disk),
            ProgramSort.Graphics => all.Where(p => !double.IsNaN(p.Gpu)).OrderByDescending(p => p.Gpu).ThenByDescending(p => p.Vram),
            _ => all.OrderByDescending(p => p.Cpu),
        };
        Rows = sorted.Take(30).Select(p => p.Row).ToList();
        Findings = findings.Select(f => new FindingViewModel(f)).ToList();
        IsEmpty = Rows.Count == 0;
        var hints = new List<string>();
        if (!_main.RecordHistory) hints.Add(Loc.T("programs.off"));
        if (Rows.Any(r => r.IsNonChc)) hints.Add(Loc.T("programs.nonchc"));
        else if (!_main.IsDemo && _main.RecordsReadAt is null) hints.Add(Loc.T("programs.lookback"));
        Hint = string.Join(" ", hints);
    }

    /// <summary>A program only Windows' records know: how long it ran, and what the exact part of the records adds.</summary>
    private static ProgramRow ToRow(PastProgramRow r) =>
        new(
            PastAnalyzer.NameOf(r.Exe),
            Loc.T("programs.subtitle.nonchc", r.Exe, Fmt.Duration(TimeSpan.FromSeconds(r.Running))),
            r.CpuWhileRunning is { } value ? Fmt.Percent(value, 1) : Fmt.Dash,
            "",
            Fmt.Dash,
            "",
            r.DiskBytes > 0 ? Fmt.Bytes((ulong)r.DiskBytes, 1) : Fmt.Dash,
            "",
            Fmt.Dash,
            "",
            "",
            IsNonChc: true);

    private static ProgramRow ToRow(ProgramUsage p, PastProgramRow? record)
    {
        static string Size(double mb) => Fmt.Bytes((ulong)(mb * 1024 * 1024), mb >= 1024 ? 1 : 0);
        var role = ProgramAdvisor.RoleKey(p);
        var kinds = new List<string>();
        if (p.ComputeAverage >= 1) kinds.Add(Loc.T("programs.gpu.compute", p.ComputeAverage));
        if (p.VideoAverage >= 1) kinds.Add(Loc.T("programs.gpu.video", p.VideoAverage));
        var tip = string.Join(" · ", new[] { p.Adapter }.Concat(kinds).Where(t => !string.IsNullOrEmpty(t)));
        return new ProgramRow(
            ProgramAdvisor.NameOf(p),
            record is { } more && more.Running - p.Seconds >= 3600
                ? Loc.T("programs.subtitle.more", p.Name, Loc.T(role), Fmt.Duration(TimeSpan.FromSeconds(p.Seconds)), Fmt.Duration(TimeSpan.FromSeconds(more.Running - p.Seconds)))
                : Loc.T("programs.subtitle", p.Name, Loc.T(role), Fmt.Duration(TimeSpan.FromSeconds(p.Seconds))),
            Fmt.Percent(p.CpuAverage, 1),
            Loc.T("programs.upto", Fmt.Percent(p.CpuMax)),
            Size(p.MemoryAverageMb),
            Loc.T("programs.upto", Size(p.MemoryMaxMb)),
            Fmt.Bytes((ulong)(p.ReadBytes + p.WriteBytes), 1),
            Loc.T("programs.disk.detail", Fmt.Bytes((ulong)p.ReadBytes, 0), Fmt.Bytes((ulong)p.WriteBytes, 0)),
            p.GpuMax > 0 ? Fmt.Percent(p.GpuAverage, 1) : Fmt.Dash,
            p.VramMaxMb > 0 ? Loc.T("programs.vram", Size(p.VramMaxMb)) : "",
            tip);
    }

    private void BuildOptions()
    {
        var period = SelectedPeriod?.Value ?? 7;
        var sort = SelectedSort?.Value ?? ProgramSort.Processor;
        _building = true;
        try
        {
            Periods.Clear();
            Periods.Add(new(1, Loc.T("programs.period.day")));
            Periods.Add(new(7, Loc.T("programs.period.week")));
            Periods.Add(new(30, Loc.T("programs.period.month")));
            Periods.Add(new(60, Loc.T("programs.period.twomonths")));
            Sorts.Clear();
            foreach (var s in Enum.GetValues<ProgramSort>())
                Sorts.Add(new(s, Loc.T("programs.sort." + s.ToString().ToLowerInvariant())));
            SelectedPeriod = Periods.First(p => p.Value == period);
            SelectedSort = Sorts.First(s => s.Value == sort);
        }
        finally
        {
            _building = false;
        }
    }
}
