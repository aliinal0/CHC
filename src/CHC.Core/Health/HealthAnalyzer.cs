using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;

namespace CHC.Core.Health;

/// <summary>
/// Turns raw data into findings. Every rule lives here so thresholds are easy to review
/// and tune; the texts live in the language files under the same keys.
/// </summary>
public static class HealthAnalyzer
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

        if (cpu.VirtualizationEnabled == false)
            list.Add(new(k, Severity.Info, "cpu.virtualization.off"));

        if (list.All(f => f.Severity <= Severity.Info) && list.All(f => f.Severity != Severity.Good))
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

        var slow = modules.FirstOrDefault(m => m.SpeedMTs > 0 && m.ConfiguredSpeedMTs > 0 && m.ConfiguredSpeedMTs < m.SpeedMTs * 0.9);
        if (slow is not null)
            list.Add(new(k, Severity.Info, "memory.speed.below", slow.ConfiguredSpeedMTs, slow.SpeedMTs));

        if (modules.Select(m => (m.CapacityBytes, m.PartNumber)).Distinct().Count() > 1)
            list.Add(new(k, Severity.Info, "memory.mixed"));
        return list;
    }

    private static List<Finding> Storage(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Storage;
        var list = new List<Finding>();

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
            if (Raw("CriticalWarning") > 0)
                list.Add(new(k, Severity.Critical, "storage.nvme.criticalwarning", name, Raw("CriticalWarning")));
            if (Raw("MediaAndDataIntegrityErrors") > 0)
                list.Add(new(k, Severity.Warning, "storage.nvme.mediaerrors", name, Raw("MediaAndDataIntegrityErrors")));
            if (Raw("CRCErrorCount") > 50)
                list.Add(new(k, Severity.Info, "storage.smart.crc", name, Raw("CRCErrorCount")));

            var worstHealth = (DiskHealth)Math.Max((int)d.SmartHealth, (int)d.WindowsHealth);
            if (d.OperationalStatus.Contains("Predictive Failure", StringComparison.Ordinal))
                worstHealth = DiskHealth.Bad;
            if (list.Count == before)
            {
                if (worstHealth == DiskHealth.Bad)
                    list.Add(new(k, Severity.Critical, "storage.health.bad", name));
                else if (worstHealth == DiskHealth.Caution)
                    list.Add(new(k, Severity.Warning, "storage.health.caution", name));
            }

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

            if (d.ReadErrorsUncorrected is > 0)
                list.Add(new(k, Severity.Warning, "storage.readerrors", name, d.ReadErrorsUncorrected));

            if (d.IsSystemDisk && d.Media == DiskMedia.Hdd)
                list.Add(new(k, Severity.Info, "storage.system.hdd", name));

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
            if (freePercent < 5)
                list.Add(new(k, Severity.Critical, "storage.volume.full", v.Letter, freePercent, Fmt.Bytes(v.FreeBytes)));
            else if (freePercent < 10)
                list.Add(new(k, Severity.Warning, "storage.volume.low", v.Letter, freePercent, Fmt.Bytes(v.FreeBytes)));
        }

        if (s.Stability.DiskErrors > 0)
            list.Add(new(k, Severity.Warning, "storage.events.disk", s.Stability.DiskErrors, StabilityInfo.PeriodDays));
        if (s.Stability.FileSystemErrors > 0)
            list.Add(new(k, Severity.Warning, "storage.events.ntfs", s.Stability.FileSystemErrors, StabilityInfo.PeriodDays));
        return list;
    }

    private static List<Finding> Gpu(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Gpu;
        var list = new List<Finding>();
        foreach (var g in s.Gpus)
        {
            var before = list.Count;
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
            if (g.DriverDate is { } date && (DateTime.Now - date).TotalDays > 540)
                list.Add(new(k, Severity.Info, "gpu.driver.old", g.Name, Fmt.Date(date)));

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
        return list;
    }

    private static List<Finding> Motherboard(SystemSnapshot s)
    {
        const ComponentKind k = ComponentKind.Motherboard;
        var list = new List<Finding>();
        var b = s.Board;

        if (b.BiosDate is { } bios && (DateTime.Now - bios).TotalDays > 3 * 365)
            list.Add(new(k, Severity.Info, "board.bios.old", Fmt.Date(bios)));
        if (b.SecureBoot == false)
            list.Add(new(k, Severity.Info, "board.secureboot.off"));
        if (b.TpmPresent == false)
            list.Add(new(k, Severity.Info, "board.tpm.off"));
        if (s.Os.Uptime is { TotalDays: > 14 } up)
            list.Add(new(k, Severity.Info, "board.uptime.long", (int)up.TotalDays));

        if (list.Count == 0)
            list.Add(new(k, Severity.Good, "board.ok"));
        return list;
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
            if (st.WheaErrors > 0)
                list.Add(new(k, Severity.Critical, "stability.whea.errors", st.WheaErrors, days));
            if (st.WheaCorrected >= 10)
                list.Add(new(k, Severity.Warning, "stability.whea.corrected", st.WheaCorrected, days));
            else if (st.WheaCorrected > 0)
                list.Add(new(k, Severity.Info, "stability.whea.corrected", st.WheaCorrected, days));
            if (st.BlueScreens > 0)
                list.Add(new(k, st.BlueScreens >= 3 ? Severity.Critical : Severity.Warning, "stability.bsod", st.BlueScreens, days));
            if (st.UnexpectedShutdowns > 0)
                list.Add(new(k, st.UnexpectedShutdowns >= 8 ? Severity.Critical : Severity.Warning, "stability.shutdowns", st.UnexpectedShutdowns, days));
        }

        foreach (var d in s.ProblemDevices)
        {
            // 22 = disabled by the user, 24/45 = not present (often ghost PS/2 or unplugged USB devices).
            var severity = d.ErrorCode is 22 or 24 or 45 ? Severity.Info : Severity.Warning;
            list.Add(new(k, severity, "stability.device", d.Name, d.ErrorCode, new LocKey(DeviceErrorKey(d.ErrorCode))));
        }

        if (list.All(f => f.Severity == Severity.Info) && st.Available)
            list.Add(new(k, Severity.Good, "stability.ok", days));
        return list;
    }

    /// <summary>Localization key that explains a Device Manager error code.</summary>
    public static string DeviceErrorKey(int code) => code switch
    {
        1 or 3 or 10 or 12 or 14 or 18 or 19 or 21 or 22 or 24 or 28 or 31 or 32 or 37 or 39 or 43 or 45 or 52 => $"device.error.{code}",
        _ => "device.error.other",
    };
}
