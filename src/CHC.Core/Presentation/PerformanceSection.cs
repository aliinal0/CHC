using CHC.Core.Diagnostics;
using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Performance;

namespace CHC.Core.Presentation;

/// <summary>Display model for the game performance page and the report section.</summary>
public static class PerformanceSection
{
    public static ComponentSection Build(SystemSnapshot s, PerfSession? session)
    {
        var findings = PerformanceAnalyzer.Analyze(s, session);
        var health = new ComponentHealth(ComponentKind.Performance, findings, session is not null);
        var section = new ComponentSection
        {
            Kind = ComponentKind.Performance,
            Title = Loc.T("component.performance"),
            Summary = session is null ? Loc.T("perf.summary.none") : Loc.T("summary." + health.Severity.ToString().ToLowerInvariant()),
            Severity = health.Severity,
            Score = health.Score,
            Findings = findings.OrderByDescending(f => f.Severity).ToList(),
        };
        if (session is not null) section.Devices.Add(Session(s, session));
        if (session is { Settings: not null } or { Display: not null }) section.Devices.Add(GameSettingsDevice(session));
        if (session?.Network is { } net)
        {
            var connection = NetworkSection.SessionDevice(net, Loc.T("perf.network.title"));
            connection.Specs.Insert(0, new SpecGroup(Loc.T("perf.group.online"), new[]
            {
                Row("perf.spec.server", session.ServerAddress),
                Row("perf.spec.gamedata", session.GameBytes > 0 ? Fmt.Bytes(session.GameBytes) : ""),
            }.OfType<SpecRow>().ToList()));
            connection.Specs.RemoveAll(g => g.Rows.Count == 0);
            section.Devices.Add(connection);
        }
        section.Devices.Add(Settings(s));
        return section;
    }

    private static DeviceSection Session(SystemSnapshot s, PerfSession p)
    {
        var d = new DeviceSection
        {
            Title = p.Game.Length > 0 ? p.Game : Loc.T("perf.unknowngame"),
            Subtitle = Loc.T("perf.session.subtitle", Fmt.DateAndTime(p.Started), Fmt.Elapsed(p.Duration)),
        };
        d.Highlights.Add(new(Loc.T("label.avgfps"), p.FpsMeasured ? Fmt.Number(p.AverageFps, "0") : Fmt.Dash));
        d.Highlights.Add(new(Loc.T("label.onepercentlow"), p.FpsMeasured ? Fmt.Number(p.OnePercentLowFps, "0") : Fmt.Dash));
        d.Highlights.Add(new(Loc.T("label.pointonepercentlow"), p.FpsMeasured ? Fmt.Number(p.PointOnePercentLowFps, "0") : Fmt.Dash));
        d.Highlights.Add(new(Loc.T("label.stutters"), p.FpsMeasured ? p.StutterTimes.Count.ToString() : Fmt.Dash));

        if (PerformanceAnalyzer.Breakdown(p) is { } b)
        {
            d.Meters.Add(new(Loc.T("perf.limit.gpu"), b.Gpu, Fmt.Percent(b.Gpu), Severity.Info));
            d.Meters.Add(new(Loc.T("perf.limit.cpu"), b.Cpu, Fmt.Percent(b.Cpu), b.Cpu >= 30 ? Severity.Warning : Severity.Good));
            d.Meters.Add(new(Loc.T("perf.limit.cap"), b.Capped, Fmt.Percent(b.Capped), Severity.Good));
            d.Meters.Add(new(Loc.T("perf.limit.other"), b.Other, Fmt.Percent(b.Other), Severity.Good));
        }

        var samples = p.Samples.Where(x => x.Second >= 3).ToList();
        string Avg(Func<PerfSample, double?> pick, Func<double?, string> format) =>
            samples.Select(pick).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Average() is var a && !double.IsNaN(a) ? format(a) : "";
        string Peak(Func<PerfSample, double?> pick, Func<double?, string> format) =>
            samples.Select(pick).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(double.NaN).Max() is var m && !double.IsNaN(m) ? format(m) : "";

        d.Specs.Add(new SpecGroup(Loc.T("perf.group.frames"), new[]
        {
            Row("label.avgfps", p.FpsMeasured ? Fmt.Number(p.AverageFps, "0.0") : ""),
            Row("perf.spec.median", p.FpsMeasured ? Fmt.Number(p.MedianFrameMs, "0.0") + " ms" : ""),
            Row("label.onepercentlow", p.FpsMeasured ? Fmt.Number(p.OnePercentLowFps, "0.0") : ""),
            Row("label.pointonepercentlow", p.FpsMeasured ? Fmt.Number(p.PointOnePercentLowFps, "0.0") : ""),
            Row("perf.spec.frames", p.FpsMeasured ? Fmt.Number(p.Frames) : ""),
            Row("perf.spec.refresh", p.RefreshRateHz > 0 ? $"{p.RefreshRateHz} Hz" : ""),
        }.OfType<SpecRow>().ToList()));
        d.Specs.Add(new SpecGroup(Loc.T("perf.group.hardware"), new[]
        {
            Row("perf.spec.cpuload", Avg(x => x.CpuLoad, v => Fmt.Percent(v))),
            Row("perf.spec.cpucore", Avg(x => x.CpuMaxCoreLoad, v => Fmt.Percent(v))),
            Row("perf.spec.cputemp", Peak(x => x.CpuTemp, Fmt.Temp)),
            Row("perf.spec.gpuload", Avg(x => x.GpuLoad, v => Fmt.Percent(v))),
            Row("perf.spec.gputemp", Peak(x => x.GpuTemp, Fmt.Temp)),
            Row("perf.spec.vram", Peak(x => x.VramUsedMb, v => Fmt.Bytes((ulong)((v ?? 0) * 1024 * 1024)))),
            Row("perf.spec.ram", Peak(x => x.RamLoad, v => Fmt.Percent(v))),
        }.OfType<SpecRow>().ToList()));

        d.Specs.Add(Power(s, p, samples));
        d.Specs.RemoveAll(group => group.Rows.Count == 0);

        if (p.Background.Count > 0)
        {
            d.Tables.Add(new TableBlock(Loc.T("perf.table.background"),
                [Loc.T("col.program"), Loc.T("perf.col.avgcpu"), Loc.T("perf.col.peakcpu")],
                p.Background.Select(b => (IReadOnlyList<string>)[b.Name, Fmt.Percent(b.AverageCpu, 1), Fmt.Percent(b.PeakCpu, 1)]).ToList()));
        }
        return d;
    }

    /// <summary>Measured power of the processor and graphics card, the estimated total, and the battery on laptops.</summary>
    private static SpecGroup Power(SystemSnapshot s, PerfSession p, List<PerfSample> samples)
    {
        static string AvgPeak(double average, double peak) => $"{Fmt.Watts(Math.Round(average))} / {Fmt.Watts(Math.Round(peak))}";
        var rows = new List<SpecRow?>();
        if (PerformanceAnalyzer.PowerOf(s, p) is not { } w)
        {
            // Without administrator rights the processor's power is often unreadable; show what was measured.
            var gpu = samples.Where(x => x.GpuPowerW > 0).Select(x => x.GpuPowerW!.Value).ToList();
            if (gpu.Count >= 10 && s.Gpus.Any(g => !g.IsIntegrated && !g.IsVirtual && !g.UsesBasicDriver))
                rows.Add(Row("perf.spec.gpupower", AvgPeak(gpu.Average(), PerformanceMonitor.Percentile(gpu, 98))));
        }
        else
        {
            rows.Add(Row("perf.spec.cpupower", AvgPeak(w.CpuAverage, w.CpuPeak)));
            if (w.GpuAverage is { } gpuAverage && w.GpuPeak is { } gpuPeak)
                rows.Add(Row("perf.spec.gpupower", AvgPeak(gpuAverage, gpuPeak)));
            rows.Add(Row("perf.spec.restpower", "≈ " + Fmt.Watts(w.Rest)));
            rows.Add(Row("perf.spec.systempower", "≈ " + AvgPeak(w.Average, w.Peak)));
            if (!s.Board.IsLaptop) rows.Add(Row("perf.spec.psu", Loc.T("perf.spec.psu.value", w.RecommendedPsuW)));
        }

        var battery = samples.Where(x => x.OnBattery is not null).ToList();
        if (battery.Count > 0)
        {
            var onBattery = battery.Where(x => x.OnBattery == true).ToList();
            rows.Add(Row("perf.spec.onbattery", Fmt.Percent(100.0 * onBattery.Count / samples.Count)));
            // On battery the battery feeds the whole laptop: this is its real total power.
            var drained = battery.Where(x => x.BatteryRateW < -0.5).Select(x => -x.BatteryRateW!.Value).ToList();
            if (drained.Count > 0) rows.Add(Row("perf.spec.batterydrain", Fmt.Watts(Math.Round(drained.Average(), 1))));
            var levels = battery.Where(x => x.BatteryPercent is not null).Select(x => x.BatteryPercent!.Value).ToList();
            if (levels.Count >= 2) rows.Add(Row("perf.spec.batterylevel", $"{Fmt.Percent(levels[0])} → {Fmt.Percent(levels[^1])}"));
            var drain = onBattery.Where(x => x.BatteryRateW < -0.5).Select(x => -x.BatteryRateW!.Value).ToList();
            if (drain.Count >= 10 && s.Battery?.FullChargeCapacityMWh is { } full and > 0)
                rows.Add(Row("perf.spec.fullruntime", Fmt.Duration(TimeSpan.FromHours(full / 1000.0 / drain.Average()))));
        }
        return new SpecGroup(Loc.T("perf.group.power"), rows.OfType<SpecRow>().ToList());
    }

    /// <summary>The game's own settings (when its settings file was found) next to what was measured on screen.</summary>
    private static DeviceSection GameSettingsDevice(PerfSession p)
    {
        var g = p.Settings;
        var d = new DeviceSection
        {
            Title = Loc.T("perf.gameset.title"),
            Subtitle = g is null ? Loc.T("perf.gameset.nosource") : Loc.T("perf.gameset.source", g.Source, g.File),
        };
        var display = p.Display;
        string Mode(GameDisplayMode? mode) => mode is { } m ? Loc.T("perf.gameset.mode." + m.ToString().ToLowerInvariant()) : "";
        d.Specs.Add(new SpecGroup(Loc.T("perf.gameset.group.display"), new[]
        {
            Row("perf.gameset.resolution", g is { Width: { } w, Height: { } h } ? $"{w} × {h}" : ""),
            Row("perf.gameset.mode", Mode(g?.DisplayMode)),
            Row("perf.gameset.window", display is null ? "" : Loc.T(display.CoversMonitor ? "perf.gameset.window.full" : "perf.gameset.window.windowed", display.WindowWidth, display.WindowHeight)),
            Row("perf.gameset.refresh", display is { RefreshHz: > 0 } ? Loc.T("perf.gameset.refresh.value", display.ModeWidth, display.ModeHeight, display.RefreshHz) : ""),
            Row("perf.gameset.monitor", display is { NativeWidth: > 0 } ? Loc.T("perf.gameset.refresh.value", display.NativeWidth, display.NativeHeight, display.MaxRefreshHz) : ""),
            Row("perf.gameset.vsync", g?.VSync is { } vsync ? Fmt.OnOff(vsync) : ""),
            Row("perf.gameset.fpslimit", g?.FrameLimit is { } limit ? limit == 0 ? Loc.T("perf.gameset.nolimit") : $"{limit} FPS" : ""),
            Row("perf.gameset.renderscale", g?.RenderScale is { } scale ? Fmt.Percent(scale) : ""),
            Row("perf.gameset.upscaler", g is { UpscalerKnown: true } ? g.Upscaler.Length > 0 ? g.Upscaler : Fmt.OnOff(false) : ""),
            Row("perf.gameset.reflex", g?.Reflex is { } reflex ? Fmt.OnOff(reflex) : ""),
            Row("perf.gameset.renderdistance", g?.RenderDistance is { } rd ? Loc.T("perf.gameset.chunks", rd) : ""),
            Row("perf.gameset.simulationdistance", g?.SimulationDistance is { } sd ? Loc.T("perf.gameset.chunks", sd) : ""),
        }.OfType<SpecRow>().ToList()));
        if (g is { Quality.Count: > 0 })
            d.Specs.Add(new SpecGroup(Loc.T("perf.gameset.group.quality"), g.Quality.Select(q => Row("gameset." + q.Setting, Loc.T(q.LevelKey))).OfType<SpecRow>().ToList()));
        d.Specs.RemoveAll(group => group.Rows.Count == 0);
        return d;
    }

    private static DeviceSection Settings(SystemSnapshot s)
    {
        var g = s.Gaming;
        var d = new DeviceSection { Title = Loc.T("perf.settings.title"), Subtitle = Loc.T("perf.settings.subtitle") };
        d.Specs.Add(new SpecGroup(Loc.T("spec.group.display"), new[]
        {
            Row("spec.resolution", g.Width > 0 ? $"{g.Width} × {g.Height}" : ""),
            Row("spec.refresh", g.RefreshHz > 0 ? $"{g.RefreshHz} Hz" : ""),
            Row("perf.spec.maxrefresh", g.MaxRefreshHz > 0 ? $"{g.MaxRefreshHz} Hz" : ""),
            Row("perf.spec.native", g.NativeWidth > 0 ? $"{g.NativeWidth} × {g.NativeHeight}" : ""),
        }.OfType<SpecRow>().ToList()));
        d.Specs.Add(new SpecGroup(Loc.T("perf.group.windows"), new[]
        {
            Row("perf.spec.gamemode", Fmt.OnOff(g.GameMode)),
            Row("perf.spec.hags", Fmt.OnOff(g.HardwareGpuScheduling)),
            Row("perf.spec.recording", Fmt.OnOff(g.BackgroundRecording)),
            Row("perf.spec.hvci", Fmt.OnOff(g.MemoryIntegrity)),
            Row("perf.spec.windowed", g.WindowedOptimizations is { } windowed ? Fmt.OnOff(windowed) : ""),
            Row("perf.spec.mouseaccel", Fmt.OnOff(g.MouseAcceleration)),
            Row("spec.powerplan", s.Power.PlanName),
            Row("perf.spec.boost", s.Power.CpuBoostMode is { } boost ? Fmt.OnOff(boost != 0) : ""),
            Row("perf.spec.vcache", Catalog.IsDualDieX3D(s.Cpu.Name) && g.VCacheDriver is { } vcache ? Loc.T(vcache ? "perf.spec.installed" : "perf.spec.missing") : ""),
        }.OfType<SpecRow>().ToList()));
        var card = s.Gpus.FirstOrDefault(x => !x.IsIntegrated && !x.UsesBasicDriver);
        var memory = HealthAnalyzer.MemoryProfile(s.Memory);
        d.Specs.Add(new SpecGroup(Loc.T("perf.group.bios"), new[]
        {
            Row("spec.rebar", card is null ? "" : Fmt.OnOff(card.ResizableBar)),
            Row("spec.xmp", Fmt.OnOff(memory.Xmp)),
            Row("spec.memoryspeed", memory.Configured > 0 ? $"{memory.Configured} MT/s" : ""),
            Row("spec.ratedspeed", memory.Rated is { } rated ? $"{rated} MT/s" : ""),
            Row("spec.smt", s.Cpu.Threads > 0 ? Fmt.OnOff(s.Cpu.Threads > s.Cpu.Cores) : ""),
            Row("spec.virtualization", Fmt.OnOff(s.Cpu.VirtualizationEnabled)),
        }.OfType<SpecRow>().ToList()));
        d.Specs.RemoveAll(group => group.Rows.Count == 0);
        return d;
    }

    private static SpecRow? Row(string key, string? value) =>
        string.IsNullOrWhiteSpace(value) || value == Fmt.Dash ? null : new SpecRow(Loc.T(key), value);
}
