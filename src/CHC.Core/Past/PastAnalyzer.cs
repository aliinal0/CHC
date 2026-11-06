using System.Text.RegularExpressions;
using CHC.Core.Diagnostics;

namespace CHC.Core.Past;

/// <summary>What one program did in one stretch of time, kept in the history. The sum of the processor is percent × seconds, as in the program history.</summary>
/// <param name="Hourly">From Windows' hourly table: how long it ran can be trusted, but what it used can't, and being in front isn't noted.</param>
public sealed record PastBlock(DateTime Start, DateTime End, string Exe, string? Path, double Seconds, double Focus, double Input, double CpuSum, long DiskBytes, long NetBytes, bool Hourly = false)
{
    /// <summary>How long it was in front. A game's hourly stretches count whole, since a game is rarely left running behind something else.</summary>
    public double PlayedSeconds => Hourly ? Seconds : Focus;
}

public enum PowerState
{
    On,
    Asleep,
}

/// <summary>A stretch of time in which the PC was on or asleep. In between it was off.</summary>
public sealed record PowerSpan(DateTime Start, DateTime End, PowerState State);

/// <summary>A program and what it used of the processor.</summary>
public sealed record PastNeighbour(string Exe, double CpuPercent);

public sealed record PastDay(DateTime Date, double Minutes);

/// <summary>A game as Windows' records remember it: no frame rates or temperatures, but when, how long, and what ran beside it.</summary>
/// <param name="FocusSeconds">All of its time: <paramref name="PlayedSeconds"/> and <paramref name="OpenSeconds"/>.</param>
/// <param name="PlayedSeconds">Known to have been in front, from the part of the records that says so.</param>
/// <param name="OpenSeconds">Only known to have been open, from the part that doesn't.</param>
/// <param name="Spans">The sittings, for telling what happened during them.</param>
/// <param name="Stretches">Each stretch by itself, for showing exactly when.</param>
public sealed record PastGameUse(string Exe, string? Path, double FocusSeconds, int Sessions, DateTime First, DateTime Last, double? CpuLoad,
    long DiskBytes, long NetBytes, IReadOnlyList<PastDay> Days, IReadOnlyList<PastNeighbour> Alongside, IReadOnlyList<(DateTime Start, DateTime End)> Spans,
    double PlayedSeconds, double OpenSeconds, IReadOnlyList<(DateTime Start, DateTime End)> Stretches);

/// <summary>A program in a given hour.</summary>
/// <param name="CpuPercent">Null where only the hourly table knows about it, whose processor numbers can't be trusted.</param>
/// <param name="CpuWhileRunning">Its average while it ran, over the part of the records that says what it used.</param>
public sealed record PastProgramRow(string Exe, double Focus, double Running, double? CpuPercent, long DiskBytes, long NetBytes, double? CpuWhileRunning = null);

/// <summary>Works out what Windows' records say about the past.</summary>
public static partial class PastAnalyzer
{
    /// <summary>Less time in front than this says too little about a game.</summary>
    public const double MinFocusSeconds = 600;

    /// <summary>A stretch with less in front than this is the game running behind something else, or starting up.</summary>
    private const double MinBlockFocus = 30;

    /// <summary>Back in front within this time it was the same sitting.</summary>
    private static readonly TimeSpan SameSession = TimeSpan.FromMinutes(10);

    /// <summary>The hourly part is stamped about once an hour, whatever the game did in between.</summary>
    private static readonly TimeSpan SameSessionHourly = TimeSpan.FromMinutes(70);

    /// <summary>A program that ran this long without ever being in front isn't what was being played.</summary>
    private const double LongInBackground = 3600;

    /// <summary>Beside a game, less than this isn't worth naming.</summary>
    private const double MinAlongside = 1;

    /// <param name="lastActivity">The last thing the PC noted before a time, for one that stopped without saying so.</param>
    public static IReadOnlyList<PowerSpan> PowerSpansFrom(IEnumerable<(DateTime Time, PowerEvent Event)> events, DateTime now, Func<DateTime, DateTime?>? lastActivity = null)
    {
        var spans = new List<PowerSpan>();
        DateTime? started = null;
        var state = PowerState.On;
        DateTime lastSeen = default;
        void Close(DateTime end)
        {
            if (started is { } s && end > s) spans.Add(new PowerSpan(s, end, state));
            started = null;
        }
        foreach (var (time, e) in events.OrderBy(x => x.Time))
        {
            switch (e)
            {
                case PowerEvent.Started:
                    // Still marked on: it lost power, or crashed, some time after the last thing noted about it.
                    if (started is not null) Close(lastActivity?.Invoke(time) ?? lastSeen);
                    started = time;
                    state = PowerState.On;
                    break;
                case PowerEvent.ShutDown:
                    Close(time);
                    break;
                case PowerEvent.Sleeping:
                    Close(time);
                    started = time;
                    state = PowerState.Asleep;
                    break;
                case PowerEvent.Woke:
                    Close(time);
                    started = time;
                    state = PowerState.On;
                    break;
            }
            lastSeen = time;
        }
        if (started is not null) Close(now);
        return spans;
    }

    /// <summary>What the records add per day to what CHC itself saw: the non-CHC part.</summary>
    public static IReadOnlyList<PastDay> BeyondMeasured(IReadOnlyList<PastDay> records, IReadOnlyList<PastDay> measured)
    {
        var seen = measured.ToDictionary(d => d.Date, d => d.Minutes);
        return records.Select(d => new PastDay(d.Date, d.Minutes - seen.GetValueOrDefault(d.Date))).Where(d => d.Minutes >= 1).ToList();
    }

    /// <summary>The minutes played per day. A block across midnight counts on both days, the way CHC's own minutes do, or a late evening would look like a non-CHC record of the next day.</summary>
    private static List<PastDay> Daily(IEnumerable<PastBlock> played)
    {
        var minutes = new SortedDictionary<DateTime, double>();
        foreach (var block in played)
        {
            var length = (block.End - block.Start).TotalSeconds;
            if (length <= 0)
            {
                minutes[block.End.Date] = minutes.GetValueOrDefault(block.End.Date) + block.PlayedSeconds / 60;
                continue;
            }
            for (var day = block.Start.Date; day < block.End; day = day.AddDays(1))
            {
                var from = block.Start > day ? block.Start : day;
                var to = block.End < day.AddDays(1) ? block.End : day.AddDays(1);
                minutes[day] = minutes.GetValueOrDefault(day) + block.PlayedSeconds * ((to - from).TotalSeconds / length) / 60;
            }
        }
        return minutes.Select(m => new PastDay(m.Key, m.Value)).ToList();
    }

    /// <summary>The minutes the PC was on, per day, counting a stretch across midnight on both days.</summary>
    public static IReadOnlyList<PastDay> MinutesOn(IReadOnlyList<PowerSpan> spans, DateTime first, int days)
    {
        var minutes = new double[days];
        foreach (var span in spans.Where(s => s.State == PowerState.On))
        {
            for (var day = span.Start.Date; day < span.End; day = day.AddDays(1))
            {
                var index = (day - first.Date).Days;
                if (index < 0 || index >= days) continue;
                var from = span.Start > day ? span.Start : day;
                var to = span.End < day.AddDays(1) ? span.End : day.AddDays(1);
                minutes[index] += (to - from).TotalMinutes;
            }
        }
        return minutes.Select((m, i) => new PastDay(first.Date.AddDays(i), m)).Where(d => d.Minutes >= 1).ToList();
    }

    /// <summary>The games in the records, the most played first.</summary>
    /// <param name="known">Program files Windows itself recognised as games.</param>
    public static IReadOnlyList<PastGameUse> Games(IReadOnlyList<PastBlock> blocks, IReadOnlyCollection<string> known)
    {
        var knownSet = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var games = new List<PastGameUse>();
        var groups = blocks.GroupBy(b => b.Exe, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in groups)
        {
            var path = group.Select(b => b.Path).FirstOrDefault(p => p is not null);
            if (!IsGame(group.Key, path, knownSet)) continue;

            // "VALORANT.exe" beside "VALORANT-Win64-Shipping.exe": the small starter stays open while the real game runs.
            var stem = System.IO.Path.GetFileNameWithoutExtension(group.Key);
            if (groups.Any(g => g.Key.StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase))) continue;

            var measured = group.Where(b => !b.Hourly).ToList();
            if (measured.Sum(b => b.Seconds) >= LongInBackground && measured.Sum(b => b.Focus) < MinBlockFocus) continue;

            var played = group.Where(b => b.PlayedSeconds >= MinBlockFocus).OrderBy(b => b.End).ToList();
            var focus = played.Sum(b => b.PlayedSeconds);
            if (focus < MinFocusSeconds) continue;
            var spans = Merge(played);
            var exact = played.Where(b => !b.Hourly).ToList();
            var seconds = exact.Sum(b => b.Seconds);
            games.Add(new PastGameUse(
                group.Key,
                path,
                focus,
                spans.Count,
                played[0].Start,
                played[^1].End,
                seconds > 0 ? exact.Sum(b => b.CpuSum) / seconds : null,
                exact.Sum(b => b.DiskBytes),
                exact.Sum(b => b.NetBytes),
                Daily(played),
                // Only the part of the records that says what it used, and only that part's time.
                Alongside(blocks, group.Key, Merge(exact)),
                spans,
                exact.Sum(b => b.Focus),
                played.Where(b => b.Hourly).Sum(b => b.Seconds),
                played.Select(b => (b.Start, b.End)).ToList()));
        }
        return games.OrderByDescending(g => g.FocusSeconds).ToList();
    }

    public static bool IsGame(string exe, string? path, IReadOnlySet<string> known) =>
        !Catalog.IsNotAGame(exe) && !NotThePlayedProgram().IsMatch(exe) && (known.Contains(exe) || Catalog.IsGamePath(path) || GamePackage().IsMatch(exe));

    /// <summary>What sits in a game's folder and runs next to it, or all day: launchers, services, updaters, crash reporters, anti-cheat.</summary>
    [GeneratedRegex(@"background|service|svc\b|host\.exe|helper|handler|crash(report|handler|pad|sender)|reporter|launcher|bootstrap|updater|update\.exe|^update|setup|install|proxy|destager|overlay|redist|downloader|cefsub|anticheat|_eac|_be\.exe|^vgc|^vgtray|^dxdiag|webview", RegexOptions.IgnoreCase)]
    private static partial Regex NotThePlayedProgram();

    [GeneratedRegex(@"^(Microsoft\.(Limitless|ForteBaseGame|Forza\w*|MinecraftUWP|FlightSimulator|SeaofThieves|Halo\w*)|FlightSimulator\d*\.exe|Forza\w*\.exe)", RegexOptions.IgnoreCase)]
    private static partial Regex GamePackage();

    private static List<(DateTime Start, DateTime End)> Merge(IReadOnlyList<PastBlock> played)
    {
        var spans = new List<(DateTime Start, DateTime End)>();
        var lastHourly = false;
        foreach (var block in played.OrderBy(b => b.Start))
        {
            var gap = block.Hourly || lastHourly ? SameSessionHourly : SameSession;
            if (spans.Count > 0 && block.Start - spans[^1].End <= gap)
                spans[^1] = (spans[^1].Start, block.End > spans[^1].End ? block.End : spans[^1].End);
            else
                spans.Add((block.Start, block.End));
            lastHourly = block.Hourly;
        }
        return spans;
    }

    /// <summary>What used the processor while the game was in front. A stretch only partly during the game counts for that part.</summary>
    private static List<PastNeighbour> Alongside(IReadOnlyList<PastBlock> blocks, string game, List<(DateTime Start, DateTime End)> spans)
    {
        var total = spans.Sum(s => (s.End - s.Start).TotalSeconds);
        if (total <= 0) return [];
        var used = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            if (block.Hourly || block.CpuSum <= 0 || block.Exe.Equals(game, StringComparison.OrdinalIgnoreCase) || !IsOwnProgram(block)) continue;
            var overlap = Overlap(block.Start, block.End, spans);
            if (overlap <= 0) continue;
            var length = (block.End - block.Start).TotalSeconds;
            used[block.Exe] = used.GetValueOrDefault(block.Exe) + (length > 0 ? block.CpuSum * Math.Min(1, overlap / length) : block.CpuSum);
        }
        return used.Select(u => new PastNeighbour(u.Key, u.Value / total)).Where(n => n.CpuPercent >= MinAlongside).OrderByDescending(n => n.CpuPercent).Take(3).ToList();
    }

    /// <summary>How many seconds of a stretch fall inside sorted spans.</summary>
    private static double Overlap(DateTime start, DateTime end, List<(DateTime Start, DateTime End)> spans)
    {
        var low = 0;
        var high = spans.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (spans[mid].End <= start) low = mid + 1;
            else high = mid;
        }
        var seconds = 0.0;
        for (var i = low; i < spans.Count && spans[i].Start < end; i++)
        {
            var from = spans[i].Start > start ? spans[i].Start : start;
            var to = spans[i].End < end ? spans[i].End : end;
            if (to > from) seconds += (to - from).TotalSeconds;
        }
        return seconds;
    }

    /// <summary>Programs in a period, what was in front first. Windows' own parts are left out.</summary>
    public static IReadOnlyList<PastProgramRow> Programs(IReadOnlyList<PastBlock> blocks, DateTime from, DateTime to)
    {
        var length = (to - from).TotalSeconds;
        var rows = new Dictionary<string, (double Focus, double Run, double Cpu, double ExactRun, double Rank, double Disk, double Net)>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in blocks)
        {
            if (block.End <= from || block.Start >= to || !IsOwnProgram(block)) continue;
            var span = (block.End - block.Start).TotalSeconds;
            var start = block.Start > from ? block.Start : from;
            var end = block.End < to ? block.End : to;
            var share = span > 0 ? Math.Clamp((end - start).TotalSeconds / span, 0, 1) : 1;
            rows.TryGetValue(block.Exe, out var x);
            // The hourly table's cycles aren't worth showing, but still tell the busy programs from the idle ones.
            var exact = !block.Hourly;
            rows[block.Exe] = (x.Focus + block.Focus * share, x.Run + Math.Min(block.Seconds, span > 0 ? span : block.Seconds) * share, x.Cpu + (exact ? block.CpuSum * share : 0), x.ExactRun + (exact ? block.Seconds * share : 0),
                x.Rank + block.CpuSum * share, x.Disk + (exact ? block.DiskBytes * share : 0), x.Net + (exact ? block.NetBytes * share : 0));
        }
        return rows
            .Where(r => r.Value.Focus >= 1 || r.Value.Rank / Math.Max(1, length) >= 0.05)
            .OrderByDescending(r => r.Value.Focus).ThenByDescending(r => r.Value.Rank)
            .Select(r => new PastProgramRow(r.Key, r.Value.Focus, r.Value.Run, r.Value.ExactRun > 0 && length > 0 ? r.Value.Cpu / length : null, (long)r.Value.Disk, (long)r.Value.Net,
                r.Value.ExactRun > 0 ? r.Value.Cpu / r.Value.ExactRun : null))
            .ToList();
    }

    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";

    private static bool IsOwnProgram(PastBlock block) =>
        IsOwnProgram(block.Exe) && !(block.Path?.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Not Windows itself, and not CHC.</summary>
    public static bool IsOwnProgram(string exe) =>
        !Catalog.IsWindowsComponent(exe) && !exe.Equals("CHC.exe", StringComparison.OrdinalIgnoreCase) && !WindowsPart().IsMatch(exe);

    [GeneratedRegex(@"^(System( Interrupts)?|IsrDpc|servicehost|ShellHost|LogonUI|Registry|Secure System|Memory Compression|Unknown|Idle|GameBar.*|gamingservices.*|XboxPcApp.*|MicrosoftWindows\..*|Microsoft\.(Windows|AAD|XboxGamingOverlay|GamingServices|WidgetsPlatformRuntime|YourPhone|ScreenSketch).*|windows\..*|.*\.dll)(\.exe)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsPart();

    /// <summary>A name to show: "chrome" for chrome.exe, "SpotifyMusic" for SpotifyAB.SpotifyMusic_abc123.</summary>
    public static string NameOf(string exe)
    {
        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return exe[..^4].Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase);
        var underscore = exe.IndexOf('_');
        var name = underscore > 0 ? exe[..underscore] : exe;
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    /// <summary>A game's name: the folder its store installed it in if there is one ("Fortnite" for FortniteClient-Win64-Shipping.exe).</summary>
    public static string TitleOf(string exe, string? path)
    {
        // javaw.exe runs every Java program on the PC, so one folder doesn't name them all.
        if (exe.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) return "Java games";
        if (exe.StartsWith("FlightSimulator", StringComparison.OrdinalIgnoreCase)) return "Microsoft Flight Simulator " + exe["FlightSimulator".Length..].Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        return path is not null && StoreFolder().Match(path) is { Success: true } folder ? Tidy(folder.Groups[1].Value) : Tidy(NameOf(exe));
    }

    /// <summary>Windows' records sometimes hold paths in small letters: "squad" reads better as "Squad".</summary>
    private static string Tidy(string name) =>
        name.Length > 0 && name.All(c => !char.IsUpper(c)) ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name) : name;

    [GeneratedRegex(@"\\(?:steamapps\\common|Epic Games|Rockstar Games|Riot Games|GOG Games|Ubisoft Game Launcher\\games|EA Games|Battle\.net)\\([^\\]+)\\", RegexOptions.IgnoreCase)]
    private static partial Regex StoreFolder();

    /// <summary>Turns what was read into stretches per program. Rows with the same end belong together: several processes of one program.</summary>
    /// <param name="exactSince">Where the history's own exact stretches begin, if earlier readings kept some: the hourly table adds nothing from there on.</param>
    public static IReadOnlyList<PastBlock> ToBlocks(SrumRecords records, DateTime? exactSince = null)
    {
        var cpuPerCycle = records.CyclesPerSecond > 0 ? 100 / records.CyclesPerSecond : 0;
        var hours = HourlyBlocks(records, cpuPerCycle, exactSince);
        return records.Blocks
            .GroupBy(b => (End: new DateTime(b.EndUtc.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc), Exe: Srum.ExeOf(b.App)))
            .Select(g =>
            {
                var span = g.Max(b => b.SpanSeconds);
                var end = g.Key.End.ToLocalTime();
                return new PastBlock(
                    end.AddSeconds(-span),
                    end,
                    g.Key.Exe,
                    records.Paths.GetValueOrDefault(g.Key.Exe),
                    g.Max(b => Math.Min(b.RunSeconds, b.SpanSeconds)),
                    Math.Min(g.Sum(b => b.FocusSeconds), span),
                    Math.Min(g.Sum(b => b.InputSeconds), span),
                    g.Sum(b => b.Cycles) * cpuPerCycle,
                    g.Sum(b => b.DiskKb) * 1024,
                    g.Sum(b => b.NetBytes));
            })
            .Where(b => b.Seconds >= 1)
            .Concat(hours)
            .ToList();
    }

    private static List<PastBlock> HourlyBlocks(SrumRecords records, double cpuPerCycle, DateTime? exactSince)
    {
        // A store game can be its package here and its program file in the timeline.
        var programOfPackage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in records.Blocks)
        {
            var at = block.App.IndexOf('!', StringComparison.Ordinal);
            if (at > 0) programOfPackage[Srum.ExeOf(block.App[..at])] = Srum.ExeOf(block.App);
        }
        return records.Hours
            .Where(h => exactSince is not { } since || h.EndUtc.ToLocalTime() <= since)
            .GroupBy(h => (h.EndUtc, Exe: Srum.ExeOf(h.App) is var exe && programOfPackage.TryGetValue(exe, out var program) ? program : exe))
            .Select(g =>
            {
                var seconds = Math.Min(g.Max(h => h.RunSeconds), HourlySeconds);
                var end = g.Key.EndUtc.ToLocalTime();
                return new PastBlock(end.AddSeconds(-seconds), end, g.Key.Exe, records.Paths.GetValueOrDefault(g.Key.Exe), seconds, 0, 0, g.Sum(h => h.Cycles) * cpuPerCycle, 0, 0, Hourly: true);
            })
            .ToList();
    }

    /// <summary>A row adds up all the processes of a program; together they still only ran for that hour.</summary>
    private const double HourlySeconds = 3600;
}

public enum PowerEvent
{
    Started,
    ShutDown,
    Sleeping,
    Woke,
}
