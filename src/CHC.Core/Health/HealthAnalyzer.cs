using System.Text.RegularExpressions;
using CHC.Core.Collection;
using CHC.Core.Diagnostics;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;
using CHC.Core.Sensors;
using CHC.Core.Updates;

namespace CHC.Core.Health;

/// <summary>
/// Turns raw data into findings. Every rule lives here so thresholds are easy to review
/// and tune; the texts live in the language files under the same keys.
/// </summary>
public static partial class HealthAnalyzer
{
    public static HealthReport Analyze(SystemSnapshot s)
    {
        var components = new List<ComponentHealth>
        {
            new(ComponentKind.Cpu, Cpu(s), s.Cpu.Name.Length > 0),
            new(ComponentKind.Memory, Memory(s), s.Memory.TotalBytes > 0),
            new(ComponentKind.Storage, Storage(s), s.Disks.Count > 0),
            new(ComponentKind.Gpu, Gpu(s), s.Gpus.Count > 0),
        };
        if (s.Battery is not null) components.Add(new(ComponentKind.Battery, Battery(s.Battery), true));
        components.Add(new(ComponentKind.Motherboard, Motherboard(s), true));
        components.Add(new(ComponentKind.Stability, Stability(s), true));
        return new HealthReport(components);
    }

    private static List<Finding> Cpu(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Cpu;
        var list = new List<Finding>();
        var cpu = s.Cpu;
        var load = cpu.LoadPercent ?? 0;

        if (cpu.TemperatureC is not { } t)
            list.Add(new(k, Severity.Info, s.IsElevated ? "cpu.temp.nodriver" : "cpu.temp.needadmin"));
        else if (t >= 100)
            list.Add(new(k, Severity.Critical, "cpu.temp.critical", t));
        else if (t >= 90 || (t >= 80 && load < 30))
            list.Add(new(k, Severity.Warning, load < 30 ? "cpu.temp.highidle" : "cpu.temp.high", t, load));
        else
            list.Add(new(k, Severity.Good, "cpu.temp.ok", t));

        if (load >= 90)
            list.Add(new(k, Severity.Info, "cpu.load.high", load));

        // The highest temperature seen while CHC was open (e.g. during a game), not just right now.
        if (SystemCollector.CpuTemperature(s.Sensors.Device(cpu.SensorHardwareId))?.Max is { } peak && peak >= 95 && list.All(f => f.Severity < Severity.Warning))
            list.Add(new(k, Severity.Warning, "cpu.temp.peak", peak));

        if (cpu.Manufacturer == "AMD" && s.Stability.Incidents.Any(i => i.Cause == "silent"))
            list.Add(new(k, Severity.Info, "cpu.ryzen.idle"));

        if (cpu.VirtualizationEnabled == false)
            list.Add(new(k, Severity.Info, "cpu.virtualization.off"));

        // Every Ryzen 5, 7 and 9 runs two threads per core; equal counts mean SMT was turned off in the BIOS.
        if (cpu.Manufacturer == "AMD" && cpu.Cores > 1 && cpu.Threads == cpu.Cores && RyzenPattern().IsMatch(cpu.Name))
            list.Add(new(k, Severity.Info, "cpu.smt.off", cpu.Cores));

        if (list.All(f => f.Severity < Severity.Warning) && list.All(f => f.Severity != Severity.Good))
            list.Add(new(k, Severity.Good, "cpu.ok"));
        return list;
    }

    private static List<Finding> Memory(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Memory;
        var list = new List<Finding>();
        var mem = s.Memory;

        if (mem.LoadPercent >= 90)
            list.Add(new(k, Severity.Warning, "memory.load.high", mem.LoadPercent));
        else if (mem.LoadPercent >= 80)
            list.Add(new(k, Severity.Info, "memory.load.elevated", mem.LoadPercent));
        else
            list.Add(new(k, Severity.Good, "memory.load.ok", mem.LoadPercent));

        if (mem.CommitLimitBytes > 0 && mem.CommitTotalBytes >= mem.CommitLimitBytes * 0.9)
            list.Add(new(k, Severity.Warning, "memory.commit.high", 100.0 * mem.CommitTotalBytes / mem.CommitLimitBytes));

        if (mem.TotalBytes > 0 && mem.TotalBytes < 7.5 * 1024 * 1024 * 1024)
            list.Add(new(k, Severity.Info, "memory.capacity.low", Fmt.Bytes(mem.TotalBytes, 0)));

        var modules = mem.Modules;
        if (modules.Count == 1 && mem.SlotCount >= 2)
            list.Add(new(k, Severity.Info, "memory.singlechannel", mem.SlotCount));
        else if (modules.Count >= 2 && modules.Select(Channel).ToList() is var channels && channels.All(c => c is not null) && channels.Distinct().Count() == 1)
            list.Add(new(k, Severity.Info, "memory.samechannel", modules.Count));

        if (mem.LoadPercent >= 80 && mem.TopProcesses.Count > 0)
            list.Add(new(k, Severity.Info, "memory.top", string.Join(", ", mem.TopProcesses.Take(3).Select(p => $"{p.Name} ({Fmt.Bytes(p.Bytes)})"))));

        // Drivers allocate non-paged pool; several GB of it means one of them leaks memory.
        if (mem.NonPagedPoolBytes >= 2UL * 1024 * 1024 * 1024)
            list.Add(new(k, mem.NonPagedPoolBytes >= 3UL * 1024 * 1024 * 1024 ? Severity.Warning : Severity.Info, "memory.pool.leak", Fmt.Bytes(mem.NonPagedPoolBytes)));

        var slow = modules.FirstOrDefault(m => m.SpeedMTs > 0 && m.ConfiguredSpeedMTs > 0 && m.ConfiguredSpeedMTs < m.SpeedMTs * 0.9);
        if (slow is not null)
            list.Add(new(k, Severity.Info, "memory.speed.below", slow.ConfiguredSpeedMTs, slow.SpeedMTs));
        // Turning XMP/EXPO on is only worth suggesting on a PC that runs stably.
        else if (MemoryProfile(mem) is { Xmp: false, Rated: { } rated } profile && !HasInstability(s.Stability))
            list.Add(new(k, Severity.Info, "memory.xmp.off", profile.Configured, rated));

        if (modules.Select(m => (m.CapacityBytes, m.PartNumber)).Distinct().Count() > 1)
            list.Add(new(k, Severity.Info, "memory.mixed"));

        var st = s.Stability;
        if (st.LowMemoryEvents.Count > 0)
            list.Add(new(k, Severity.Warning, "memory.lowmemory", st.LowMemoryEvents.Count, StabilityInfo.PeriodDays));

        if (st.MemoryTestFoundErrors == true)
            list.Add(new(k, Severity.Critical, "memory.test.errors", Fmt.Date(st.MemoryTestDate)));
        else if (HasInstability(st) && st.MemoryTestDate is null)
            list.Add(new(k, Severity.Info, "memory.test.suggest"));

        // XMP/EXPO is an overclock; with crashes or freezes it is the first thing to rule out.
        if (HasInstability(st) && MemoryProfile(mem) is { Xmp: true } running)
            list.Add(new(k, Severity.Info, "memory.xmp.stability", running.Configured));

        if (mem.TotalBytes > 0 && mem.CommitLimitBytes > 0 && mem.CommitLimitBytes <= mem.TotalBytes * 1.02)
            list.Add(new(k, st.AppCrashes > 0 || st.LowMemoryEvents.Count > 0 ? Severity.Warning : Severity.Info, "memory.pagefile.off"));
        return list;
    }

    private static List<Finding> Storage(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Storage;
        var list = new List<Finding>();

        if (s.Os.TrimDisabled == true && s.Disks.Any(d => d.Media is DiskMedia.Ssd or DiskMedia.Nvme))
            list.Add(new(k, Severity.Warning, "storage.trim.off"));

        if (!s.IsElevated && s.Disks.All(d => !d.SmartAvailable))
            list.Add(new(k, Severity.Info, "storage.smart.needadmin"));

        foreach (var d in s.Disks)
        {
            var name = d.Model.Length > 0 ? d.Model : $"Disk {d.Index}";
            var before = list.Count;

            // Specific SMART attributes first: they explain *why* a disk is unhealthy.
            var attr = d.SmartAttributes.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            ulong Raw(string kind) => attr.TryGetValue(kind, out var a) ? a.Raw : 0;

            if (Raw("UncorrectableSectorCount") > 0)
                list.Add(new(k, Severity.Critical, "storage.smart.uncorrectable", name, Raw("UncorrectableSectorCount")));
            if (Raw("ReallocatedSectorsCount") > 0)
                list.Add(new(k, Raw("ReallocatedSectorsCount") > 100 ? Severity.Critical : Severity.Warning, "storage.smart.reallocated", name, Raw("ReallocatedSectorsCount")));
            if (Raw("PendingSectorCount") > 0)
                list.Add(new(k, Severity.Warning, "storage.smart.pending", name, Raw("PendingSectorCount")));
            // NVMe critical warning bits: 0 spare space, 1 temperature, 2 reliability, 3 read-only, 4 backup power.
            // The temperature bit alone means the drive is too hot right now, not that it is failing.
            var nvmeWarning = Raw("CriticalWarning");
            if ((nvmeWarning & ~0x02UL) != 0)
                list.Add(new(k, Severity.Critical, "storage.nvme.criticalwarning", name, nvmeWarning));
            if (Raw("MediaAndDataIntegrityErrors") > 0)
                list.Add(new(k, Severity.Warning, "storage.nvme.mediaerrors", name, Raw("MediaAndDataIntegrityErrors")));
            if (Raw("CRCErrorCount") > 50)
                list.Add(new(k, Severity.Info, "storage.smart.crc", name, Raw("CRCErrorCount")));
            if (d.Media == DiskMedia.Hdd && Raw("SpinRetryCount") > 0)
                list.Add(new(k, Severity.Warning, "storage.smart.spinretry", name, Raw("SpinRetryCount")));

            if (d.LifePercent is { } life)
            {
                if (life <= 10) list.Add(new(k, Severity.Critical, "storage.life.critical", name, life));
                else if (life <= 30) list.Add(new(k, Severity.Warning, "storage.life.low", name, life));
            }

            if (d.TemperatureC is { } t)
            {
                var ssd = d.Media is DiskMedia.Ssd or DiskMedia.Nvme;
                var warn = d.TemperatureWarningC ?? (ssd ? 70 : 50);
                var crit = d.TemperatureCriticalC ?? (ssd ? 80 : 60);
                if (t >= crit) list.Add(new(k, Severity.Critical, "storage.temp.critical", name, t));
                else if (t >= warn) list.Add(new(k, Severity.Warning, "storage.temp.high", name, t));
            }
            if (nvmeWarning == 0x02 && !list.Skip(before).Any(f => f.Key.StartsWith("storage.temp.", StringComparison.Ordinal)))
                list.Add(new(k, Severity.Warning, "storage.nvme.overheat", name));

            var worstHealth = (DiskHealth)Math.Max((int)d.SmartHealth, (int)d.WindowsHealth);
            if (d.OperationalStatus.Contains("Predictive Failure", StringComparison.Ordinal))
                worstHealth = DiskHealth.Bad;
            // Health status last: a specific finding above explains a "caution" better. "Bad" (the drive or
            // Windows predicts failure) must stay critical even when a milder finding explains part of it.
            if (worstHealth == DiskHealth.Bad && !list.Skip(before).Any(f => f.Severity == Severity.Critical))
                list.Add(new(k, Severity.Critical, "storage.health.bad", name));
            else if (worstHealth == DiskHealth.Caution && list.Count == before)
                list.Add(new(k, Severity.Warning, "storage.health.caution", name));

            if (d.ReadErrorsUncorrected is > 0)
                list.Add(new(k, Severity.Warning, "storage.readerrors", name, d.ReadErrorsUncorrected));

            if (d.IsSystemDisk && d.Media == DiskMedia.Hdd)
                list.Add(new(k, Severity.Info, "storage.system.hdd", name));
            if (d.Media == DiskMedia.Hdd && d.PowerOnHours >= 40_000)
                list.Add(new(k, Severity.Info, "storage.hdd.old", name, Fmt.Number(d.PowerOnHours)));

            if (list.Count == before && worstHealth != DiskHealth.Bad)
            {
                if (d.LifePercent is { } l)
                    list.Add(new(k, Severity.Good, "storage.disk.ok.life", name, l));
                else
                    list.Add(new(k, Severity.Good, "storage.disk.ok", name));
            }
        }

        foreach (var v in s.Volumes.Where(v => v.TotalBytes > 0))
        {
            var freePercent = 100.0 * v.FreeBytes / v.TotalBytes;
            // Only a full Windows drive breaks things (updates, page file, temp files); a full data drive is a warning.
            if (freePercent < 5)
                list.Add(new(k, v.IsSystem ? Severity.Critical : Severity.Warning, "storage.volume.full", v.Letter, freePercent, Fmt.Bytes(v.FreeBytes)));
            else if (freePercent < 10)
                list.Add(new(k, Severity.Warning, "storage.volume.low", v.Letter, freePercent, Fmt.Bytes(v.FreeBytes)));
        }

        if (s.Stability.DiskErrors > 0)
            list.Add(new(k, Severity.Warning, "storage.events.disk", s.Stability.DiskErrors, StabilityInfo.PeriodDays));
        if (s.Stability.FileSystemErrors > 0)
            list.Add(new(k, Severity.Warning, "storage.events.ntfs", s.Stability.FileSystemErrors, StabilityInfo.PeriodDays));
        if (s.Stability.StorageResets.Count > 0)
            list.Add(new(k, Severity.Warning, "storage.resets", s.Stability.StorageResets.Count, StabilityInfo.PeriodDays));
        return list;
    }

    private static List<Finding> Gpu(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Gpu;
        var list = new List<Finding>();
        var st = s.Stability;
        var days = StabilityInfo.PeriodDays;

        // Driver resets are system wide; report them once, before the per-card findings.
        if (st.GpuDriverResets > 0)
            list.Add(new(k, st.GpuDriverResets >= 5 ? Severity.Critical : Severity.Warning, "gpu.resets", st.GpuDriverResets, days));
        var gpuHangs = st.LiveKernelEvents.Where(e => Catalog.LiveKernelEvent(e.Code).Cause == "gpu").ToList();
        if (gpuHangs.Count > 0)
        {
            var hard = gpuHangs.Count(e => e.Code is 0x117 or 0x141);
            var name = Catalog.LiveKernelEvent(gpuHangs.GroupBy(e => e.Code).OrderByDescending(g => g.Count()).First().Key).Name;
            list.Add(new(k, hard > 0 ? Severity.Warning : Severity.Info, "gpu.livekernel", gpuHangs.Count, name, days));
        }

        // A monitor in the motherboard's video output: games then run on the processor's graphics, not the card.
        var idleCard = s.Gpus.FirstOrDefault(g => !g.IsIntegrated && !g.UsesBasicDriver && g.Resolution.Length == 0);
        var integratedInUse = s.Gpus.FirstOrDefault(g => g.IsIntegrated && g.Resolution.Length > 0);
        if (!s.Board.IsLaptop && idleCard is not null && integratedInUse is not null)
            list.Add(new(k, Severity.Warning, "gpu.display.igpu", idleCard.Name, integratedInUse.Name));

        // Windows below the screen's own resolution: everything is stretched and looks soft.
        var screen = s.Gaming;
        if (screen.Width > 0 && screen.NativeWidth > screen.Width && screen.NativeHeight > screen.Height)
            list.Add(new(k, Severity.Info, "gpu.display.resolution", $"{screen.Width} × {screen.Height}", $"{screen.NativeWidth} × {screen.NativeHeight}"));

        foreach (var u in s.Updates.Items.Where(i => i.Kind == UpdateKind.GraphicsDriver))
        {
            var released = u.Released is { } day ? Loc.T("update.released", Fmt.Date(day)) : "";
            list.Add(new(k, Severity.Update, "update.graphics", u.Name, u.Latest, u.Installed, released) { ActionUrl = u.Url ?? MakerPages.Graphics(u.Name) });
        }

        foreach (var g in s.Gpus)
        {
            var before = list.Count;
            if (g.UsesBasicDriver)
            {
                list.Add(new(k, Severity.Warning, "gpu.nodriver", g.Name));
                continue;
            }
            if (g.PcieWidth is > 0 and var width && g.PcieMaxWidth > width)
                list.Add(new(k, width * 4 <= g.PcieMaxWidth ? Severity.Warning : Severity.Info, "gpu.pcie.narrow", g.Name, width, g.PcieMaxWidth));
            if (g.TemperatureC is { } t)
            {
                if (t >= 95) list.Add(new(k, Severity.Critical, "gpu.temp.critical", g.Name, t));
                else if (t >= 85) list.Add(new(k, Severity.Warning, "gpu.temp.high", g.Name, t));
            }
            if (g.HotSpotC is >= 105)
                list.Add(new(k, Severity.Warning, "gpu.hotspot.high", g.Name, g.HotSpotC));
            if (g.MemoryJunctionC is >= 105)
                list.Add(new(k, Severity.Warning, "gpu.memjunction.high", g.Name, g.MemoryJunctionC));
            if (g.Status.Length > 0 && !g.Status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                list.Add(new(k, Severity.Warning, "gpu.status", g.Name, g.Status));
            // Once the maker's newest driver is known, how old this one is says nothing any more.
            if (g.DriverDate is { } date && (DateTime.Now - date).TotalDays > 540 && !s.Updates.KnownGraphics.Contains(g.Name))
                list.Add(new(k, Severity.Update, "gpu.driver.old", g.Name, Fmt.Date(date)) { ActionUrl = MakerPages.Graphics(g.Name) });

            // Peaks since CHC started: catches overheating during a game that has already cooled down.
            var device = s.Sensors.Device(g.SensorHardwareId);
            if (device.Find("Temperature", "GPU Core")?.Max is { } corePeak && corePeak >= 87 && g.TemperatureC is null or < 85)
                list.Add(new(k, Severity.Warning, "gpu.temp.peak", g.Name, corePeak));
            if (device.Find("Temperature", "GPU Hot Spot")?.Max is { } hotPeak && hotPeak >= 105 && g.HotSpotC is null or < 105)
                list.Add(new(k, Severity.Warning, "gpu.hotspot.peak", g.Name, hotPeak));

            if (list.Count == before)
                list.Add(g.TemperatureC is { } ok
                    ? new(k, Severity.Good, "gpu.ok.temp", g.Name, ok)
                    : new(k, Severity.Good, "gpu.ok", g.Name));
        }
        return list;
    }

    private static List<Finding> Battery(BatteryInfo b)
    {
        const ComponentKind k = ComponentKind.Battery;
        var list = new List<Finding>();
        if (b.HealthPercent is { } h)
        {
            if (h < 50) list.Add(new(k, Severity.Critical, "battery.health.critical", h));
            else if (h < 70) list.Add(new(k, Severity.Warning, "battery.health.low", h));
            else list.Add(new(k, Severity.Good, "battery.health.ok", h));
        }
        else
        {
            list.Add(new(k, Severity.Info, "battery.health.unknown"));
        }
        if (b.CycleCount is > 800)
            list.Add(new(k, Severity.Info, "battery.cycles.high", b.CycleCount));
        if (b.DrainingOnAc)
            list.Add(new(k, Severity.Warning, "battery.charger.weak", -b.RateW!.Value));
        if (BatteryWear(b) is { } wear && wear.LostPerYear >= 15 && wear.Lost >= 5)
            list.Add(new(k, Severity.Warning, "battery.wear.fast", wear.Lost, wear.Months));
        return list;
    }

    /// <summary>
    /// Capacity lost over the last year of Windows' battery history (at least two months of it). Both ends
    /// use the median of three readings, because single readings jump around by a few percent.
    /// </summary>
    public static (double Lost, int Months, double LostPerYear)? BatteryWear(BatteryInfo b)
    {
        var last = b.History.LastOrDefault();
        if (last is null) return null;
        var window = b.History.Where(p => p.Date >= last.Date.AddYears(-1)).ToList();
        if (window.Count < 6) return null;
        var days = (window[^1].Date - window[0].Date).TotalDays;
        if (days < 60) return null;
        static double Median(IEnumerable<BatteryHistoryPoint> points) => points.Select(p => p.HealthPercent).Order().ElementAt(1);
        var lost = Math.Max(0, Median(window.Take(3)) - Median(window.TakeLast(3)));
        return (lost, (int)Math.Round(days / 30.44), lost / days * 365);
    }

    private static List<Finding> Motherboard(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Motherboard;
        var list = new List<Finding>();
        var b = s.Board;

        // Drivers first, as they are what an update is most often about.
        foreach (var u in s.Updates.Items)
        {
            if (u.Kind == UpdateKind.ChipsetDriver)
                list.Add(new(k, Severity.Update, u.Installed.Length > 0 ? "update.chipset" : "update.chipset.missing", u.Name, u.Latest, u.Installed) { ActionUrl = u.Url });
            else if (u.Kind == UpdateKind.WindowsDriver)
                list.Add(new(k, Severity.Update, "update.windowsdriver", u.Name) { ActionUrl = u.Url, ActionKey = "update.action.windows" });
            else if (u.Kind == UpdateKind.Windows)
                list.Add(new(k, Severity.Update, "update.windows", u.Name) { ActionUrl = u.Url, ActionKey = "update.action.windows" });
        }

        if (b.BiosDate is { } bios && (DateTime.Now - bios).TotalDays > 3 * 365)
            list.Add(new(k, Severity.Update, "board.bios.old", Fmt.Date(bios)) { ActionUrl = MakerPages.Board(b)?.Url, ActionKey = "update.action.board" });
        if (b.SecureBoot == false)
            list.Add(new(k, Severity.Info, "board.secureboot.off"));
        if (b.TpmPresent == false)
            list.Add(new(k, Severity.Info, "board.tpm.off"));
        if (s.Os.Uptime is { TotalDays: > 14 } up)
            list.Add(new(k, Severity.Info, "board.uptime.long", (int)up.TotalDays));

        var os = s.Os;
        if (os.SupportEnds is { } end && end < DateTime.Now)
            list.Add(new(k, Severity.Warning, "board.windows.unsupported", Fmt.Or(os.Name), Fmt.Or(os.DisplayVersion.Length > 0 ? os.DisplayVersion : os.Build), Fmt.Date(end)) { IsUpdate = true, ActionUrl = MakerPages.WindowsUpdate, ActionKey = "update.action.windows" });
        if (os.RestartPending == true)
            list.Add(new(k, Severity.Update, "board.restart.pending") { ActionUrl = MakerPages.WindowsUpdate, ActionKey = "update.action.windows" });
        if (os.StartupApps.Count >= 15)
            list.Add(new(k, Severity.Info, "board.startup.many", os.StartupApps.Count, string.Join(", ", os.StartupApps.Take(8))));

        // One failure is usually retried successfully; the same update failing again needs a look.
        if (s.Stability.UpdateFailures.GroupBy(u => u.Title).OrderByDescending(g => g.Count()).FirstOrDefault() is { } failed && failed.Count() >= 2)
            list.Add(new(k, Severity.Update, "board.updates.failed", failed.Key, failed.Count(), Fmt.Or(failed.First().ErrorCode), StabilityInfo.PeriodDays) { ActionUrl = MakerPages.WindowsUpdate, ActionKey = "update.action.windows" });

        PowerSupply(s, list);

        if (list.Count == 0)
            list.Add(new(k, Severity.Good, "board.ok"));
        return list;
    }

    /// <summary>
    /// The power supply of a desktop: is it big enough for the most the PC drew lately, and do its rails hold up?
    /// A laptop's "supply" is its charger, which the battery checks cover.
    /// </summary>
    private static void PowerSupply(SystemSnapshot s, List<Finding> list)
    {
        const ComponentKind k = ComponentKind.Motherboard;
        if (s.PowerSupply is not { } psu || s.Board.IsLaptop) return;
        var discrete = s.Gpus.Any(g => !g.IsIntegrated && !g.IsVirtual && !g.UsesBasicDriver);

        if (psu.RatedWatts is { } rated && psu.PeakW is { } peak && psu.RecommendedWatts is { } recommended)
        {
            var load = peak / rated;
            if (load > 1)
                list.Add(new(k, Severity.Critical, "psu.over", Fmt.Watts(Math.Round(peak)), Fmt.Watts(rated), Fmt.Watts(recommended)));
            else if (load > 0.85)
                list.Add(new(k, Severity.Warning, "psu.tight", Fmt.Watts(Math.Round(peak)), Fmt.Percent(load * 100), Fmt.Watts(rated), Fmt.Watts(recommended)));
            else
                list.Add(new(k, Severity.Good, "psu.ok", Fmt.Watts(Math.Round(peak)), Fmt.Percent(load * 100), Fmt.Watts(rated)));
        }
        else if (psu.RatedWatts is null && discrete)
        {
            list.Add(new(k, Severity.Info, "psu.unknown"));
        }

        // The motherboard measures roughly; only a clear drop below the ATX limit is worth a word.
        foreach (var rail in psu.Rails.Where(r => r.Lowest is { } low && low < r.LowLimit * (r.FromSupply ? 1 : 0.99)))
            list.Add(new(k, Severity.Warning, "psu.rail.low", rail.Name, rail.Lowest!.Value.ToString("0.00", Loc.Instance.Culture) + " V",
                rail.LowLimit.ToString("0.00", Loc.Instance.Culture) + " V", new LocKey(rail.FromSupply ? "psu.source.supply" : "psu.source.board")));
    }

    private static List<Finding> Stability(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Stability;
        var list = new List<Finding>();
        var st = s.Stability;
        var days = StabilityInfo.PeriodDays;

        if (!st.Available)
        {
            list.Add(new(k, Severity.Info, "stability.unavailable"));
        }
        else
        {
            var incidents = st.Incidents;

            // The overall verdict first: what most freezes and crashes point to.
            // It sums up the freezes and crashes below, which already count in the score.
            if (IncidentBuilder.MainCause(incidents) is { Count: >= 2 } main)
                list.Add(new(k, Severity.Warning, "stability.diagnosis", new LocKey("cause." + main.Cause), main.Count, incidents.Count, new LocKey("cause." + main.Cause + ".fix")) { Scored = false });

            // The problems began right after a driver, an update, a BIOS update or a memory setting changed:
            // that change is the first suspect. Also a summary of incidents counted below.
            if (ChangeSuspects.Find(s.Changes, incidents) is { } suspect)
                list.Add(new(k, Severity.Warning, "stability.afterchange", new ChangeArgument(suspect.Change), suspect.After,
                    Fmt.Date(suspect.Change.Time), new LocKey("stability.afterchange.fix." + FixFor(suspect.Change))) { Scored = false });

            Moments(incidents, list);

            var crashes = incidents.Where(i => i.Kind == ShutdownKind.Crash).ToList();
            if (crashes.Count > 0)
            {
                var top = crashes.Where(c => c.BugcheckCode != 0).GroupBy(c => c.BugcheckCode).OrderByDescending(g => g.Count()).FirstOrDefault();
                var code = top?.Key ?? 0;
                list.Add(new(k, crashes.Count >= 3 ? Severity.Critical : Severity.Warning, "stability.bsod", crashes.Count, days,
                    code == 0 ? "?" : Catalog.BugcheckText(code), new LocKey("cause." + (code == 0 ? "unknown" : Catalog.Bugcheck(code).Cause))));
            }

            var frozen = incidents.Count(i => i.Kind == ShutdownKind.ForcedOff);
            if (frozen > 0)
                list.Add(new(k, frozen >= 3 ? Severity.Critical : Severity.Warning, "stability.forcedoff", frozen, days));
            var sleep = incidents.Count(i => i.Kind == ShutdownKind.Sleep);
            if (sleep > 0)
                list.Add(new(k, sleep >= 3 ? Severity.Critical : Severity.Warning, "stability.sleepfail", sleep, days));
            var silent = incidents.Count(i => i.Kind == ShutdownKind.PowerLoss);
            if (silent > 0)
                list.Add(new(k, silent >= 8 ? Severity.Critical : Severity.Warning, "stability.silent", silent, days));
            if (st.ThermalShutdowns.Count > 0)
                list.Add(new(k, Severity.Critical, "stability.thermal", st.ThermalShutdowns.Count, days));

            if (st.WheaErrors > 0)
                list.Add(new(k, Severity.Critical, "stability.whea.errors", st.WheaErrors, days, WheaSource(st, fatal: true)));
            if (st.WheaCorrected >= 10)
                list.Add(new(k, Severity.Warning, "stability.whea.corrected", st.WheaCorrected, days, WheaSource(st, fatal: false)));
            else if (st.WheaCorrected > 0)
                list.Add(new(k, Severity.Info, "stability.whea.corrected", st.WheaCorrected, days, WheaSource(st, fatal: false)));

            var otherHangs = st.LiveKernelEvents.Where(e => Catalog.LiveKernelEvent(e.Code).Cause != "gpu").ToList();
            if (otherHangs.Count > 0)
                list.Add(new(k, Severity.Info, "stability.livekernel", otherHangs.Count, Catalog.LiveKernelEvent(otherHangs[0].Code).Name, days));

            foreach (var app in st.AppProblems.Where(a => a.Crashes >= 2).Take(3))
            {
                var vendor = Catalog.GraphicsDriverVendor(app.Module);
                list.Add(vendor is not null
                    ? new(k, Severity.Warning, "stability.app.crash.gpu", app.App, app.Crashes, new LocKey("driver." + vendor))
                    : new(k, app.Crashes >= 5 ? Severity.Warning : Severity.Info, "stability.app.crash", app.App, app.Crashes, Fmt.Or(app.Module)));
            }
            foreach (var app in st.AppProblems.Where(a => a.Hangs >= 3).Take(2))
                list.Add(new(k, Severity.Info, "stability.app.hang", app.App, app.Hangs));

            // A network adapter that stops responding means lag spikes and disconnects in online games.
            if (st.NetworkResets.GroupBy(n => n.Adapter, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(n => n.Resets)).FirstOrDefault() is { } nic)
            {
                var resets = nic.Sum(n => n.Resets);
                var bursts = nic.Count();
                list.Add(new(k, bursts >= 2 || resets >= 5 ? Severity.Warning : Severity.Info, "stability.network", Fmt.Or(nic.Key), resets, bursts, days));
            }

            PowerTips(s, list);
        }

        list.AddRange(Devices.DeviceAdvisor.Advise(s).Where(f => f.Key != "device.problem"));
        foreach (var d in s.ProblemDevices)
        {
            // 22 = disabled by the user, 24/45 = not present (often ghost PS/2 or unplugged USB devices).
            var severity = d.ErrorCode is 22 or 24 or 45 ? Severity.Info : Severity.Warning;
            list.Add(new(k, severity, "stability.device", d.Name, d.ErrorCode, new LocKey(DeviceErrorKey(d.ErrorCode))));
        }

        // "No crashes or hardware errors" must not sit next to a corrected WHEA error or a program crash tip;
        // only harmless device notes (disabled / unplugged) may accompany it.
        if (st.Available && list.All(f => f.Severity == Severity.Info && f.Key == "stability.device"))
            list.Add(new(k, Severity.Good, "stability.ok", days));
        return list;
    }

    /// <summary>
    /// What the history saw right before the freezes and crashes: the same program every time points at that program,
    /// always under full load at power or heat, always idle at power saving. Sums up incidents counted below.
    /// </summary>
    private static void Moments(IReadOnlyList<Incident> incidents, List<Finding> list)
    {
        const ComponentKind k = ComponentKind.Stability;
        var moments = incidents.Where(i => i.Moment is not null).Select(i => i.Moment!).ToList();
        if (moments.Count < 2) return;

        var programs = moments.Where(m => m.App is not null && m.Exe is not null && !Catalog.IsWindowsComponent(m.Exe)).ToList();
        if (programs.GroupBy(m => m.Exe).OrderByDescending(g => g.Count()).FirstOrDefault() is { } top && top.Count() >= 2 && top.Count() * 3 >= moments.Count * 2)
            list.Add(new(k, Severity.Warning, "stability.sameapp", top.First().App, top.Count(), moments.Count) { Scored = false });

        var underLoad = moments.Count(m => m.GpuLoad >= 85 || m.CpuLoad >= 80);
        var idle = moments.Count(m => m.GpuLoad < 20 && m.CpuLoad < 15);
        if (underLoad >= 2 && underLoad * 3 >= moments.Count * 2)
            list.Add(new(k, Severity.Info, "stability.underload", underLoad, moments.Count) { Scored = false });
        else if (idle >= 2 && idle * 3 >= moments.Count * 2)
            list.Add(new(k, Severity.Info, "stability.atidle", idle, moments.Count) { Scored = false });
    }

    /// <summary>Power-saving features known to cause idle freezes and sleep problems; only shown when such problems happened.</summary>
    private static void PowerTips(SystemSnapshot s, List<Finding> list)
    {
        const ComponentKind k = ComponentKind.Stability;
        var st = s.Stability;
        var p = s.Power;
        var freezes = st.Incidents.Count(i => i.Kind is ShutdownKind.ForcedOff or ShutdownKind.PowerLoss or ShutdownKind.Sleep);
        var sleepRelated = st.Incidents.Any(i => i.Cause == "sleep");
        if (freezes == 0 && st.GpuDriverResets == 0) return;

        if (p.PcieLinkPowerManagement is > 0)
            list.Add(new(k, Severity.Info, "power.aspm"));
        if (freezes == 0) return;
        if (p.FastStartup == true)
            list.Add(new(k, Severity.Info, "power.faststartup"));
        if (p.HybridSleep == true && p.SleepAfterSeconds is > 0 && !s.Board.IsLaptop)
            list.Add(new(k, Severity.Info, "power.hybridsleep"));
        if (p.ModernStandby == true)
            list.Add(new(k, Severity.Info, "power.modernstandby"));
        if (sleepRelated && p.UsbSelectiveSuspend == true)
            list.Add(new(k, Severity.Info, "power.usbsuspend"));
    }

    private static LocKey WheaSource(StabilityInfo st, bool fatal)
    {
        var component = st.Whea.Where(w => w.Fatal == fatal).GroupBy(w => w.Component).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "other";
        return new LocKey("whea." + component);
    }

    /// <summary>
    /// The memory's current speed, the speed it is sold for, and whether its XMP/EXPO profile is on:
    /// true above the official (JEDEC) speed, false when the modules are rated faster than they run, otherwise unknown.
    /// </summary>
    public static (int Configured, int? Rated, bool? Xmp) MemoryProfile(MemoryInfo mem)
    {
        var configured = mem.Modules.Select(m => m.ConfiguredSpeedMTs).DefaultIfEmpty(0).Max();
        var rated = mem.Modules.Select(m => m.RatedSpeedMTs).Max();
        var type = mem.Modules.Select(m => m.Type).FirstOrDefault(t => t.Length > 0) ?? "";
        var jedec = type switch { "DDR5" => 5600, "DDR4" => 3200, "DDR3" => 1600, _ => int.MaxValue };
        bool? xmp = configured == 0 ? null
            : configured > jedec ? true
            : rated > configured * 1.04 && rated > jedec ? false
            : null;
        return (configured, rated, xmp);
    }

    [GeneratedRegex(@"Ryzen\s+(5|7|9)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenPattern();

    /// <summary>Freezes, crashes or hardware errors happened recently.</summary>
    private static bool HasInstability(StabilityInfo st) =>
        st.Incidents.Count > 0 || st.WheaErrors > 0 || st.WheaCorrected >= 10 || st.GpuDriverResets > 0;

    /// <summary>
    /// Memory channel of a module from its slot names ("P0 CHANNEL A", "Controller1-ChannelA-DIMM0", "DIMM_B2"),
    /// including the controller so that Intel's two "Channel A"s are told apart. Null if the names don't say.
    /// </summary>
    private static string? Channel(MemoryModule m)
    {
        var text = $"{m.Bank} {m.Slot}";
        var controller = ControllerPattern().Match(text) is { Success: true } c ? c.Groups[1].Value : "0";
        if (ChannelPattern().Match(text) is { Success: true } channel) return controller + channel.Groups[1].Value.ToUpperInvariant();
        if (DimmPattern().Match(m.Slot) is { Success: true } dimm) return controller + dimm.Groups[1].Value.ToUpperInvariant();
        return null;
    }

    [GeneratedRegex(@"Controller\s*(\d)", RegexOptions.IgnoreCase)]
    private static partial Regex ControllerPattern();

    [GeneratedRegex(@"Channel\s*([A-H])(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"DIMM[\s_-]?([A-H])\d", RegexOptions.IgnoreCase)]
    private static partial Regex DimmPattern();

    /// <summary>Localization key that explains a Device Manager error code.</summary>
    public static string DeviceErrorKey(int code) => code switch
    {
        1 or 3 or 10 or 12 or 14 or 18 or 19 or 21 or 22 or 24 or 28 or 31 or 32 or 37 or 39 or 43 or 45 or 52 => $"device.error.{code}",
        _ => "device.error.other",
    };

    /// <summary>Which advice fits undoing a change: roll back a driver, remove an update, reset the BIOS, slow the memory down.</summary>
    internal static string FixFor(Timeline.SystemChange change) => change.Kind switch
    {
        Timeline.ChangeKind.Driver => "driver",
        Timeline.ChangeKind.WindowsUpdate or Timeline.ChangeKind.Windows => "update",
        Timeline.ChangeKind.Bios => "bios",
        Timeline.ChangeKind.Setting when change.Setting == "memoryspeed" => "memory",
        _ => "other",
    };
}
