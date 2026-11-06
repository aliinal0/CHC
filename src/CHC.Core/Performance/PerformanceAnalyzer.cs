using CHC.Core.Diagnostics;
using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Network;
using CHC.Core.Presentation;

namespace CHC.Core.Performance;

/// <summary>
/// Explains a game session: what limited the frame rate, why it stuttered, and which
/// settings hold performance back. Findings use <see cref="ComponentKind.Performance"/>.
/// </summary>
public static class PerformanceAnalyzer
{
    private static readonly Guid PowerSaverPlan = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid HighPerformancePlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformancePlan = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly int[] CommonCaps = [30, 60, 75, 90, 120, 144, 165, 170, 180, 200, 240, 280, 360];

    /// <summary>The first seconds are loading and alt-tabbing; ignore them.</summary>
    private static List<PerfSample> Usable(PerfSession s) => s.Samples.Where(x => x.Second >= 3).ToList();

    public static List<Finding> Analyze(SystemSnapshot snapshot, PerfSession? session)
    {
        const ComponentKind k = ComponentKind.Performance;
        var list = new List<Finding>();

        // Settings worth checking before playing at all.
        var g = snapshot.Gaming;
        if (g.RefreshHz > 0 && g.MaxRefreshHz > g.RefreshHz + 5)
            list.Add(new(k, Severity.Warning, "perf.refresh.low", g.RefreshHz, g.MaxRefreshHz));
        if (snapshot.Power.PlanGuid == PowerSaverPlan)
            list.Add(new(k, Severity.Warning, "perf.powersaver", Fmt.Or(snapshot.Power.PlanName)));
        else if (snapshot.Power.ProcessorMaxState is < 100 and var maxState)
            list.Add(new(k, Severity.Warning, "perf.cpu.maxstate", maxState));
        if (g.GameMode == false)
            list.Add(new(k, Severity.Info, "perf.gamemode.off"));

        // BIOS and Windows settings that cost FPS.
        if (snapshot.Power.CpuBoostMode == 0)
            list.Add(new(k, Severity.Warning, "perf.cpu.boostoff"));
        var card = snapshot.Gpus.FirstOrDefault(x => !x.IsIntegrated && !x.UsesBasicDriver);
        if (card is { ResizableBar: false } && Catalog.SupportsResizableBar(card.Name))
            list.Add(new(k, card.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? Severity.Warning : Severity.Info, "perf.rebar.off", card.Name));
        if (card is not null && g.HardwareGpuScheduling == false && Catalog.SupportsFrameGeneration(card.Name))
            list.Add(new(k, Severity.Info, "perf.hags.framegen", card.Name));
        if (g.BackgroundRecording == true)
            list.Add(new(k, Severity.Info, "perf.gamedvr"));
        if (g.MemoryIntegrity == true)
            list.Add(new(k, Severity.Info, "perf.hvci"));
        if (g.MouseAcceleration == true)
            list.Add(new(k, Severity.Info, "perf.mouse.accel"));
        // The switch only exists on Windows 11 (build 22000 and later).
        if (g.WindowedOptimizations == false && int.TryParse(snapshot.Os.Build.Split('.')[0], out var build) && build >= 22000)
            list.Add(new(k, Severity.Info, "perf.windowed.off"));
        if (card is not null && g.PowerSavingGames.Count > 0 && snapshot.Gpus.Any(x => x.IsIntegrated))
            list.Add(new(k, Severity.Warning, "perf.gpu.powersaving", string.Join(", ", g.PowerSavingGames), card.Name));

        // Two core dies, only one with the extra cache: AMD's driver and core parking keep games on the right one.
        if (Catalog.IsDualDieX3D(snapshot.Cpu.Name))
        {
            if (g.VCacheDriver == false)
                list.Add(new(k, Severity.Warning, "perf.x3d.driver", snapshot.Cpu.Name) { IsUpdate = true, ActionUrl = CHC.Core.Updates.MakerPages.AmdDrivers });
            if (snapshot.Power.PlanGuid is { } plan && (plan == HighPerformancePlan || plan == UltimatePerformancePlan))
                list.Add(new(k, Severity.Info, "perf.x3d.powerplan", snapshot.Cpu.Name, Fmt.Or(snapshot.Power.PlanName)));
        }

        if (session is null) return list;

        if (!session.FpsMeasured)
            list.Add(new(k, Severity.Info, "perf.nofps"));

        var samples = Usable(session);
        if (samples.Count < 10)
        {
            list.Add(new(k, Severity.Info, "perf.tooshort"));
            return list;
        }

        if (Breakdown(session) is { } b)
        {
            if (b.Cpu >= 30)
            {
                var avgCpu = samples.Average(x => x.CpuLoad ?? 0);
                list.Add(new(k, Severity.Warning, avgCpu < 60 ? "perf.cpubound.single" : "perf.cpubound", b.Cpu, avgCpu));
                var modules = snapshot.Memory.Modules;
                if (modules.Count == 1 && snapshot.Memory.SlotCount >= 2)
                    list.Add(new(k, Severity.Info, "perf.singlechannel"));
            }
            if (b.Gpu >= 60)
                list.Add(new(k, Severity.Info, "perf.gpubound", b.Gpu));
            if (b.Capped >= 50 && DetectCap(session) is { } cap)
                list.Add(new(k, Severity.Info, "perf.capped", cap, b.Capped));
        }

        CpuHeat(samples, list);
        GpuHeat(samples, list);

        var powerLimited = samples.Count(x => x.GpuPowerPercent >= 97 && x.GpuLoad >= 90);
        if (Share(powerLimited, samples) >= 30)
            list.Add(new(k, Severity.Info, "perf.gpu.powerlimit", Share(powerLimited, samples)));

        Battery(samples, list);
        Power(snapshot, session, list);

        var vramFull = samples.Where(x => x is { VramUsedMb: > 0, VramTotalMb: > 0 } && x.VramUsedMb / x.VramTotalMb >= 0.95).ToList();
        if (Share(vramFull.Count, samples) >= 10)
        {
            var peak = samples.Max(x => x.VramUsedMb ?? 0) / 1024;
            var total = samples.Max(x => x.VramTotalMb ?? 0) / 1024;
            list.Add(new(k, Severity.Warning, "perf.vram.full", peak, total));
        }

        var ramFull = samples.Count(x => x.RamLoad >= 90);
        var ramPeak = samples.Max(x => x.RamLoad ?? 0);
        if (Share(ramFull, samples) >= 10)
            list.Add(new(k, Severity.Warning, "perf.ram.full", ramPeak));
        else if (ramPeak >= 95)
            list.Add(new(k, Severity.Warning, "perf.ram.peak", ramPeak, Fmt.Bytes(snapshot.Memory.TotalBytes, 0)));

        Stutters(session, samples, list);

        // Uneven frame pacing without distinct hitches: the 1% lows fall far below the average.
        if (session.FpsMeasured && session.StutterTimes.Count < 3 && session.AverageFps > 0 && session.OnePercentLowFps < session.AverageFps * 0.4)
            list.Add(new(k, Severity.Info, "perf.lows", session.OnePercentLowFps, session.AverageFps));

        if (session.Background.FirstOrDefault() is { AverageCpu: >= 4 } top)
            list.Add(new(k, top.AverageCpu >= 10 ? Severity.Warning : Severity.Info, "perf.background", top.Name, top.AverageCpu));

        if (session.GamePath.Length >= 2 && session.GamePath[1] == ':')
        {
            var letter = session.GamePath[..2];
            var disk = snapshot.Disks.FirstOrDefault(d => d.DriveLetters.Contains(letter, StringComparer.OrdinalIgnoreCase));
            if (disk?.Media == DiskMedia.Hdd)
                list.Add(new(k, Severity.Info, "perf.game.hdd", session.Game, disk.Model));
        }

        GameSettingsTips(snapshot, session, samples, Share(vramFull.Count, samples) >= 10, list);

        if (session.Online && session.Network is { } net)
            Online(session, net, list);

        if (session.FpsMeasured && list.All(f => f.Severity < Severity.Warning))
            list.Add(new(k, Severity.Good, "perf.good", session.AverageFps));
        return list;
    }

    /// <summary>
    /// Online games: the ping to the game server (or to 1.1.1.1 when the server ignores pings), then where
    /// packet loss, jitter and lag spikes came from while playing.
    /// </summary>
    private static void Online(PerfSession session, NetSession net, List<Finding> list)
    {
        const ComponentKind k = ComponentKind.Performance;
        var server = net.ServerStats;
        var internet = net.InternetStats;
        var (ping, target) = server.HasData ? (server, net.ServerAddress) : (internet, net.InternetTarget);
        if (ping.HasData)
            list.Add(new(k, Severity.Info, "perf.online", Fmt.Or(session.Game), ping.Median, ping.Jitter, ping.LossPercent, target));
        if (net.ServerAddress.Length > 0 && server.Sent >= 10 && !server.HasData)
            list.Add(new(k, Severity.Info, "perf.server.silent", net.ServerAddress));
        if (server.HasData && server.Median >= 80)
            list.Add(new(k, server.Median >= 150 ? Severity.Warning : Severity.Info, "perf.server.high", server.Median, internet.Median));
        // Loss only on the way to the game server, not to 1.1.1.1: the server or its route, not the home connection.
        if (server.HasData && server.LossPercent >= 2 && internet.LossPercent < 1)
            list.Add(new(k, Severity.Warning, "perf.server.loss", server.LossPercent));
        NetworkAnalyzer.Session(net, list, summary: false);
    }

    /// <summary>
    /// What the game's own settings and its window add to the measurements: a monitor run below its refresh rate,
    /// a stretched resolution, a window, VSync or a frame limit that holds FPS down, and which settings to change
    /// for more FPS (graphics card limited) or a better picture at the same FPS (processor limited).
    /// </summary>
    private static void GameSettingsTips(SystemSnapshot snapshot, PerfSession session, List<PerfSample> samples, bool vramFull, List<Finding> list)
    {
        const ComponentKind k = ComponentKind.Performance;
        var s = session.Settings;
        var d = session.Display;
        var g = snapshot.Gaming;
        var game = GameTitle(session);
        var maxHz = d is { MaxRefreshHz: > 0 } ? d.MaxRefreshHz : g.MaxRefreshHz;
        var (nativeWidth, nativeHeight) = d is { NativeWidth: > 0 } ? (d.NativeWidth, d.NativeHeight) : (g.Width, g.Height);
        var b = Breakdown(session);
        var gpuLoad = samples.Where(x => x.GpuLoad is not null).Select(x => x.GpuLoad!.Value).DefaultIfEmpty(0).Average();

        // The game's fullscreen mode ran the monitor slower than it can (the desktop itself is checked separately).
        if (d is { RefreshHz: > 0 } && maxHz > d.RefreshHz + 5 && g.RefreshHz >= g.MaxRefreshHz - 5)
            list.Add(new(k, Severity.Warning, "perf.game.lowhz", game, d.RefreshHz, maxHz));

        // Exclusive fullscreen below the monitor's resolution: measured from the monitor's mode, or read from the settings.
        var (width, height) = d is { ModeWidth: > 0 } && d.ModeWidth < nativeWidth ? (d.ModeWidth, d.ModeHeight)
            : s is { DisplayMode: GameDisplayMode.Fullscreen, Width: { } sw, Height: { } sh } && sw < nativeWidth ? (sw, sh) : (0, 0);
        if (width > 0)
            list.Add(new(k, Severity.Info, "perf.game.resolution", game, $"{width} × {height}", $"{nativeWidth} × {nativeHeight}"));

        if (s?.DisplayMode == GameDisplayMode.Windowed || d is { CoversMonitor: false, HasTitleBar: true })
            list.Add(new(k, Severity.Info, "perf.game.windowed", game));

        if (s?.VSync == true && maxHz > 0)
            list.Add(new(k, Severity.Info, "perf.game.vsync", game, maxHz, maxHz - 3));
        if (s?.FrameLimit is > 0 and var limit && maxHz > 0 && limit < maxHz - 5 && gpuLoad < 90)
            list.Add(new(k, Severity.Info, "perf.game.fpslimit", game, limit, maxHz, maxHz - 3));
        // The game's own setting says more than "FPS was capped".
        if (list.Any(f => f.Key is "perf.game.vsync" or "perf.game.fpslimit"))
            list.RemoveAll(f => f.Key == "perf.capped");

        if (s?.RenderScale is < 95 and var low)
            list.Add(new(k, Severity.Info, "perf.game.renderscale.low", game, low));
        else if (s?.RenderScale is > 105 and var high && b is { Gpu: >= 60 })
            list.Add(new(k, Severity.Info, "perf.game.renderscale.high", game, high));

        // Graphics card limited and below the monitor's refresh rate (at most 144): what to lower first.
        var target = maxHz > 0 ? Math.Min(maxHz, 144) : 60;
        if (s is not null && b is { Gpu: >= 60 } && session.FpsMeasured && session.AverageFps < target * 0.9)
        {
            var heavy = s.Quality.Where(q => q.IsHigh && GameSettings.GpuHeavy.Contains(q.Setting)).OrderByDescending(q => q.Level).Take(4).ToList();
            if (heavy.Count > 0)
                list.Add(new(k, Severity.Info, "perf.game.gpuheavy", session.AverageFps, Items(heavy)));
            // With a render scale far from 100% that tip comes first; upscaling replaces it.
            if (s.UpscalerKnown && s.Upscaler.Length == 0 && s.RenderScale is null or (>= 95 and <= 105))
                list.Add(new(k, Severity.Info, "perf.game.upscaler", game, UpscalerFor(snapshot)));
            if (s.Reflex == false && snapshot.Gpus.Any(x => !x.IsIntegrated && x.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
                list.Add(new(k, Severity.Info, "perf.game.reflex", game));
        }

        // Processor limited while the graphics card waits: a better picture costs nothing, processor settings give FPS.
        if (b is { Cpu: >= 30 } && gpuLoad < 70)
        {
            var cpuHeavy = s?.Quality.Where(q => q.IsHigh && GameSettings.CpuHeavy.Contains(q.Setting)).ToList() ?? [];
            object lower = s is { RenderDistance: >= 16 } or { SimulationDistance: >= 12 }
                ? new LocFormat("gameset.mc.distances", s.RenderDistance, s.SimulationDistance)
                : cpuHeavy.Count > 0 ? Items(cpuHeavy) : new LocKey("perf.game.cpuheavy.generic");
            list.Add(new(k, Severity.Info, "perf.game.cpuheadroom", gpuLoad, lower));
        }

        if (vramFull && s?.Quality.FirstOrDefault(q => q.Setting == "textures") is { IsHigh: true } textures)
            list.Add(new(k, Severity.Warning, "perf.game.textures", game, new LocKey(textures.LevelKey)));
    }

    /// <summary>"Shadows (Epic), Effects (High)", translated when shown.</summary>
    internal static LocJoin Items(IEnumerable<GameQuality> settings) =>
        new(settings.Select(q => (object)new LocFormat("gameset.item", new LocKey("gameset." + q.Setting), new LocKey(q.LevelKey))).ToList());

    /// <summary>The game's own name when its settings say it, otherwise the exe's name.</summary>
    private static string GameTitle(PerfSession session) =>
        session.Settings?.Source is "VALORANT" or "Counter-Strike 2" or "Minecraft" ? session.Settings.Source
        : session.Game.Length > 0 ? session.Game : "?";

    /// <summary>The upscaler the graphics card does best: DLSS on GeForce RTX, XeSS on Intel Arc, otherwise FSR.</summary>
    internal static string UpscalerFor(SystemSnapshot snapshot)
    {
        var card = snapshot.Gpus.FirstOrDefault(x => !x.IsIntegrated && !x.UsesBasicDriver)?.Name ?? "";
        return card.Contains("RTX", StringComparison.OrdinalIgnoreCase) ? "DLSS"
            : card.Contains("Arc", StringComparison.OrdinalIgnoreCase) ? "XeSS"
            : "FSR";
    }

    /// <summary>
    /// Laptops: playing on battery (the processor and graphics card get much less power), or a charger
    /// that can't keep up, so the battery drains while plugged in.
    /// </summary>
    private static void Battery(List<PerfSample> samples, List<Finding> list)
    {
        const ComponentKind k = ComponentKind.Performance;
        var battery = samples.Where(x => x.OnBattery is not null).ToList();
        if (battery.Count == 0) return;

        var onBattery = battery.Count(x => x.OnBattery == true);
        if (Share(onBattery, samples) >= 30)
        {
            list.Add(new(k, Severity.Warning, "perf.battery", Share(onBattery, samples)));
            return;
        }

        // Plugged in, yet the battery supplied power for a good part of the session.
        var draining = battery.Where(x => x.OnBattery == false && x.BatteryRateW <= -2).ToList();
        if (Share(draining.Count, samples) >= 20)
            list.Add(new(k, Severity.Warning, "perf.charger.weak", draining.Average(x => -x.BatteryRateW!.Value), BatteryDrop(battery)));
    }

    /// <summary>Charge (percentage points) the battery lost from the start to the end of the session.</summary>
    public static double BatteryDrop(List<PerfSample> samples)
    {
        var levels = samples.Where(x => x.BatteryPercent is not null).Select(x => x.BatteryPercent!.Value).ToList();
        return levels.Count < 2 ? 0 : Math.Max(0, levels[0] - levels[^1]);
    }

    /// <summary>How much the PC drew while gaming and, for desktops, the power supply that suits it.</summary>
    private static void Power(SystemSnapshot snapshot, PerfSession session, List<Finding> list)
    {
        const ComponentKind k = ComponentKind.Performance;
        if (PowerOf(snapshot, session) is not { } p) return;
        // The chassis type, not a battery: a desktop on a USB UPS also reports one.
        if (snapshot.Board.IsLaptop)
        {
            list.Add(new(k, Severity.Info, "perf.power.laptop", p.Average));
            return;
        }
        list.Add(new(k, Severity.Info, "perf.power", p.Peak, p.Rest, p.RecommendedPsuW));
        // Sudden power-offs and a hungry PC point at the power supply.
        if (snapshot.Stability.PowerLosses > 0 && p.Peak >= 250)
            list.Add(new(k, Severity.Warning, "perf.psu.shutdowns", snapshot.Stability.PowerLosses, p.Peak, p.RecommendedPsuW));
    }

    /// <summary>
    /// Power drawn in the session, or null without processor power readings, or when a separate graphics card
    /// gave none (half a figure would mislead). Graphics built into the processor are part of the processor's power.
    /// </summary>
    public static PerfPower? PowerOf(SystemSnapshot snapshot, PerfSession session)
    {
        var samples = Usable(session);
        var cpu = samples.Where(x => x.CpuPowerW > 0).Select(x => x.CpuPowerW!.Value).ToList();
        if (cpu.Count < 10) return null;
        var discrete = snapshot.Gpus.Any(x => !x.IsIntegrated && !x.IsVirtual && !x.UsesBasicDriver);
        List<double>? gpu = null;
        if (discrete)
        {
            gpu = samples.Where(x => x.GpuPowerW > 0).Select(x => x.GpuPowerW!.Value).ToList();
            if (gpu.Count < 10) return null;
        }
        // Not measurable: motherboard, memory, disks, fans and USB devices (a laptop: its screen and board).
        var rest = snapshot.Board.IsLaptop ? 15 : 60;
        return new PerfPower(cpu.Average(), PerformanceMonitor.Percentile(cpu, 98),
            gpu?.Average(), gpu is null ? null : PerformanceMonitor.Percentile(gpu, 98), rest);
    }

    /// <summary>Share of the session (in %) limited by the GPU, the CPU, a frame cap, or nothing obvious.</summary>
    public static PerfBreakdown? Breakdown(PerfSession session)
    {
        var samples = Usable(session).Where(x => x.GpuLoad is not null && Rendering(session, x)).ToList();
        if (samples.Count < 10) return null;
        var cap = DetectCap(session);
        int gpu = 0, cpu = 0, capped = 0, other = 0;
        foreach (var x in samples)
        {
            if (cap is { } c && x.Fps is { } f && Math.Abs(f - c) <= c * 0.04 && x.GpuLoad < 90) capped++;
            else if (x.GpuLoad >= 95) gpu++;
            // The graphics card is waiting and a core is busy. The game's main thread moves between cores,
            // so a single core rarely shows 100% even when that thread is what holds the game back.
            else if (x.GpuLoad < 85 && (x.CpuMaxCoreLoad >= 70 || x.CpuLoad >= 85)) cpu++;
            else other++;
        }
        double P(int n) => 100.0 * n / samples.Count;
        return new PerfBreakdown(P(gpu), P(cpu), P(capped), P(other));
    }

    /// <summary>
    /// The game was drawing in this second: its frames were counted, or, without frame data, the graphics card
    /// was busy. On an idle desktop a busy core says nothing about what limits a game.
    /// </summary>
    private static bool Rendering(PerfSession session, PerfSample x) => session.FpsMeasured ? x.Fps > 0 : x.GpuLoad >= 20;

    /// <summary>A steady frame rate right at the refresh rate or a round number means VSync or a frame limiter.</summary>
    public static int? DetectCap(PerfSession session)
    {
        var fps = Usable(session).Where(x => x.Fps > 0).Select(x => x.Fps!.Value).ToList();
        if (fps.Count < 10) return null;
        var median = PerformanceMonitor.Percentile(fps, 50);
        var steady = fps.Count(f => Math.Abs(f - median) <= median * 0.04) >= fps.Count * 0.6;
        if (!steady) return null;
        // Besides round numbers: the refresh rate, the usual "refresh minus 3" G-Sync/FreeSync cap,
        // and NVIDIA Reflex's automatic cap (refresh - refresh² / 3600).
        var refresh = session.RefreshRateHz;
        var candidates = CommonCaps.Concat(refresh > 0 ? [refresh, refresh - 3, (int)Math.Round(refresh - refresh * refresh / 3600.0)] : []).Where(c => c > 0);
        return candidates.Where(c => Math.Abs(median - c) <= c * 0.03).OrderBy(c => Math.Abs(median - c)).Cast<int?>().FirstOrDefault();
    }

    private static void CpuHeat(List<PerfSample> samples, List<Finding> list)
    {
        var hot = samples.Where(x => x.CpuTemp >= 95).ToList();
        if (Share(hot.Count, samples) < 10) return;
        var maxTemp = hot.Max(x => x.CpuTemp ?? 0);
        var peakClock = samples.Max(x => x.CpuClock ?? 0);
        var hotClock = hot.Where(x => x.CpuClock > 0).Select(x => x.CpuClock!.Value).DefaultIfEmpty(peakClock).Average();
        var drop = peakClock > 0 ? 100 * (1 - hotClock / peakClock) : 0;
        list.Add(drop >= 10
            ? new(ComponentKind.Performance, Severity.Warning, "perf.cpu.thermal", maxTemp, drop)
            : new(ComponentKind.Performance, Severity.Info, "perf.cpu.hot", maxTemp));
    }

    private static void GpuHeat(List<PerfSample> samples, List<Finding> list)
    {
        var loaded = samples.Where(x => x.GpuLoad >= 80 && x.GpuClock > 0).ToList();
        var hot = loaded.Where(x => x.GpuTemp >= 83 || x.GpuHotSpot >= 100).ToList();
        if (loaded.Count == 0 || Share(hot.Count, samples) < 10) return;
        var reference = PerformanceMonitor.Percentile(loaded.Select(x => x.GpuClock!.Value).ToList(), 95);
        var hotClock = hot.Average(x => x.GpuClock!.Value);
        var drop = reference > 0 ? 100 * (1 - hotClock / reference) : 0;
        var maxTemp = hot.Max(x => Math.Max(x.GpuTemp ?? 0, x.GpuHotSpot ?? 0));
        list.Add(drop >= 8
            ? new(ComponentKind.Performance, Severity.Warning, "perf.gpu.thermal", maxTemp, drop)
            : new(ComponentKind.Performance, Severity.Info, "perf.gpu.hot", maxTemp));
    }

    /// <summary>Counts hitches and looks at what else was going on in the seconds they happened.</summary>
    private static void Stutters(PerfSession session, List<PerfSample> samples, List<Finding> list)
    {
        var count = session.StutterTimes.Count;
        var minutes = Math.Max(0.5, session.Duration.TotalMinutes);
        var perMinute = count / minutes;
        if (count < 3) return;

        var votes = new Dictionary<string, int>();
        foreach (var x in samples.Where(x => x.Stutters > 0))
        {
            var cause = x.DiskActivity >= 80 ? "disk"
                : x is { VramUsedMb: > 0, VramTotalMb: > 0 } && x.VramUsedMb / x.VramTotalMb >= 0.95 ? "vram"
                : x.RamLoad >= 90 ? "ram"
                : x.CpuMaxCoreLoad >= 95 ? "cpu"
                : "unknown";
            votes[cause] = votes.GetValueOrDefault(cause) + x.Stutters;
        }
        var likely = votes.Where(v => v.Key != "unknown").OrderByDescending(v => v.Value).Select(v => v.Key).FirstOrDefault() ?? "unknown";
        list.Add(new(ComponentKind.Performance, perMinute >= 4 ? Severity.Warning : Severity.Info, "perf.stutter", count, perMinute, new LocKey("perf.stuttercause." + likely)));
    }

    private static double Share(int count, List<PerfSample> samples) => samples.Count == 0 ? 0 : 100.0 * count / samples.Count;
}
