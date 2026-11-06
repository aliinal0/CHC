using System.Diagnostics;
using CHC.Core.Collection;

namespace CHC.Core.History;

/// <summary>
/// Adds up what each program uses into one row per hour, separately for the time a game was being played, so the
/// history can tell what ran next to that game. A program using next to nothing doesn't get a row.
/// </summary>
internal sealed class ProgramTracker(ProcessSampler sampler) : IDisposable
{
    /// <summary>A longer gap means the PC slept or recording was paused; the counters since then say nothing about a period.</summary>
    private const double MaxSampleSeconds = 120;

    private sealed class Entry
    {
        public required string Name;
        public string? Game;
        public int MainPid;
        public double Seconds, FrontSeconds, CpuSum, CpuMax, MemorySum, MemoryMax, GpuSum, GpuMax, VideoSum, ComputeSum, VramMax;
        public long ReadBytes, WriteBytes;
        public string? Adapter;
    }

    private readonly Dictionary<(string Name, string Game), Entry> _hour = [];
    private readonly Dictionary<string, string?> _titles = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _hourStart;

    /// <summary>Takes a reading. When a new hour begins, the rows of the finished one are returned to be saved.</summary>
    public IReadOnlyList<ProgramHour> Sample(int frontPid, string? game, DateTime utcNow)
    {
        IReadOnlyList<ProgramHour> finished = [];
        var hour = new DateTime(utcNow.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc);
        if (hour != _hourStart)
        {
            finished = Rows();
            _hour.Clear();
            _hourStart = hour;
        }

        var (seconds, programs) = sampler.Read(frontPid);
        if (seconds is <= 0 or > MaxSampleSeconds) return finished;
        const double MiB = 1024 * 1024;
        foreach (var p in programs)
        {
            var key = (p.Name.ToLowerInvariant(), game?.ToLowerInvariant() ?? "");
            if (!_hour.TryGetValue(key, out var e)) _hour[key] = e = new Entry { Name = p.Name, Game = game };
            e.MainPid = p.MainPid;
            e.Seconds += seconds;
            if (p.InFront) e.FrontSeconds += seconds;
            e.CpuSum += p.CpuPercent * seconds;
            e.CpuMax = Math.Max(e.CpuMax, p.CpuPercent);
            e.MemorySum += p.MemoryBytes / MiB * seconds;
            e.MemoryMax = Math.Max(e.MemoryMax, p.MemoryBytes / MiB);
            e.ReadBytes += p.ReadBytes;
            e.WriteBytes += p.WriteBytes;
            e.GpuSum += p.GpuPercent * seconds;
            e.GpuMax = Math.Max(e.GpuMax, p.GpuPercent);
            e.VideoSum += p.GpuVideo * seconds;
            e.ComputeSum += p.GpuCompute * seconds;
            e.VramMax = Math.Max(e.VramMax, p.VramBytes / MiB);
            if (p.GpuPercent > 0 && p.Adapter is not null) e.Adapter = p.Adapter;
        }
        return finished;
    }

    /// <summary>The current hour so far: the rows replace what was saved of it before.</summary>
    public IReadOnlyList<ProgramHour> Rows() =>
        _hour.Values.Where(Matters).Select(e => new ProgramHour
        {
            Start = _hourStart,
            App = e.Name,
            AppTitle = TitleOf(e),
            Game = e.Game,
            Seconds = e.Seconds,
            FrontSeconds = e.FrontSeconds,
            CpuSum = e.CpuSum,
            CpuMax = e.CpuMax,
            MemorySum = e.MemorySum,
            MemoryMax = e.MemoryMax,
            ReadBytes = e.ReadBytes,
            WriteBytes = e.WriteBytes,
            GpuSum = e.GpuSum,
            GpuMax = e.GpuMax,
            VideoSum = e.VideoSum,
            ComputeSum = e.ComputeSum,
            VramMax = e.VramMax,
            Adapter = e.Adapter,
        }).ToList();

    private static bool Matters(Entry e) =>
        e.Seconds > 0 && (e.FrontSeconds > 0 || e.CpuSum / e.Seconds >= 0.3 || e.MemorySum / e.Seconds >= 150 || e.GpuSum / e.Seconds >= 1
                          || e.ReadBytes + e.WriteBytes >= 100L * 1024 * 1024);

    /// <summary>The name in the exe's file details ("Google Chrome"), looked up once per program.</summary>
    private string? TitleOf(Entry e)
    {
        if (_titles.TryGetValue(e.Name, out var known)) return known;
        string? title = null;
        try
        {
            if (Native.ProcessPath(e.MainPid) is { } path)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                title = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
            }
        }
        catch (FileNotFoundException) { }
        return _titles[e.Name] = title;
    }

    public void Dispose() => sampler.Dispose();
}
