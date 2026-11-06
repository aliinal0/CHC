using System.Text.RegularExpressions;
using CHC.Core.Diagnostics;
using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Past;
using CHC.Core.Presentation;
using CHC.Core.Timeline;

namespace CHC.Core.Performance;

/// <summary>What mostly holds a game back on this PC.</summary>
public enum GameLimit
{
    /// <summary>Not enough to tell: the graphics card has room, but without the frame rate the reason stays open.</summary>
    Unknown,
    GraphicsCard,
    Processor,

    /// <summary>The game stays at a round number of FPS below what the monitor shows, with the graphics card to spare.</summary>
    FrameCap,

    /// <summary>Neither stands out.</summary>
    Balanced,

    /// <summary>The game reaches the monitor's refresh rate.</summary>
    Nothing,
}

/// <summary>A game as the history remembers it, what holds it back, and what to change.</summary>
public sealed class GameReport
{
    public required GameUsage Usage { get; init; }
    public required string Title { get; init; }
    public GameLimit Limit { get; init; }
    public int MonitorHz { get; init; }
    public IReadOnlyList<HistoryPoint> DailyFps { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    /// <summary>The name of what holds it back.</summary>
    public string LimitKey => Limit switch
    {
        GameLimit.GraphicsCard => "game.limit.gpu",
        GameLimit.Processor => "game.limit.cpu",
        GameLimit.FrameCap => "game.limit.cap",
        GameLimit.Nothing => "game.limit.none",
        GameLimit.Balanced => "game.limit.balanced",
        _ => "game.limit.unknown",
    };

    /// <summary>False for a game only Windows' records know about, so far without frame rates or temperatures.</summary>
    public bool Measured { get; init; } = true;

    /// <summary>What Windows' records say about it, when they were read.</summary>
    public PastGameUse? Past { get; init; }

    /// <summary>How long CHC itself saw it being played, per day, over the same days as the records.</summary>
    public IReadOnlyList<PastDay> MeasuredMinutes { get; init; } = [];
}

/// <summary>
/// Judges the games played lately from what the recorder kept: frame rate, load and temperatures minute by minute,
/// plus each game's own settings file where CHC can read it, and the change timeline for a game that got slower.
/// </summary>
public static partial class GameAdvisor
{
    private const ComponentKind K = ComponentKind.Performance;
    public const int PeriodDays = 30;

    /// <summary>How far back Windows' records are used; they hold about this much.</summary>
    public const int PastDays = 60;

    /// <summary>Play before a change, to say that the crashes began after it.</summary>
    private const double MinHoursBeforeChange = 3;

    /// <summary>Fewer minutes than this say too little about a game.</summary>
    private const int MinMinutes = 10;

    /// <summary>A trend needs this much play on both sides; one short match on a different map proves nothing.</summary>
    private const int MinTrendMinutes = 30;

    /// <summary>A smaller drop in FPS is within what different scenes and maps do anyway.</summary>
    private const double TrendDrop = 0.12;

    private static readonly int[] RoundCaps = [30, 60, 75, 90, 100, 120, 144, 165, 180, 200, 240];

    /// <param name="readSettings">Reads a game's settings file from its exe path; the real files by default, made-up ones in demo mode.</param>
    public static IReadOnlyList<GameReport> Build(HistoryStore store, SystemSnapshot snapshot, Func<string, GameSettings?>? readSettings = null, int count = 8)
    {
        readSettings ??= path => GameSettingsReader.Read(path, "");
        var now = DateTime.Now;
        var from = now.AddDays(-PeriodDays);
        var to = now.AddMinutes(1);
        var pastFrom = now.AddDays(-PastDays);
        var changes = store.Changes(pastFrom, to).Where(c => c.CanAffectStability).ToList();
        var past = PastAnalyzer.Games(store.PastBlocks(pastFrom, to), snapshot.Gaming.KnownGames).ToDictionary(g => g.Exe, StringComparer.OrdinalIgnoreCase);

        var measured = store.Games(from, to, 30).Where(IsGame).ToList();
        var measuredNames = measured.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var games = measured.Select(u => (Usage: u, Past: past.GetValueOrDefault(u.Name), Measured: true))
            .Concat(past.Values.Where(p => !measuredNames.Contains(p.Exe)).Select(p => (Usage: FromRecords(p), Past: (PastGameUse?)p, Measured: false)));
        var crashes = snapshot.PastEvents.AppEvents.Where(e => !e.IsHang).GroupBy(e => e.App, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return games
            // A game that keeps crashing is what someone wants to hear about first, however little it was played.
            .OrderByDescending(g => crashes.GetValueOrDefault(g.Usage.Name) >= 2)
            .ThenByDescending(g => Math.Max(g.Usage.Minutes, (g.Past?.FocusSeconds ?? 0) / 60))
            .Take(count)
            .Select(g => Analyze(store, snapshot, g.Usage, g.Past, g.Measured, changes, readSettings, from, to))
            .ToList();
    }

    private static GameUsage FromRecords(PastGameUse p) =>
        new(p.Exe, null, p.Path, (int)(p.FocusSeconds / 60), p.Last, null, null, null, 0, null, 0, p.CpuLoad, null, null, null);

    private static bool IsGame(GameUsage u) =>
        u.Minutes >= MinMinutes && !Catalog.IsNotAGame(u.Name) && (Catalog.IsGamePath(u.Path) || u.GpuLoad >= 25);

    private static GameReport Analyze(HistoryStore store, SystemSnapshot snapshot, GameUsage u, PastGameUse? past, bool measured, IReadOnlyList<SystemChange> changes,
        Func<string, GameSettings?> readSettings, DateTime from, DateTime to)
    {
        var settings = u.Path is { Length: > 0 } path ? readSettings(path) : null;
        var title = TitleOf(u, settings);
        var hz = snapshot.Gaming.MaxRefreshHz > 0 ? snapshot.Gaming.MaxRefreshHz : snapshot.Gaming.RefreshHz;
        var limit = LimitOf(u, settings, hz);
        var list = new List<Finding>();

        Trend(store, u, title, changes, from, to, list);
        WhatHoldsItBack(snapshot, u, title, settings, limit, hz, list);
        SettingsTips(snapshot, title, settings, u, limit, hz, list);
        Comfort(snapshot, u, title, hz, list);
        var programs = store.Programs(from, to, u.Name);
        NextToTheGame(snapshot, programs, u, title, settings, list);
        AddPast(snapshot, past, u, title, programs.Count == 0, changes, list);

        if (u.Fps is { } fps && list.All(f => f.Severity < Severity.Info))
            list.Add(new(K, Severity.Good, "game.good", title, fps));

        return new GameReport
        {
            Usage = u,
            Title = title,
            Limit = limit,
            MonitorHz = hz,
            DailyFps = store.GameDaily(u.Name, from, to),
            Findings = list.OrderByDescending(f => f.Severity).ToList(),
            Measured = measured,
            Past = past,
            MeasuredMinutes = past is null ? [] : store.GameMinutesDaily(u.Name, to.AddDays(-PastDays), to),
        };
    }

    /// <summary>
    /// What is known without having measured the game: how often it crashed according to Windows' log, in which part,
    /// whether that began after a change, whether memory ran out while it was on, and what ran beside it.
    /// </summary>
    private static void AddPast(SystemSnapshot snapshot, PastGameUse? past, GameUsage u, string title, bool sayAlongside, IReadOnlyList<SystemChange> changes, List<Finding> list)
    {
        var events = snapshot.PastEvents.AppEvents.Where(e => e.App.Equals(u.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var crashes = events.Where(e => !e.IsHang).ToList();
        var hangs = events.Count - crashes.Count;
        var hoursPlayed = Math.Max(u.Minutes / 60.0, (past?.FocusSeconds ?? 0) / 3600);
        if (crashes.Count > 0)
        {
            var module = crashes.Where(c => c.Module.Length > 0).GroupBy(c => c.Module, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
            var key = GraphicsDriverFile().IsMatch(module) ? "game.past.crashes.driver" : "game.past.crashes";
            list.Add(new(K, crashes.Count >= 2 ? Severity.Warning : Severity.Info, key, title, crashes.Count, PastDays, module.Length > 0 ? module : Fmt.Dash) { Scored = false });
            if (hoursPlayed >= 1 && crashes.Count >= 2)
                list[^1] = new(K, Severity.Warning, key + ".rate", title, crashes.Count, PastDays, module.Length > 0 ? module : Fmt.Dash, Math.Max(1, hoursPlayed / crashes.Count)) { Scored = false };

            if (crashes.Count >= 2 && past is not null && CrashesBeganAfter(crashes, past, changes) is { } change)
                list.Add(new(K, Severity.Warning, "game.past.crashes.after", title, new ChangeArgument(change), Fmt.Date(change.Time), new LocKey("stability.afterchange.fix." + HealthAnalyzer.FixFor(change))) { Scored = false });
        }
        if (hangs >= 2)
            list.Add(new(K, Severity.Info, "game.past.hangs", title, hangs, PastDays) { Scored = false });

        if (past is null) return;

        var low = snapshot.PastEvents.LowMemory.Where(e => past.Spans.Any(s => e.Time >= s.Start && e.Time <= s.End)).ToList();
        if (low.Count > 0)
        {
            var names = low.SelectMany(e => e.Programs).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(3).Select(g => PastAnalyzer.NameOf(g.Key));
            list.Add(new(K, Severity.Warning, "game.past.lowmemory", title, low.Count, PastDays, string.Join(", ", names)) { Scored = false });
        }

        if (sayAlongside && past.Alongside.Sum(n => n.CpuPercent) >= 5)
            list.Add(new(K, Severity.Info, "game.past.alongside", title, string.Join(", ", past.Alongside.Select(n => Loc.T("game.background.item", PastAnalyzer.NameOf(n.Exe), n.CpuPercent, 0)))) { Scored = false });
    }

    /// <summary>The change after which every crash came, when the game had been played without any before it.</summary>
    private static SystemChange? CrashesBeganAfter(IReadOnlyList<AppEvent> crashes, PastGameUse past, IReadOnlyList<SystemChange> changes)
    {
        var first = crashes.Min(c => c.Time);
        return changes
            .Where(c => c.Time <= first && first - c.Time <= TimeSpan.FromDays(14))
            .Where(c => past.Spans.Where(s => s.End <= c.Time).Sum(s => (s.End - s.Start).TotalHours) >= MinHoursBeforeChange)
            .OrderByDescending(c => c.Time)
            .FirstOrDefault();
    }

    [GeneratedRegex(@"^(nvwgf|nvlddmkm|nvd3d|nvoglv|nvgpucomp|nvapi|atidx|atiu|amdx|amdvlk|aga|igd|igc|ig\d|dxgi|d3d1[012]|D3D12Core)", RegexOptions.IgnoreCase)]
    private static partial Regex GraphicsDriverFile();

    /// <summary>
    /// Reaching the monitor comes first; then a graphics card at full load most of the time; a card with room at a round
    /// number of FPS is a frame limit; a card with room below that is waiting for the processor.
    /// </summary>
    private static GameLimit LimitOf(GameUsage u, GameSettings? settings, int hz)
    {
        if (u.Fps is { } reached && hz > 0 && reached >= hz * 0.95) return GameLimit.Nothing;
        if (u.GpuBusyShare >= 50) return GameLimit.GraphicsCard;
        if (u.GpuLoad is not { } gpu) return GameLimit.Unknown;
        if (u.Fps is not { } fps) return gpu < 70 ? GameLimit.Unknown : GameLimit.Balanced;
        if (gpu >= 75) return GameLimit.Balanced;
        return IsCapped(fps, u.FpsBest ?? fps, settings) ? GameLimit.FrameCap : GameLimit.Processor;
    }

    /// <summary>
    /// The game's own limit, or a round number it stays at: an average merely close to one isn't enough, because the
    /// fastest minute of a game without a limit gets well past it.
    /// </summary>
    private static bool IsCapped(double fps, double best, GameSettings? settings)
    {
        if (settings?.FrameLimit is > 0 and var limit && Math.Abs(fps - limit) <= limit * 0.03) return true;
        return RoundCaps.Any(cap => Math.Abs(fps - cap) <= cap * 0.015 && best <= cap * 1.02);
    }

    /// <summary>
    /// The same game before and after a driver, update, BIOS or memory change. The change with the biggest lasting drop
    /// is named; a drop no change explains is still worth a word.
    /// </summary>
    private static void Trend(HistoryStore store, GameUsage u, string title, IReadOnlyList<SystemChange> changes, DateTime from, DateTime to, List<Finding> list)
    {
        if (u.FpsMinutes < MinTrendMinutes) return;
        (SystemChange Change, double Before, double After)? worst = null;
        foreach (var change in changes)
        {
            var before = store.GameFps(u.Name, change.Time.AddDays(-14), change.Time);
            var after = store.GameFps(u.Name, change.Time, to);
            if (before.Minutes < MinTrendMinutes || after.Minutes < MinTrendMinutes || before.Fps is not { } b || after.Fps is not { } a) continue;
            if (a > b * (1 - TrendDrop)) continue;
            if (worst is not { } w || a / b < w.After / w.Before) worst = (change, b, a);
        }
        if (worst is { } found)
        {
            list.Add(new(K, Severity.Warning, "game.fps.afterchange", title, found.Before, found.After, new ChangeArgument(found.Change),
                Fmt.Date(found.Change.Time), new LocKey("stability.afterchange.fix." + HealthAnalyzer.FixFor(found.Change))));
            return;
        }

        var split = to.AddDays(-7);
        var earlier = store.GameFps(u.Name, from, split);
        var recent = store.GameFps(u.Name, split, to);
        if (earlier.Minutes >= MinTrendMinutes && recent.Minutes >= MinTrendMinutes && earlier.Fps is { } was && recent.Fps is { } now && now <= was * (1 - TrendDrop))
            list.Add(new(K, Severity.Info, "game.fps.drop", title, was, now));
    }

    private static void WhatHoldsItBack(SystemSnapshot snapshot, GameUsage u, string title, GameSettings? s, GameLimit limit, int hz, List<Finding> list)
    {
        var target = hz > 0 ? Math.Min(hz, 144) : 60;
        var gpu = u.GpuLoad ?? 0;
        switch (limit)
        {
            // At full load, but fast enough that there is nothing to win.
            case GameLimit.GraphicsCard when u.Fps is { } fast && fast >= target * 0.9:
                break;

            case GameLimit.GraphicsCard:
                var heavy = s?.Quality.Where(q => q.IsHigh && GameSettings.GpuHeavy.Contains(q.Setting)).OrderByDescending(q => q.Level).Take(4).ToList() ?? [];
                object lower = heavy.Count > 0
                    ? new LocFormat("game.gpu.lower.settings", PerformanceAnalyzer.Items(heavy))
                    : new LocFormat("game.gpu.lower.generic", PerformanceAnalyzer.UpscalerFor(snapshot));
                list.Add(new(K, u.Fps is < 40 ? Severity.Warning : Severity.Info, "game.gpu.limited", title, u.GpuBusyShare, Fmt.Duration(TimeSpan.FromMinutes(u.Minutes)), lower));
                break;

            case GameLimit.Processor:
                var cpuHeavy = s?.Quality.Where(q => q.IsHigh && GameSettings.CpuHeavy.Contains(q.Setting)).ToList() ?? [];
                object cpuLower = s is { RenderDistance: >= 16 } or { SimulationDistance: >= 12 }
                    ? new LocFormat("gameset.mc.distances", s.RenderDistance, s.SimulationDistance)
                    : cpuHeavy.Count > 0 ? PerformanceAnalyzer.Items(cpuHeavy) : new LocKey("perf.game.cpuheavy.generic");
                list.Add(new(K, Severity.Info, "game.cpu.limited", title, u.Fps, hz, gpu, cpuLower));
                // Slow memory is the system setting that costs a processor-limited game the most.
                var memory = HealthAnalyzer.MemoryProfile(snapshot.Memory);
                if (memory is { Xmp: false, Rated: { } rated })
                    list.Add(new(K, Severity.Info, "memory.xmp.off", memory.Configured, rated));
                if (snapshot.Memory.Modules.Count == 1 && snapshot.Memory.SlotCount >= 2)
                    list.Add(new(K, Severity.Info, "perf.singlechannel"));
                break;

            case GameLimit.FrameCap:
                list.Add(new(K, Severity.Info, "game.capped", title, u.Fps, gpu, hz));
                break;

            case GameLimit.Nothing when gpu < 70:
                list.Add(new(K, Severity.Info, "game.headroom", title, u.Fps, hz, gpu, Math.Max(30, hz - 3)));
                break;

            case GameLimit.Unknown when u.GpuLoad is not null:
                list.Add(new(K, Severity.Info, "game.gpu.room", title, gpu));
                break;
        }
    }

    /// <summary>What the game's own settings file adds; the same advice a measured session gives.</summary>
    private static void SettingsTips(SystemSnapshot snapshot, string title, GameSettings? s, GameUsage u, GameLimit limit, int hz, List<Finding> list)
    {
        if (s is null) return;
        var g = snapshot.Gaming;
        var (nativeWidth, nativeHeight) = g.NativeWidth > 0 ? (g.NativeWidth, g.NativeHeight) : (g.Width, g.Height);
        var target = hz > 0 ? Math.Min(hz, 144) : 60;
        var slow = limit == GameLimit.GraphicsCard && (u.Fps is null || u.Fps < target * 0.9);

        if (s is { DisplayMode: GameDisplayMode.Fullscreen, Width: { } width, Height: { } height } && width < nativeWidth)
            list.Add(new(K, Severity.Info, "perf.game.resolution", title, $"{width} × {height}", $"{nativeWidth} × {nativeHeight}"));
        if (s.DisplayMode == GameDisplayMode.Windowed)
            list.Add(new(K, Severity.Info, "perf.game.windowed", title));
        if (s.VSync == true && hz > 0)
            list.Add(new(K, Severity.Info, "perf.game.vsync", title, hz, hz - 3));
        if (s.FrameLimit is > 0 and var limitFps && hz > 0 && limitFps < hz - 5 && u.GpuLoad < 90)
            list.Add(new(K, Severity.Info, "perf.game.fpslimit", title, limitFps, hz, hz - 3));
        if (s.RenderScale is < 95 and var low)
            list.Add(new(K, Severity.Info, "perf.game.renderscale.low", title, low));
        else if (s.RenderScale is > 105 and var high && slow)
            list.Add(new(K, Severity.Info, "perf.game.renderscale.high", title, high));
        if (!slow) return;
        if (s.UpscalerKnown && s.Upscaler.Length == 0 && s.RenderScale is null or (>= 95 and <= 105))
            list.Add(new(K, Severity.Info, "perf.game.upscaler", title, PerformanceAnalyzer.UpscalerFor(snapshot)));
        if (s.Reflex == false && snapshot.Gpus.Any(x => !x.IsIntegrated && x.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
            list.Add(new(K, Severity.Info, "perf.game.reflex", title));
    }

    /// <summary>Stutter, heat, full memory and a slow disk: what makes a game unpleasant whatever its average.</summary>
    private static void Comfort(SystemSnapshot snapshot, GameUsage u, string title, int hz, List<Finding> list)
    {
        var target = hz > 0 ? Math.Min(hz, 144) : 60;
        if (u.Fps is { } fps && u.FpsLow is { } low && low < fps * 0.45 && low < target * 0.75)
            list.Add(new(K, Severity.Info, "game.stutter", title, fps, low));
        if (u.GpuTempMax is >= 85 and var gpuTemp)
            list.Add(new(K, gpuTemp >= 92 ? Severity.Warning : Severity.Info, "game.hot.gpu", title, gpuTemp));
        if (u.CpuTempMax is >= 92 and var cpuTemp)
            list.Add(new(K, Severity.Info, "game.hot.cpu", title, cpuTemp));
        if (u.Path is { Length: >= 2 } path && path[1] == ':')
        {
            var letter = path[..2];
            var disk = snapshot.Disks.FirstOrDefault(d => d.DriveLetters.Contains(letter, StringComparer.OrdinalIgnoreCase));
            if (disk?.Media == DiskMedia.Hdd)
                list.Add(new(K, Severity.Info, "perf.game.hdd", title, disk.Model));
        }
    }

    /// <summary>
    /// The record of each program while this game ran: the adapter it ran on, its video memory, and what else was busy.
    /// That turns "close other programs" into which ones.
    /// </summary>
    private static void NextToTheGame(SystemSnapshot snapshot, IReadOnlyList<ProgramUsage> programs, GameUsage u, string title, GameSettings? settings, List<Finding> list)
    {
        var self = programs.FirstOrDefault(p => p.Name.Equals(u.Name, StringComparison.OrdinalIgnoreCase));
        var used = self?.Adapter is { } adapter ? snapshot.Gpus.FirstOrDefault(g => g.Name.Contains(adapter, StringComparison.OrdinalIgnoreCase) || adapter.Contains(g.Name, StringComparison.OrdinalIgnoreCase)) : null;
        var card = snapshot.Gpus.FirstOrDefault(g => !g.IsIntegrated && !g.IsVirtual && !g.UsesBasicDriver);
        if (used is { IsIntegrated: true } && card is not null && self is { GpuAverage: >= 10 })
            list.Add(new(K, Severity.Warning, "game.gpu.integrated", title, used.Name, card.Name));

        var memoryOf = used ?? card;
        if (self is { VramMaxMb: > 0 } && memoryOf is { VramBytes: > 0 } && self.VramMaxMb * 1024 * 1024 >= memoryOf.VramBytes * 0.95)
        {
            list.Add(new(K, Severity.Warning, "perf.vram.full", self.VramMaxMb / 1024, memoryOf.VramBytes / 1024.0 / 1024 / 1024));
            if (settings?.Quality.FirstOrDefault(q => q.Setting == "textures") is { IsHigh: true } textures)
                list.Add(new(K, Severity.Warning, "perf.game.textures", title, new LocKey(textures.LevelKey)));
        }

        var others = programs.Where(p => p != self && ProgramAdvisor.IsOwnProgram(p)).ToList();
        if (u.RamLoad is >= 88 and var ram)
        {
            var biggest = others.OrderByDescending(p => p.MemoryAverageMb).Take(3).ToList();
            list.Add(biggest.Count > 0
                ? new(K, Severity.Info, "game.ram.full", title, ram, self is null ? Fmt.Dash : Fmt.Bytes((ulong)(self.MemoryAverageMb * 1024 * 1024), 1), ProgramAdvisor.MemoryList(biggest))
                : new(K, Severity.Info, "perf.ram.full", ram));
        }
        var busy = others.Where(p => p.CpuAverage >= 1 || p.GpuAverage >= 1).OrderByDescending(p => p.CpuAverage + p.GpuAverage).Take(3).ToList();
        if (busy.Sum(p => p.CpuAverage) >= 5 || busy.Sum(p => p.GpuAverage) >= 5)
            list.Add(new(K, Severity.Info, "game.background", title, string.Join(", ", busy.Select(p =>
                Loc.T(p.GpuAverage >= 1 ? "game.background.item.gpu" : "game.background.item", ProgramAdvisor.NameOf(p), p.CpuAverage, p.GpuAverage)))));
    }

    /// <summary>The name the game goes by: its settings', its file details', its Steam folder's, or at worst its exe's.</summary>
    private static string TitleOf(GameUsage u, GameSettings? settings)
    {
        if (settings?.Source is "VALORANT" or "Counter-Strike 2" or "Minecraft") return settings.Source;
        if (u.Title is { Length: > 1 } title && !GenericTitle().IsMatch(title)) return title;
        return PastAnalyzer.TitleOf(u.Name, u.Path);
    }

    [GeneratedRegex(@"^(game|launcher|bootstrap\w*|unreal\w*|unity\w*|.*\bengine)$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTitle();

}
