using CHC.Core.Models;
using CHC.Core.Sensors;
using Microsoft.Win32;

namespace CHC.Core.Collection;

/// <summary>Reads OS, motherboard, CPU, memory, GPU, battery and device information.</summary>
internal static class SystemCollector
{
    public static OsInfo CollectOs()
    {
        var os = new OsInfo();
        if (Wmi.First("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem") is { } o)
        {
            os.Name = o.Str("Caption").Replace("Microsoft ", "");
            os.Version = o.Str("Version");
            os.Build = o.Str("BuildNumber");
            os.Architecture = o.Str("OSArchitecture");
            os.InstallDate = o.Date("InstallDate");
            os.LastBoot = o.Date("LastBootUpTime");
        }
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key is not null)
        {
            os.DisplayVersion = key.GetValue("DisplayVersion")?.ToString() ?? "";
            if (key.GetValue("UBR") is int ubr && os.Build.Length > 0) os.Build = $"{os.Build}.{ubr}";
        }
        return os;
    }

    public static BoardInfo CollectBoard()
    {
        var board = new BoardInfo();
        if (Wmi.First("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem") is { } cs)
        {
            board.SystemManufacturer = cs.Str("Manufacturer");
            board.SystemModel = cs.Str("Model");
            board.IsLaptop = cs.I32("PCSystemType") == 2;
        }
        if (Wmi.First("SELECT ChassisTypes FROM Win32_SystemEnclosure") is { } enc)
            board.IsLaptop |= enc.IntArray("ChassisTypes").Any(t => t is 8 or 9 or 10 or 14 or 30 or 31 or 32);
        if (Wmi.First("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard") is { } bb)
        {
            board.Manufacturer = bb.Str("Manufacturer");
            board.Product = bb.Str("Product");
            board.Version = bb.Str("Version");
            board.SerialNumber = bb.Str("SerialNumber");
        }
        if (Wmi.First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS") is { } bios)
        {
            board.BiosVendor = bios.Str("Manufacturer");
            board.BiosVersion = bios.Str("SMBIOSBIOSVersion");
            board.BiosDate = bios.Date("ReleaseDate");
        }

        board.IsUefi = Native.IsUefi();
        using (var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            board.SecureBoot = sb?.GetValue("UEFISecureBootEnabled") is int enabled ? enabled == 1 : board.IsUefi == false ? false : null;

        // Win32_Tpm is only visible to administrators; leave null otherwise.
        if (Wmi.First("SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm", Wmi.Tpm) is { } tpm)
        {
            board.TpmPresent = tpm.Bool("IsEnabled_InitialValue") ?? true;
            board.TpmVersion = tpm.Str("SpecVersion").Split(',')[0].Trim();
        }
        return board;
    }

    public static CpuInfo CollectCpu(SensorSnapshot sensors)
    {
        var cpu = new CpuInfo();
        var all = Wmi.Query("SELECT Name, Manufacturer, Caption, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled, LoadPercentage FROM Win32_Processor");
        if (all.FirstOrDefault() is { } p)
        {
            cpu.Name = p.Str("Name");
            cpu.Manufacturer = p.Str("Manufacturer") switch
            {
                "AuthenticAMD" => "AMD",
                "GenuineIntel" => "Intel",
                var m => m,
            };
            cpu.Identifier = p.Str("Caption");
            cpu.Socket = p.Str("SocketDesignation");
            // Multi-socket machines: add up cores and threads.
            cpu.Cores = all.Sum(x => x.I32("NumberOfCores"));
            cpu.Threads = all.Sum(x => x.I32("NumberOfLogicalProcessors"));
            cpu.BaseClockMHz = p.I32("MaxClockSpeed");
            cpu.CurrentClockMHz = p.I32("CurrentClockSpeed");
            cpu.L2CacheKB = p.I32("L2CacheSize");
            cpu.L3CacheKB = p.I32("L3CacheSize");
            cpu.VirtualizationEnabled = p.Bool("VirtualizationFirmwareEnabled");
            cpu.LoadPercent = p.I32("LoadPercentage");
        }

        var device = sensors.OfKind("Cpu").FirstOrDefault();
        if (device is not null)
        {
            cpu.SensorHardwareId = device.Id;
            if (cpu.Name.Length == 0) cpu.Name = device.Name;
            cpu.LoadPercent = device.Find("Load", "CPU Total")?.Value ?? cpu.LoadPercent;
            cpu.TemperatureC = CpuTemperature(device)?.Positive();
            cpu.PackagePowerW = device.Find("Power", "Package", "CPU Package").Positive();
        }
        return cpu;
    }

    /// <summary>The sensor that best represents "the CPU temperature" on AMD and Intel.</summary>
    public static SensorReading? CpuTemperature(SensorDevice? cpu) =>
        cpu.Find("Temperature", "Core (Tctl/Tdie)", "CPU Package", "Core Max", "Core (Tctl)", "Core Average", "CPU Cores");

    public static MemoryInfo CollectMemory()
    {
        var mem = new MemoryInfo();
        if (Native.MemoryStatus() is { } st)
        {
            mem.TotalBytes = st.TotalPhys;
            mem.AvailableBytes = st.AvailPhys;
            mem.LoadPercent = st.MemoryLoad;
            mem.CommitLimitBytes = st.TotalPageFile;
            mem.CommitTotalBytes = st.TotalPageFile - st.AvailPageFile;
        }
        if (Wmi.First("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray WHERE Use = 3") is { } arr)
        {
            mem.SlotCount = arr.I32("MemoryDevices");
            mem.MaxCapacityBytes = arr.U64("MaxCapacityEx") * 1024;
        }
        foreach (var m in Wmi.Query("SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SerialNumber, SMBIOSMemoryType, FormFactor, ConfiguredVoltage FROM Win32_PhysicalMemory"))
        {
            mem.Modules.Add(new MemoryModule
            {
                Slot = m.Str("DeviceLocator"),
                Bank = m.Str("BankLabel"),
                CapacityBytes = m.U64("Capacity"),
                SpeedMTs = m.I32("Speed"),
                ConfiguredSpeedMTs = m.I32("ConfiguredClockSpeed"),
                Manufacturer = m.Str("Manufacturer"),
                PartNumber = m.Str("PartNumber"),
                SerialNumber = m.Str("SerialNumber"),
                Type = MemoryType(m.I32("SMBIOSMemoryType")),
                FormFactor = m.I32("FormFactor") switch { 8 => "DIMM", 12 => "SO-DIMM", 0 => "", var f => $"#{f}" },
                VoltageMv = m.I32("ConfiguredVoltage"),
            });
        }
        return mem;
    }

    private static string MemoryType(int smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        22 => "DDR2 FB-DIMM",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };

    public static List<GpuInfo> CollectGpus(SensorSnapshot sensors)
    {
        var gpus = new List<GpuInfo>();
        foreach (var v in Wmi.Query("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, Status, PNPDeviceID FROM Win32_VideoController"))
        {
            var pnp = v.Str("PNPDeviceID");
            // Skip software adapters (remote desktop, virtual displays, basic render driver).
            if (pnp.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)) continue;

            var width = v.I32("CurrentHorizontalResolution");
            var height = v.I32("CurrentVerticalResolution");
            var gpu = new GpuInfo
            {
                Name = v.Str("Name"),
                Vendor = v.Str("AdapterCompatibility"),
                DriverVersion = v.Str("DriverVersion"),
                DriverDate = v.Date("DriverDate"),
                Resolution = width > 0 ? $"{width} × {height}" : "",
                RefreshRateHz = v.I32("CurrentRefreshRate"),
                Status = v.Str("Status"),
                PnpDeviceId = pnp,
                // AdapterRAM is a 32-bit value (max 4 GB); the registry has the real size.
                VramBytes = RegistryVram(pnp) ?? v.U64("AdapterRAM"),
            };
            gpus.Add(gpu);
        }

        var devices = sensors.OfKind("Gpu").ToList();
        foreach (var gpu in gpus)
        {
            var device = devices.FirstOrDefault(d => Normalize(d.Name) == Normalize(gpu.Name))
                         ?? devices.FirstOrDefault(d => Normalize(gpu.Name).Contains(Normalize(d.Name)) || Normalize(d.Name).Contains(Normalize(gpu.Name)))
                         ?? (gpus.Count == 1 && devices.Count == 1 ? devices[0] : null);
            if (device is null) continue;
            devices.Remove(device);
            ApplyGpuSensors(gpu, device);
        }
        return gpus;
    }

    public static void ApplyGpuSensors(GpuInfo gpu, SensorDevice device)
    {
        gpu.SensorHardwareId = device.Id;
        gpu.TemperatureC = device.Find("Temperature", "GPU Core").Positive();
        gpu.HotSpotC = device.Find("Temperature", "GPU Hot Spot").Positive();
        gpu.MemoryJunctionC = device.Find("Temperature", "GPU Memory Junction").Positive();
        gpu.LoadPercent = device.Find("Load", "GPU Core", "D3D 3D")?.Value;
        gpu.PowerW = device.Find("Power", "GPU Package", "GPU Power", "GPU Core").Positive();
        if (device.Find("SmallData", "GPU Memory Total").Positive() is { } totalMb && (ulong)(totalMb * 1024 * 1024) > gpu.VramBytes)
            gpu.VramBytes = (ulong)(totalMb * 1024 * 1024);
    }

    private static string Normalize(string s)
    {
        s = s.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase);
        return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static ulong? RegistryVram(string pnpDeviceId)
    {
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(displayClass);
            if (root is null) return null;
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                var matching = sub?.GetValue("MatchingDeviceId")?.ToString();
                if (sub is null || string.IsNullOrEmpty(matching) || !pnpDeviceId.StartsWith(matching, StringComparison.OrdinalIgnoreCase)) continue;
                switch (sub.GetValue("HardwareInformation.qwMemorySize"))
                {
                    case long l when l > 0: return (ulong)l;
                    case byte[] b when b.Length >= 8: return BitConverter.ToUInt64(b, 0);
                }
                switch (sub.GetValue("HardwareInformation.MemorySize"))
                {
                    case int i when i != 0: return (uint)i;
                    case byte[] b when b.Length >= 4: return BitConverter.ToUInt32(b, 0);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    public static BatteryInfo? CollectBattery(SensorSnapshot sensors)
    {
        var win32 = Wmi.First("SELECT Name, EstimatedChargeRemaining, BatteryStatus, Chemistry FROM Win32_Battery");
        var lhm = sensors.OfKind("Battery").FirstOrDefault();
        if (win32 is null && lhm is null) return null;

        var battery = new BatteryInfo();
        if (win32 is not null)
        {
            battery.Name = win32.Str("Name");
            battery.ChargePercent = win32.I32("EstimatedChargeRemaining");
            var status = win32.I32("BatteryStatus");
            battery.IsCharging = status is 6 or 7 or 8 or 9;
            battery.OnAcPower = status is 2 or 3 or 6 or 7 or 8 or 9;
            battery.Chemistry = win32.I32("Chemistry") switch { 3 => "Lead Acid", 4 => "NiCd", 5 => "NiMH", 6 => "Li-ion", 7 => "Zinc air", 8 => "Li-Po", _ => "" };
        }

        // root\wmi has the real capacities (Win32_Battery usually leaves them empty).
        if (Wmi.First("SELECT DesignedCapacity, ManufactureName, DeviceName FROM BatteryStaticData", Wmi.Wmi_) is { } st)
        {
            battery.DesignCapacityMWh = (uint)st.U64("DesignedCapacity");
            battery.Manufacturer = st.Str("ManufactureName");
            if (st.Str("DeviceName") is { Length: > 0 } dn) battery.Name = dn;
        }
        if (Wmi.First("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", Wmi.Wmi_) is { } fc)
            battery.FullChargeCapacityMWh = (uint)fc.U64("FullChargedCapacity");
        if (Wmi.First("SELECT CycleCount FROM BatteryCycleCount", Wmi.Wmi_) is { } cc && cc.U64("CycleCount") > 0)
            battery.CycleCount = (uint)cc.U64("CycleCount");

        if (lhm is not null)
        {
            battery.DesignCapacityMWh ??= (uint?)lhm.Find("Energy", "Designed Capacity").Positive();
            battery.FullChargeCapacityMWh ??= (uint?)lhm.Find("Energy", "Full Charged Capacity", "Fully-Charged Capacity").Positive();
            battery.ChargePercent ??= (int?)lhm.Find("Level", "Charge Level")?.Value;
            if (battery.Name.Length == 0) battery.Name = lhm.Name;
        }
        if (battery.DesignCapacityMWh == 0) battery.DesignCapacityMWh = null;
        if (battery.FullChargeCapacityMWh == 0) battery.FullChargeCapacityMWh = null;
        return battery;
    }

    public static List<ProblemDevice> CollectProblemDevices() =>
        Wmi.Query("SELECT Name, PNPClass, ConfigManagerErrorCode, PNPDeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
            .Select(d => new ProblemDevice
            {
                Name = d.Str("Name") is { Length: > 0 } n ? n : d.Str("PNPDeviceID"),
                Class = d.Str("PNPClass"),
                ErrorCode = d.I32("ConfigManagerErrorCode"),
                DeviceId = d.Str("PNPDeviceID"),
            })
            .OrderBy(d => d.Name)
            .ToList();
}
