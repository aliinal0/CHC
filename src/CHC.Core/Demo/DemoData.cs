using CHC.Core.Collection;
using CHC.Core.Models;
using CHC.Core.Sensors;

namespace CHC.Core.Demo;

/// <summary>
/// A made-up gaming PC for screenshots and for trying CHC without scanning (<c>CHC.exe --demo</c>).
/// It deliberately has a few problems (a worn hard disk, an almost full drive, corrected
/// hardware errors) so every part of the UI has something to show. Nothing here comes from
/// the computer CHC runs on.
/// </summary>
public sealed class DemoData
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private readonly Random _random = new(2026);
    private readonly Dictionary<string, (float Min, float Max)> _extremes = [];
    private readonly double[] _coreLoads = new double[16];
    private double _cpuLoad = 12, _cpuTemp = 47, _gpuLoad = 6, _gpuTemp = 41, _ramLoad = 46, _nvmeTemp = 39;

    public SystemSnapshot CreateSnapshot()
    {
        var now = DateTime.Now;
        var sensors = NextSensors();
        var snapshot = new SystemSnapshot
        {
            TakenAt = now,
            IsElevated = true,
            AppVersion = SnapshotCollector.AppVersion,
            Sensors = sensors,
            Os = new OsInfo
            {
                Name = "Windows 11 Home",
                Version = "10.0.26200",
                Build = "26200.6899",
                DisplayVersion = "25H2",
                Architecture = "64-bit",
                InstallDate = now.AddMonths(-7).Date,
                LastBoot = now.AddDays(-2).AddHours(-3),
            },
            Board = new BoardInfo
            {
                SystemManufacturer = "ASUS",
                SystemModel = "System Product Name",
                Manufacturer = "ASUSTeK COMPUTER INC.",
                Product = "TUF GAMING B650-PLUS WIFI",
                Version = "Rev 1.xx",
                SerialNumber = "DEMO0000000000",
                BiosVendor = "American Megatrends Inc.",
                BiosVersion = "3201",
                BiosDate = new DateTime(2025, 8, 14),
                IsUefi = true,
                SecureBoot = true,
                TpmPresent = true,
                TpmVersion = "2.0",
            },
            Cpu = new CpuInfo
            {
                Name = "AMD Ryzen 7 7800X3D 8-Core Processor",
                Manufacturer = "AMD",
                Identifier = "AMD64 Family 25 Model 97 Stepping 2",
                Socket = "AM5",
                Cores = 8,
                Threads = 16,
                BaseClockMHz = 4201,
                CurrentClockMHz = 4201,
                L2CacheKB = 8 * 1024,
                L3CacheKB = 96 * 1024,
                VirtualizationEnabled = true,
                SensorHardwareId = "/amdcpu/0",
            },
            Memory = new MemoryInfo
            {
                TotalBytes = 32 * GiB - 340 * 1024 * 1024,
                SlotCount = 4,
                MaxCapacityBytes = 192 * GiB,
                CommitLimitBytes = 37 * GiB,
                CommitTotalBytes = 21 * GiB,
                Modules =
                [
                    Dimm("DIMM_A2", "P0 CHANNEL A"),
                    Dimm("DIMM_B2", "P0 CHANNEL B"),
                ],
            },
            Disks = [Nvme(), Hdd()],
            Volumes =
            [
                new VolumeInfo { Letter = "C:", Label = "Windows", FileSystem = "NTFS", TotalBytes = 1862 * GiB, FreeBytes = 1104 * GiB, IsSystem = true },
                new VolumeInfo { Letter = "D:", Label = "Archive", FileSystem = "NTFS", TotalBytes = 1862 * GiB, FreeBytes = 121 * GiB },
            ],
            Gpus =
            [
                new GpuInfo
                {
                    Name = "NVIDIA GeForce RTX 4070 SUPER",
                    Vendor = "NVIDIA",
                    DriverVersion = "32.0.15.8157",
                    DriverDate = now.AddDays(-41).Date,
                    VramBytes = 12 * GiB,
                    Resolution = "2560 × 1440",
                    RefreshRateHz = 165,
                    Status = "OK",
                    PnpDeviceId = @"PCI\VEN_10DE&DEV_2783&SUBSYS_00000000&REV_A1\DEMO",
                    SensorHardwareId = "/gpu-nvidia/0",
                },
            ],
            Stability = new StabilityInfo
            {
                Available = true,
                WheaCorrected = 2,
                RecentEvents =
                [
                    WheaEvent(now.AddDays(-6).AddHours(-4)),
                    WheaEvent(now.AddDays(-19).AddHours(-9)),
                ],
            },
        };
        SnapshotCollector.ApplyLiveSensors(snapshot, sensors);
        ApplyMemory(snapshot.Memory);
        return snapshot;
    }

    /// <summary>Next set of live values: a gentle random walk so charts and tiles look alive.</summary>
    public SensorSnapshot NextSensors()
    {
        _cpuLoad = Walk(_cpuLoad, 12, 7, 2, 85, spike: 0.08);
        _cpuTemp = Walk(_cpuTemp, 44 + _cpuLoad * 0.25, 1.2, 38, 82);
        _gpuLoad = Walk(_gpuLoad, 6, 4, 1, 70, spike: 0.05);
        _gpuTemp = Walk(_gpuTemp, 39 + _gpuLoad * 0.3, 0.8, 34, 78);
        _ramLoad = Walk(_ramLoad, 46, 1.5, 38, 70);
        _nvmeTemp = Walk(_nvmeTemp, 39, 0.4, 35, 50);
        for (var i = 0; i < _coreLoads.Length; i++)
            _coreLoads[i] = Math.Clamp(_cpuLoad + (_random.NextDouble() - 0.5) * 30, 0, 100);

        var totalGb = 31.67;
        var usedGb = totalGb * _ramLoad / 100;
        var cpuClock = 4700 + _cpuLoad * 3 + _random.NextDouble() * 120;

        var cpu = new List<SensorReading>
        {
            R("/amdcpu/0/load/0", "CPU Total", "Load", _cpuLoad),
            R("/amdcpu/0/load/1", "CPU Core Max", "Load", _coreLoads.Max()),
            R("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "Temperature", _cpuTemp),
            R("/amdcpu/0/temperature/3", "CCD1 (Tdie)", "Temperature", _cpuTemp - 3.5),
            R("/amdcpu/0/clock/1", "Cores (Average)", "Clock", cpuClock),
            R("/amdcpu/0/power/0", "Package", "Power", 32 + _cpuLoad * 0.7),
            R("/amdcpu/0/voltage/0", "Core (SVI3 TFN)", "Voltage", 1.05 + _cpuLoad * 0.002),
        };
        for (var i = 0; i < _coreLoads.Length; i++)
            cpu.Add(R($"/amdcpu/0/load/{i + 2}", $"CPU Core #{i + 1}", "Load", _coreLoads[i]));
        for (var i = 0; i < 8; i++)
            cpu.Add(R($"/amdcpu/0/clock/{i + 3}", $"Core #{i + 1}", "Clock", cpuClock + (_random.NextDouble() - 0.5) * 150));

        var devices = new List<SensorDevice>
        {
            new("/motherboard", "ASUS TUF GAMING B650-PLUS WIFI", "Motherboard", null, []),
            new("/lpc/nct6799d", "Nuvoton NCT6799D", "SuperIO", "/motherboard",
            [
                R("/lpc/nct6799d/temperature/0", "System", "Temperature", 33 + _cpuLoad * 0.05),
                R("/lpc/nct6799d/fan/0", "CPU Fan", "Fan", 820 + _cpuTemp * 9),
                R("/lpc/nct6799d/fan/1", "System Fan #1", "Fan", 760 + _random.NextDouble() * 20),
                R("/lpc/nct6799d/voltage/0", "Vcore", "Voltage", 1.05 + _cpuLoad * 0.002),
                R("/lpc/nct6799d/voltage/1", "+12V", "Voltage", 12.07 + (_random.NextDouble() - 0.5) * 0.02),
                R("/lpc/nct6799d/voltage/2", "+5V", "Voltage", 5.04 + (_random.NextDouble() - 0.5) * 0.01),
            ]),
            new("/amdcpu/0", "AMD Ryzen 7 7800X3D", "Cpu", null, cpu),
            new("/ram", "Total Memory", "Memory", null,
            [
                R("/ram/data/0", "Memory Used", "Data", usedGb),
                R("/ram/data/1", "Memory Available", "Data", totalGb - usedGb),
                R("/ram/load/0", "Memory", "Load", _ramLoad),
            ]),
            new("/gpu-nvidia/0", "NVIDIA GeForce RTX 4070 SUPER", "GpuNvidia", null,
            [
                R("/gpu-nvidia/0/temperature/0", "GPU Core", "Temperature", _gpuTemp),
                R("/gpu-nvidia/0/temperature/2", "GPU Hot Spot", "Temperature", _gpuTemp + 9),
                R("/gpu-nvidia/0/load/0", "GPU Core", "Load", _gpuLoad),
                R("/gpu-nvidia/0/load/1", "GPU Memory Controller", "Load", _gpuLoad * 0.6),
                R("/gpu-nvidia/0/clock/0", "GPU Core", "Clock", 210 + _gpuLoad * 26),
                R("/gpu-nvidia/0/clock/4", "GPU Memory", "Clock", _gpuLoad > 15 ? 10501 : 405),
                R("/gpu-nvidia/0/power/0", "GPU Package", "Power", 14 + _gpuLoad * 2.1),
                R("/gpu-nvidia/0/fan/1", "GPU Fan 1", "Fan", _gpuTemp > 55 ? 1100 + _gpuTemp * 8 : 0),
                R("/gpu-nvidia/0/smalldata/2", "GPU Memory Total", "SmallData", 12282),
                R("/gpu-nvidia/0/smalldata/1", "GPU Memory Used", "SmallData", 1450 + _gpuLoad * 60),
                R("/gpu-nvidia/0/smalldata/0", "GPU Memory Free", "SmallData", 12282 - 1450 - _gpuLoad * 60),
            ]),
            new("/nvme/0", "Samsung SSD 990 PRO 2TB", "Storage", null,
            [
                R("/nvme/0/temperature/0", "Temperature", "Temperature", _nvmeTemp),
                R("/nvme/0/level/0", "Available Spare", "Level", 100),
                R("/nvme/0/level/2", "Percentage Used", "Level", 3),
                R("/nvme/0/load/0", "Used Space", "Load", 40.7),
                R("/nvme/0/data/0", "Data Read", "Data", 24100),
                R("/nvme/0/data/1", "Data Written", "Data", 18450),
            ]),
            new("/hdd/1", "WDC WD20EZBX-00AYRA0", "Storage", null,
            [
                R("/hdd/1/temperature/0", "Temperature", "Temperature", 36 + (_random.NextDouble() - 0.5) * 0.4),
                R("/hdd/1/load/0", "Used Space", "Load", 93.5),
            ]),
        };
        return new SensorSnapshot(devices, DateTime.Now);
    }

    /// <summary>Keeps the static memory numbers in line with the live memory sensors.</summary>
    public void ApplyMemory(MemoryInfo memory)
    {
        memory.LoadPercent = Math.Round(_ramLoad);
        memory.AvailableBytes = (ulong)(memory.TotalBytes * (1 - _ramLoad / 100));
    }

    private double Walk(double value, double target, double noise, double min, double max, double spike = 0)
    {
        value += (target - value) * 0.25 + (_random.NextDouble() - 0.5) * 2 * noise;
        if (_random.NextDouble() < spike) value += 25 + _random.NextDouble() * 30;
        return Math.Clamp(value, min, max);
    }

    private SensorReading R(string id, string name, string type, double value)
    {
        var v = (float)value;
        var (min, max) = _extremes.TryGetValue(id, out var e) ? (Math.Min(e.Min, v), Math.Max(e.Max, v)) : (v, v);
        _extremes[id] = (min, max);
        return new SensorReading(id, name, type, v, min, max);
    }

    private static MemoryModule Dimm(string slot, string bank) => new()
    {
        Slot = slot,
        Bank = bank,
        CapacityBytes = 16 * GiB,
        SpeedMTs = 6000,
        ConfiguredSpeedMTs = 6000,
        Manufacturer = "G Skill Intl",
        PartNumber = "F5-6000J3038F16G",
        SerialNumber = "00000000",
        Type = "DDR5",
        FormFactor = "DIMM",
        VoltageMv = 1350,
    };

    private static DiskInfo Nvme() => new()
    {
        Index = 0,
        Model = "Samsung SSD 990 PRO 2TB",
        SerialNumber = "S7DEMO0000000001",
        Firmware = "4B2QJXD7",
        BusType = "NVMe",
        Media = DiskMedia.Nvme,
        SizeBytes = 2_000_398_934_016,
        IsSystemDisk = true,
        TrimSupported = true,
        WindowsHealth = DiskHealth.Good,
        OperationalStatus = "OK",
        SmartHealth = DiskHealth.Good,
        SmartAvailable = true,
        TemperatureC = 39,
        TemperatureWarningC = 82,
        TemperatureCriticalC = 85,
        LifePercent = 97,
        PowerOnHours = 2380,
        PowerOnCount = 412,
        HostWritesGB = 18450,
        HostReadsGB = 24100,
        ReadErrorsUncorrected = 0,
        DriveLetters = ["C:"],
        SensorHardwareId = "/nvme/0",
        SmartAttributes =
        [
            Smart(0x01, "Critical Warning", "CriticalWarning", 0),
            Smart(0x02, "Composite Temperature", "CompositeTemperature", 312),
            Smart(0x03, "Available Spare", "AvailableSpare", 100),
            Smart(0x04, "Available Spare Threshold", "AvailableSpareThreshold", 10),
            Smart(0x05, "Percentage Used", "PercentageUsed", 3),
            Smart(0x06, "Data Units Read", "HostDataRead", 51_394_211),
            Smart(0x07, "Data Units Written", "HostDataWritten", 39_345_877),
            Smart(0x08, "Host Read Commands", "HostReadCommands", 412_088_120),
            Smart(0x09, "Host Write Commands", "HostWriteCommands", 655_302_418),
            Smart(0x0A, "Controller Busy Time", "ControllerBusyTime", 1_204),
            Smart(0x0B, "Power Cycles", "PowerCycleCount", 412),
            Smart(0x0C, "Power On Hours", "PowerOnHours", 2_380),
            Smart(0x0D, "Unsafe Shutdowns", "UnsafeShutdownCount", 17),
            Smart(0x0E, "Media and Data Integrity Errors", "MediaAndDataIntegrityErrors", 0),
            Smart(0x0F, "Number of Error Information Log Entries", "NumberOfErrorInformationLogEntries", 0),
        ],
    };

    private static DiskInfo Hdd() => new()
    {
        Index = 1,
        Model = "WDC WD20EZBX-00AYRA0",
        SerialNumber = "WD-DEMO00000002",
        Firmware = "01.01A01",
        BusType = "SATA",
        Media = DiskMedia.Hdd,
        SizeBytes = 2_000_398_934_016,
        TrimSupported = false,
        WindowsHealth = DiskHealth.Good,
        OperationalStatus = "OK",
        SmartHealth = DiskHealth.Caution,
        SmartAvailable = true,
        TemperatureC = 36,
        TemperatureWarningC = 50,
        TemperatureCriticalC = 60,
        PowerOnHours = 31_240,
        PowerOnCount = 2_950,
        ReadErrorsUncorrected = 0,
        DriveLetters = ["D:"],
        SensorHardwareId = "/hdd/1",
        SmartAttributes =
        [
            Smart(0x01, "Read Error Rate", "ReadErrorRate", 0, 200, 200, 51),
            Smart(0x03, "Spin-Up Time", "SpinUpTime", 4_183, 175, 172, 21),
            Smart(0x04, "Start/Stop Count", "StartStopCount", 3_021, 97, 97, 0),
            Smart(0x05, "Reallocated Sectors Count", "ReallocatedSectorsCount", 8, 199, 199, 140),
            Smart(0x07, "Seek Error Rate", "SeekErrorRate", 0, 200, 200, 0),
            Smart(0x09, "Power-On Hours", "PowerOnHours", 31_240, 58, 58, 0),
            Smart(0x0A, "Spin Retry Count", "SpinRetryCount", 0, 100, 100, 0),
            Smart(0x0C, "Power Cycle Count", "PowerCycleCount", 2_950, 98, 98, 0),
            Smart(0xC2, "Temperature", "Temperature", 36, 114, 103, 0),
            Smart(0xC4, "Reallocation Event Count", "ReallocationEventCount", 8, 192, 192, 0),
            Smart(0xC5, "Current Pending Sector Count", "PendingSectorCount", 0, 200, 200, 0),
            Smart(0xC6, "Uncorrectable Sector Count", "UncorrectableSectorCount", 0, 100, 253, 0),
            Smart(0xC7, "UltraDMA CRC Error Count", "CRCErrorCount", 0, 200, 200, 0),
        ],
    };

    private static SmartAttributeInfo Smart(byte id, string name, string kind, ulong raw, byte current = 0, byte worst = 0, byte threshold = 0) =>
        new() { Id = id, Name = name, Kind = kind, Raw = raw, Current = current, Worst = worst, Threshold = threshold };

    private static EventEntry WheaEvent(DateTime time) => new()
    {
        Time = time,
        Source = "Microsoft-Windows-WHEA-Logger",
        EventId = 17,
        Level = "Warning",
        Category = "whea-corrected",
    };
}
