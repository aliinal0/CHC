using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Sensors;

namespace CHC.Core.Presentation;

/// <summary>Builds the localized, display-ready <see cref="ComponentSection"/>s from a snapshot.</summary>
public static class SectionBuilder
{
    public static List<ComponentSection> Build(SystemSnapshot s, HealthReport health, bool revealSerials = true)
    {
        string Serial(string? value) => revealSerials ? Fmt.Or(value) : Fmt.Mask(value);

        var sections = new List<ComponentSection>();
        foreach (var component in health.Components)
        {
            var section = new ComponentSection
            {
                Kind = component.Kind,
                Title = Loc.T(TitleKey(component.Kind)),
                Summary = Loc.T("summary." + component.Severity.ToString().ToLowerInvariant()),
                Severity = component.Severity,
                Score = component.Score,
                Findings = component.Findings.OrderByDescending(f => f.Severity).ToList(),
            };
            switch (component.Kind)
            {
                case ComponentKind.Cpu: section.Devices.Add(Cpu(s)); break;
                case ComponentKind.Memory: section.Devices.Add(Memory(s, Serial)); break;
                case ComponentKind.Storage: section.Devices.AddRange(Storage(s, component, Serial)); break;
                case ComponentKind.Gpu: section.Devices.AddRange(Gpus(s, component, Serial)); break;
                case ComponentKind.Battery when s.Battery is not null: section.Devices.Add(Battery(s.Battery, s.Sensors)); break;
                case ComponentKind.Motherboard: section.Devices.AddRange(Motherboard(s, Serial)); break;
                case ComponentKind.Stability: section.Devices.AddRange(Stability(s)); break;
            }
            foreach (var device in section.Devices) device.Specs.RemoveAll(g => g.Rows.Count == 0);
            sections.Add(section);
        }
        return sections;
    }

    public static string TitleKey(ComponentKind kind) => "component." + kind.ToString().ToLowerInvariant();

    // ---------------------------------------------------------------- CPU

    private static DeviceSection Cpu(SystemSnapshot s)
    {
        var cpu = s.Cpu;
        var device = s.Sensors.Device(cpu.SensorHardwareId);
        var loadSensor = device.Find("Load", "CPU Total");
        var tempSensor = Collection.SystemCollector.CpuTemperature(device);
        var clockSensor = device.Find("Clock", "Cores (Average)", "Core #1");
        var powerSensor = device.Find("Power", "Package", "CPU Package");

        var d = new DeviceSection
        {
            Title = Fmt.Or(cpu.Name),
            Subtitle = Loc.T("cpu.subtitle", cpu.Cores, cpu.Threads, Fmt.Or(cpu.Socket)),
        };
        d.Highlights.Add(new(Loc.T("label.load"), Fmt.Percent(cpu.LoadPercent), loadSensor?.Id, "Load"));
        d.Highlights.Add(new(Loc.T("label.temperature"), Fmt.Temp(cpu.TemperatureC), tempSensor?.Id, "Temperature"));
        d.Highlights.Add(clockSensor.Positive() is not null
            ? new(Loc.T("label.clock"), Fmt.MHz(clockSensor!.Value), clockSensor.Id, "Clock")
            : new(Loc.T("label.clock"), Fmt.MHz(cpu.CurrentClockMHz)));
        d.Highlights.Add(new(Loc.T("label.power"), Fmt.Watts(cpu.PackagePowerW), powerSensor?.Id, "Power"));
        d.Meters.Add(new(Loc.T("label.load"), cpu.LoadPercent ?? 0, Fmt.Percent(cpu.LoadPercent), LoadSeverity(cpu.LoadPercent), loadSensor?.Id));

        d.Specs.Add(Group("spec.group.general",
            Row("spec.name", cpu.Name),
            Row("spec.manufacturer", cpu.Manufacturer),
            Row("spec.cores", cpu.Cores > 0 ? cpu.Cores.ToString() : ""),
            Row("spec.threads", cpu.Threads > 0 ? cpu.Threads.ToString() : ""),
            Row("spec.baseclock", cpu.BaseClockMHz > 0 ? Fmt.MHz(cpu.BaseClockMHz) : ""),
            Row("spec.socket", cpu.Socket)));
        d.Specs.Add(Group("spec.group.cache",
            Row("spec.l2", cpu.L2CacheKB > 0 ? Fmt.Bytes((ulong)cpu.L2CacheKB * 1024, 0) : ""),
            Row("spec.l3", cpu.L3CacheKB > 0 ? Fmt.Bytes((ulong)cpu.L3CacheKB * 1024, 0) : "")));
        d.Specs.Add(Group("spec.group.technical",
            Row("spec.identifier", cpu.Identifier),
            Row("spec.architecture", s.Os.Architecture),
            Row("spec.virtualization", Fmt.OnOff(cpu.VirtualizationEnabled))));
        if (cpu.SensorHardwareId is not null) d.SensorDeviceIds.Add(cpu.SensorHardwareId);
        return d;
    }

    // ---------------------------------------------------------------- Memory

    private static DeviceSection Memory(SystemSnapshot s, Func<string?, string> serial)
    {
        var mem = s.Memory;
        var modules = mem.Modules;
        var type = modules.Select(m => m.Type).FirstOrDefault(t => t.Length > 0) ?? "";
        var speed = modules.Select(m => m.ConfiguredSpeedMTs > 0 ? m.ConfiguredSpeedMTs : m.SpeedMTs).DefaultIfEmpty(0).Max();
        var ram = s.Sensors.OfKind("Memory").FirstOrDefault(x => x.Id == "/ram");

        var d = new DeviceSection
        {
            Title = $"{Fmt.Bytes(mem.TotalBytes, 0)} {type}".Trim(),
            Subtitle = Loc.T("memory.subtitle", modules.Count, mem.SlotCount > 0 ? mem.SlotCount : modules.Count, speed),
        };
        d.Highlights.Add(new(Loc.T("label.inuse"), Fmt.Bytes(mem.UsedBytes), ram.Find("Data", "Memory Used")?.Id, "Data"));
        d.Highlights.Add(new(Loc.T("label.load"), Fmt.Percent(mem.LoadPercent), ram.Find("Load", "Memory")?.Id, "Load"));
        d.Highlights.Add(new(Loc.T("label.available"), Fmt.Bytes(mem.AvailableBytes), ram.Find("Data", "Memory Available")?.Id, "Data"));
        d.Highlights.Add(new(Loc.T("label.speed"), speed > 0 ? $"{speed} MT/s" : Fmt.Dash));

        d.Meters.Add(new(Loc.T("memory.meter.physical"), mem.LoadPercent, $"{Fmt.Bytes(mem.UsedBytes)} / {Fmt.Bytes(mem.TotalBytes)}", LoadSeverity(mem.LoadPercent, 80, 90), ram.Find("Load", "Memory")?.Id));
        if (mem.CommitLimitBytes > 0)
        {
            var commit = 100.0 * mem.CommitTotalBytes / mem.CommitLimitBytes;
            d.Meters.Add(new(Loc.T("memory.meter.commit"), commit, $"{Fmt.Bytes(mem.CommitTotalBytes)} / {Fmt.Bytes(mem.CommitLimitBytes)}", LoadSeverity(commit, 80, 90)));
        }

        d.Specs.Add(Group("spec.group.general",
            Row("spec.total", Fmt.Bytes(mem.TotalBytes)),
            Row("spec.type", type),
            Row("spec.slots", mem.SlotCount > 0 ? Loc.T("memory.slots.value", modules.Count, mem.SlotCount) : ""),
            Row("spec.maxcapacity", mem.MaxCapacityBytes > 0 ? Fmt.Bytes(mem.MaxCapacityBytes, 0) : ""),
            Row("spec.channel", modules.Count switch { 0 => "", 1 => Loc.T("memory.channel.single"), _ => Loc.T("memory.channel.multi") })));

        if (modules.Count > 0)
        {
            d.Tables.Add(new TableBlock(Loc.T("memory.table.modules"),
                [Loc.T("col.slot"), Loc.T("col.capacity"), Loc.T("col.type"), Loc.T("col.speed"), Loc.T("col.manufacturer"), Loc.T("col.partnumber"), Loc.T("col.serial"), Loc.T("col.voltage")],
                modules.Select(m => (IReadOnlyList<string>)
                [
                    Fmt.Or(string.Join(" / ", new[] { m.Slot, m.Bank }.Where(x => x.Length > 0))),
                    Fmt.Bytes(m.CapacityBytes, 0),
                    Fmt.Or($"{m.Type} {m.FormFactor}".Trim()),
                    m.ConfiguredSpeedMTs > 0 && m.ConfiguredSpeedMTs != m.SpeedMTs ? $"{m.ConfiguredSpeedMTs} / {m.SpeedMTs} MT/s" : m.SpeedMTs > 0 ? $"{m.SpeedMTs} MT/s" : Fmt.Dash,
                    Fmt.Or(m.Manufacturer),
                    Fmt.Or(m.PartNumber),
                    serial(m.SerialNumber),
                    m.VoltageMv > 0 ? (m.VoltageMv / 1000.0).ToString("0.00", Loc.Instance.Culture) + " V" : Fmt.Dash,
                ]).ToList()));
        }
        d.SensorDeviceIds.AddRange(s.Sensors.OfKind("Memory").Select(x => x.Id));
        return d;
    }

    // ---------------------------------------------------------------- Storage

    private static IEnumerable<DeviceSection> Storage(SystemSnapshot s, ComponentHealth health, Func<string?, string> serial)
    {
        var mappedLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var disk in s.Disks)
        {
            var name = disk.Model.Length > 0 ? disk.Model : $"Disk {disk.Index}";
            var device = s.Sensors.Device(disk.SensorHardwareId);
            var tempSensor = device.Find("Temperature", "Temperature", "Composite Temperature");
            var severity = health.Findings.Where(f => f.Args.Length > 0 && Equals(f.Args[0], name)).Select(f => f.Severity).DefaultIfEmpty(Severity.Good).Max();

            var d = new DeviceSection
            {
                Title = name,
                Subtitle = string.Join(" · ", new[] { Fmt.DiskSize(disk.SizeBytes), MediaName(disk.Media), disk.BusType, string.Join(" ", disk.DriveLetters) }.Where(x => x.Length > 0)),
                Severity = severity,
            };
            var bestHealth = disk.SmartHealth != DiskHealth.Unknown ? disk.SmartHealth : disk.WindowsHealth;
            d.Highlights.Add(new(Loc.T("label.health"), HealthName(bestHealth)));
            d.Highlights.Add(new(Loc.T("label.life"), disk.LifePercent is { } life ? Fmt.Percent(life) : Fmt.Dash));
            d.Highlights.Add(new(Loc.T("label.temperature"), Fmt.Temp(disk.TemperatureC), tempSensor?.Id, "Temperature"));
            d.Highlights.Add(new(Loc.T("label.poweron"), disk.PowerOnHours is { } h ? Loc.T("common.hours", h) : Fmt.Dash));

            if (disk.LifePercent is { } lifeLeft)
                d.Meters.Add(new(Loc.T("storage.meter.life"), lifeLeft, Fmt.Percent(lifeLeft), lifeLeft <= 10 ? Severity.Critical : lifeLeft <= 30 ? Severity.Warning : Severity.Good));
            foreach (var v in s.Volumes.Where(v => disk.DriveLetters.Contains(v.Letter, StringComparer.OrdinalIgnoreCase)))
            {
                mappedLetters.Add(v.Letter);
                d.Meters.Add(VolumeMeter(v));
            }

            d.Specs.Add(Group("spec.group.identity",
                Row("spec.model", disk.Model),
                Row("spec.serial", serial(disk.SerialNumber)),
                Row("spec.firmware", disk.Firmware),
                Row("spec.capacity", $"{Fmt.DiskSize(disk.SizeBytes)} ({Fmt.Bytes(disk.SizeBytes)})"),
                Row("spec.mediatype", MediaName(disk.Media)),
                Row("spec.interface", disk.BusType),
                Row("spec.trim", Fmt.YesNo(disk.TrimSupported)),
                Row("spec.systemdisk", Fmt.YesNo(disk.IsSystemDisk)),
                Row("spec.removable", Fmt.YesNo(disk.IsRemovable))));
            d.Specs.Add(Group("spec.group.health",
                Row("spec.windowshealth", HealthName(disk.WindowsHealth)),
                Row("spec.smarthealth", disk.SmartAvailable ? HealthName(disk.SmartHealth) : Loc.T(s.IsElevated ? "common.unavailable" : "common.needsadmin")),
                Row("spec.operational", disk.OperationalStatus),
                Row("spec.life", disk.LifePercent is { } l ? Fmt.Percent(l) : ""),
                Row("spec.temperature", disk.TemperatureC is { } t ? Fmt.Temp(t) : ""),
                Row("spec.tempthresholds", disk.TemperatureWarningC is { } w ? $"{Fmt.Temp(w)} / {Fmt.Temp(disk.TemperatureCriticalC)}" : ""),
                Row("spec.poweronhours", disk.PowerOnHours is { } poh ? Fmt.Hours(poh) : ""),
                Row("spec.powercycles", disk.PowerOnCount is { } pc ? Fmt.Number(pc) : ""),
                Row("spec.hostwrites", disk.HostWritesGB is { } hw ? Fmt.Gigabytes(hw) : ""),
                Row("spec.hostreads", disk.HostReadsGB is { } hr ? Fmt.Gigabytes(hr) : ""),
                Row("spec.readerrors", disk.ReadErrorsUncorrected is { } re ? Fmt.Number(re) : "")));

            if (disk.SmartAttributes.Count > 0)
            {
                d.Tables.Add(new TableBlock(Loc.T("storage.table.smart"),
                    ["ID", Loc.T("col.attribute"), Loc.T("col.current"), Loc.T("col.worst"), Loc.T("col.threshold"), Loc.T("col.raw")],
                    disk.SmartAttributes.Select(a => (IReadOnlyList<string>)
                    [
                        a.Id.ToString("X2"),
                        Fmt.Or(a.Name),
                        a.Current > 0 ? a.Current.ToString() : Fmt.Dash,
                        a.Worst > 0 ? a.Worst.ToString() : Fmt.Dash,
                        a.Threshold > 0 ? a.Threshold.ToString() : Fmt.Dash,
                        a.Raw.ToString("N0", Loc.Instance.Culture),
                    ]).ToList()));
            }
            if (disk.SensorHardwareId is not null) d.SensorDeviceIds.Add(disk.SensorHardwareId);
            yield return d;
        }

        var unmapped = s.Volumes.Where(v => !mappedLetters.Contains(v.Letter)).ToList();
        if (unmapped.Count > 0)
        {
            var d = new DeviceSection { Title = Loc.T("storage.volumes"), Subtitle = Loc.T("storage.volumes.subtitle", unmapped.Count) };
            d.Meters.AddRange(unmapped.Select(VolumeMeter));
            yield return d;
        }
    }

    private static Meter VolumeMeter(VolumeInfo v)
    {
        var label = string.IsNullOrWhiteSpace(v.Label) ? v.Letter : $"{v.Letter} {v.Label}";
        var caption = Loc.T("storage.volume.caption", Fmt.Bytes(v.FreeBytes), Fmt.Bytes(v.TotalBytes), v.FileSystem);
        var free = 100 - v.UsedPercent;
        return new Meter(label, v.UsedPercent, caption, free < 5 ? Severity.Critical : free < 10 ? Severity.Warning : Severity.Good);
    }

    // ---------------------------------------------------------------- GPU

    private static IEnumerable<DeviceSection> Gpus(SystemSnapshot s, ComponentHealth health, Func<string?, string> serial)
    {
        foreach (var g in s.Gpus)
        {
            var device = s.Sensors.Device(g.SensorHardwareId);
            var temp = device.Find("Temperature", "GPU Core");
            var load = device.Find("Load", "GPU Core", "D3D 3D");
            var power = device.Find("Power", "GPU Package", "GPU Power", "GPU Core");
            var memUsed = device.Find("SmallData", "GPU Memory Used", "D3D Dedicated Memory Used");
            var memTotal = device.Find("SmallData", "GPU Memory Total");
            var severity = health.Findings.Where(f => f.Args.Length > 0 && Equals(f.Args[0], g.Name)).Select(f => f.Severity).DefaultIfEmpty(Severity.Good).Max();

            var d = new DeviceSection
            {
                Title = Fmt.Or(g.Name),
                Subtitle = string.Join(" · ", new[] { g.VramBytes > 0 ? Fmt.Bytes(g.VramBytes, 0) + " VRAM" : "", g.DriverVersion.Length > 0 ? Loc.T("gpu.driver", g.DriverVersion) : "" }.Where(x => x.Length > 0)),
                Severity = severity,
            };
            d.Highlights.Add(new(Loc.T("label.temperature"), Fmt.Temp(g.TemperatureC), temp?.Id, "Temperature"));
            d.Highlights.Add(new(Loc.T("label.load"), Fmt.Percent(g.LoadPercent), load?.Id, "Load"));
            d.Highlights.Add(new(Loc.T("label.power"), Fmt.Watts(g.PowerW), power?.Id, "Power"));
            d.Highlights.Add(new(Loc.T("label.vramused"), memUsed?.Value is { } mu ? Fmt.Sensor("SmallData", mu) : Fmt.Dash, memUsed?.Id, "SmallData"));

            if (load is not null)
                d.Meters.Add(new(Loc.T("label.load"), load.Value ?? 0, Fmt.Percent(load.Value), LoadSeverity(load.Value), load.Id));
            if (memUsed?.Value is { } used && (memTotal?.Value ?? g.VramBytes / 1048576.0) is var total and > 0)
                d.Meters.Add(new(Loc.T("gpu.meter.vram"), 100.0 * used / total, $"{Fmt.Bytes((ulong)(used * 1048576.0))} / {Fmt.Bytes((ulong)(total * 1048576.0), 0)}", Severity.Good));

            d.Specs.Add(Group("spec.group.general",
                Row("spec.name", g.Name),
                Row("spec.vendor", g.Vendor),
                Row("spec.vram", g.VramBytes > 0 ? Fmt.Bytes(g.VramBytes, 0) : ""),
                Row("spec.status", g.Status)));
            d.Specs.Add(Group("spec.group.driver",
                Row("spec.driverversion", g.DriverVersion),
                Row("spec.driverdate", g.DriverDate is { } dd ? Fmt.Date(dd) : "")));
            d.Specs.Add(Group("spec.group.display",
                Row("spec.resolution", g.Resolution),
                Row("spec.refresh", g.RefreshRateHz > 1 ? $"{g.RefreshRateHz} Hz" : "")));
            d.Specs.Add(Group("spec.group.technical",
                Row("spec.pnpid", Fmt.MaskInstance(g.PnpDeviceId, serial))));
            if (g.SensorHardwareId is not null) d.SensorDeviceIds.Add(g.SensorHardwareId);
            yield return d;
        }
    }

    // ---------------------------------------------------------------- Battery

    private static DeviceSection Battery(BatteryInfo b, SensorSnapshot sensors)
    {
        var d = new DeviceSection
        {
            Title = Fmt.Or(b.Name.Length > 0 ? b.Name : Loc.T("component.battery")),
            Subtitle = string.Join(" · ", new[] { b.Manufacturer, b.Chemistry }.Where(x => x.Length > 0)),
        };
        var status = b.IsCharging == true ? Loc.T("battery.status.charging") : b.OnAcPower == true ? Loc.T("battery.status.ac") : Loc.T("battery.status.discharging");
        d.Highlights.Add(new(Loc.T("label.health"), Fmt.Percent(b.HealthPercent)));
        d.Highlights.Add(new(Loc.T("label.charge"), Fmt.Percent(b.ChargePercent)));
        d.Highlights.Add(new(Loc.T("label.cycles"), b.CycleCount is { } c ? c.ToString() : Fmt.Dash));
        d.Highlights.Add(new(Loc.T("label.status"), status));

        if (b.HealthPercent is { } h)
            d.Meters.Add(new(Loc.T("battery.meter.health"), h, $"{Fmt.Number(b.FullChargeCapacityMWh)} / {Fmt.Number(b.DesignCapacityMWh)} mWh", h < 50 ? Severity.Critical : h < 70 ? Severity.Warning : Severity.Good));
        if (b.ChargePercent is { } charge)
            d.Meters.Add(new(Loc.T("battery.meter.charge"), charge, Fmt.Percent(charge), Severity.Good));

        d.Specs.Add(Group("spec.group.general",
            Row("spec.name", b.Name),
            Row("spec.manufacturer", b.Manufacturer),
            Row("spec.chemistry", b.Chemistry),
            Row("spec.designcapacity", b.DesignCapacityMWh is { } dc ? $"{Fmt.Number(dc)} mWh" : ""),
            Row("spec.fullcapacity", b.FullChargeCapacityMWh is { } fc ? $"{Fmt.Number(fc)} mWh" : ""),
            Row("spec.wear", b.HealthPercent is { } hp ? Fmt.Percent(100 - hp, 1) : ""),
            Row("spec.cycles", b.CycleCount?.ToString())));
        d.SensorDeviceIds.AddRange(sensors.OfKind("Battery").Select(x => x.Id));
        return d;
    }

    // ---------------------------------------------------------------- Motherboard & OS

    private static IEnumerable<DeviceSection> Motherboard(SystemSnapshot s, Func<string?, string> serial)
    {
        var b = s.Board;
        var board = new DeviceSection
        {
            Title = Fmt.Or($"{b.Manufacturer} {b.Product}".Trim()),
            Subtitle = Loc.T("board.subtitle", Fmt.Or(b.BiosVersion), Fmt.Date(b.BiosDate)),
        };
        board.Highlights.Add(new(Loc.T("label.firmware"), b.IsUefi switch { true => "UEFI", false => "Legacy BIOS", _ => Fmt.Dash }));
        board.Highlights.Add(new(Loc.T("label.secureboot"), Fmt.OnOff(b.SecureBoot)));
        board.Highlights.Add(new(Loc.T("label.tpm"), b.TpmPresent is null ? Fmt.Dash : b.TpmPresent == true ? Fmt.Or(b.TpmVersion) : Loc.T("common.off")));
        board.Highlights.Add(new(Loc.T("label.formfactor"), Loc.T(b.IsLaptop ? "board.laptop" : "board.desktop")));

        board.Specs.Add(Group("spec.group.motherboard",
            Row("spec.manufacturer", b.Manufacturer),
            Row("spec.model", b.Product),
            Row("spec.version", b.Version),
            Row("spec.serial", serial(b.SerialNumber))));
        board.Specs.Add(Group("spec.group.bios",
            Row("spec.vendor", b.BiosVendor),
            Row("spec.version", b.BiosVersion),
            Row("spec.date", b.BiosDate is { } bd ? Fmt.Date(bd) : ""),
            Row("spec.firmwaretype", b.IsUefi switch { true => "UEFI", false => "Legacy BIOS", _ => "" }),
            Row("spec.secureboot", Fmt.OnOff(b.SecureBoot)),
            Row("spec.tpm", b.TpmPresent is null ? Loc.T(s.IsElevated ? "common.unavailable" : "common.needsadmin") : b.TpmPresent == true ? Fmt.Or(b.TpmVersion) : Loc.T("common.off"))));
        board.Specs.Add(Group("spec.group.system",
            Row("spec.manufacturer", b.SystemManufacturer),
            Row("spec.model", b.SystemModel)));
        board.SensorDeviceIds.AddRange(s.Sensors.OfKind("Motherboard").SelectMany(m => s.Sensors.DeviceTree(m.Id)).Select(x => x.Id));
        board.SensorDeviceIds.AddRange(s.Sensors.Devices.Where(x => x.Kind is "Cooler" or "Psu" or "EmbeddedController").Select(x => x.Id));
        yield return board;

        var os = s.Os;
        var osSection = new DeviceSection
        {
            Title = Fmt.Or(os.Name),
            Subtitle = string.Join(" · ", new[] { os.DisplayVersion, os.Build.Length > 0 ? Loc.T("os.build", os.Build) : "", os.Architecture }.Where(x => x.Length > 0)),
        };
        osSection.Highlights.Add(new(Loc.T("label.uptime"), Fmt.Duration(os.Uptime)));
        osSection.Highlights.Add(new(Loc.T("label.lastboot"), Fmt.DateAndTime(os.LastBoot)));
        osSection.Highlights.Add(new(Loc.T("label.installed"), Fmt.Date(os.InstallDate)));
        osSection.Highlights.Add(new(Loc.T("label.admin"), Fmt.YesNo(s.IsElevated)));
        osSection.Specs.Add(Group("spec.group.os",
            Row("spec.name", os.Name),
            Row("spec.version", os.Version),
            Row("spec.displayversion", os.DisplayVersion),
            Row("spec.build", os.Build),
            Row("spec.architecture", os.Architecture),
            Row("spec.installdate", os.InstallDate is { } id ? Fmt.Date(id) : ""),
            Row("spec.lastboot", os.LastBoot is { } lb ? Fmt.DateAndTime(lb) : ""),
            Row("spec.uptime", Fmt.Duration(os.Uptime))));
        yield return osSection;
    }

    // ---------------------------------------------------------------- Stability

    private static IEnumerable<DeviceSection> Stability(SystemSnapshot s)
    {
        var st = s.Stability;
        var log = new DeviceSection
        {
            Title = Loc.T("stability.log.title"),
            Subtitle = st.Available ? Loc.T("stability.log.subtitle", StabilityInfo.PeriodDays) : Loc.T("stability.unavailable.title"),
        };
        log.Highlights.Add(new(Loc.T("label.shutdowns"), st.UnexpectedShutdowns.ToString()));
        log.Highlights.Add(new(Loc.T("label.bluescreens"), st.BlueScreens.ToString()));
        log.Highlights.Add(new(Loc.T("label.whea"), (st.WheaErrors + st.WheaCorrected).ToString()));
        log.Highlights.Add(new(Loc.T("label.diskerrors"), (st.DiskErrors + st.FileSystemErrors).ToString()));
        if (st.RecentEvents.Count > 0)
        {
            log.Tables.Add(new TableBlock(Loc.T("stability.table.events"),
                [Loc.T("col.time"), Loc.T("col.category"), Loc.T("col.source"), Loc.T("col.eventid"), Loc.T("col.level")],
                st.RecentEvents.Select(e => (IReadOnlyList<string>)
                [
                    Fmt.DateAndTime(e.Time),
                    Loc.T("event.category." + e.Category),
                    e.Source,
                    e.EventId.ToString(),
                    Loc.Instance.Has("event.level." + e.Level.ToLowerInvariant()) ? Loc.T("event.level." + e.Level.ToLowerInvariant()) : e.Level,
                ]).ToList()));
        }
        yield return log;

        var devices = new DeviceSection
        {
            Title = Loc.T("stability.devices.title"),
            Subtitle = s.ProblemDevices.Count == 0 ? Loc.T("stability.devices.none") : Loc.T("stability.devices.count", s.ProblemDevices.Count),
        };
        if (s.ProblemDevices.Count > 0)
        {
            devices.Tables.Add(new TableBlock(Loc.T("stability.table.devices"),
                [Loc.T("col.device"), Loc.T("col.class"), Loc.T("col.code"), Loc.T("col.meaning")],
                s.ProblemDevices.Select(p => (IReadOnlyList<string>)
                [
                    p.Name, Fmt.Or(p.Class), p.ErrorCode.ToString(), Loc.T(HealthAnalyzer.DeviceErrorKey(p.ErrorCode)),
                ]).ToList()));
        }
        yield return devices;
    }

    // ---------------------------------------------------------------- helpers

    private static SpecRow? Row(string key, string? value) =>
        string.IsNullOrWhiteSpace(value) || value == Fmt.Dash ? null : new SpecRow(Loc.T(key), value.Trim());

    private static SpecGroup Group(string titleKey, params SpecRow?[] rows) =>
        new(Loc.T(titleKey), rows.OfType<SpecRow>().ToList());

    private static Severity LoadSeverity(double? value, double warn = 101, double crit = 101) =>
        value >= crit ? Severity.Critical : value >= warn ? Severity.Warning : Severity.Good;

    public static string MediaName(DiskMedia media) => media switch
    {
        DiskMedia.Nvme => "NVMe SSD",
        DiskMedia.Ssd => "SSD",
        DiskMedia.Hdd => "HDD",
        _ => "",
    };

    public static string HealthName(DiskHealth health) => Loc.T("diskhealth." + health.ToString().ToLowerInvariant());
}
