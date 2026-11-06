using CHC.Core.Collection;
using CHC.Core.Diagnostics;
using CHC.Core.Models;
using CHC.Core.Network;
using CHC.Core.Sensors;
using CHC.Core.Timeline;

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
    private readonly object _sync = new();

    /// <summary>While true the sample PC behaves like it is running a game.</summary>
    public bool Gaming { get; set; }

    /// <summary>A laptop variant with a battery (<c>CHC.exe --demo-laptop</c>), for trying the battery checks on a desktop.</summary>
    public bool Laptop { get; init; }

    private double _batteryPercent = 78;

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
                EditionId = "Core",
                SupportEnds = Catalog.WindowsSupportEnds(26200, "Core"),
                RestartPending = true,
                TrimDisabled = false,
                StartupApps =
                [
                    "SecurityHealth", "RtkAudUService", "OneDrive", "Discord", "Steam", "EpicGamesLauncher", "Spotify",
                    "Microsoft Edge Update", "NVIDIA Broadcast", "Logitech G HUB", "Corsair iCUE", "Armoury Crate",
                    "Razer Synapse", "Battle.net", "Ubisoft Connect", "Adobe Creative Cloud", "WhatsApp", "Zoom",
                ],
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
                IsLaptop = Laptop,
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
                NonPagedPoolBytes = 612 * 1024 * 1024,
                PagedPoolBytes = 894 * 1024 * 1024,
                TopProcesses =
                [
                    new("chrome.exe", 2_460 * 1024 * 1024UL, 27),
                    new("steamwebhelper.exe", 905 * 1024 * 1024UL, 9),
                    new("Discord.exe", 712 * 1024 * 1024UL, 6),
                    new("svchost.exe", 604 * 1024 * 1024UL, 81),
                    new("MsMpEng.exe", 351 * 1024 * 1024UL, 1),
                    new("explorer.exe", 187 * 1024 * 1024UL, 1),
                    new("CHC.exe", 156 * 1024 * 1024UL, 1),
                    new("dwm.exe", 131 * 1024 * 1024UL, 1),
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
                    Name = Laptop ? "NVIDIA GeForce RTX 4070 Laptop GPU" : "NVIDIA GeForce RTX 4070 SUPER",
                    Vendor = "NVIDIA",
                    DriverVersion = "32.0.15.8157",
                    DriverDate = now.AddDays(-41).Date,
                    VramBytes = 12 * GiB,
                    Resolution = "2560 × 1440",
                    RefreshRateHz = 165,
                    Status = "OK",
                    PnpDeviceId = @"PCI\VEN_10DE&DEV_2783&SUBSYS_00000000&REV_A1\DEMO",
                    SensorHardwareId = "/gpu-nvidia/0",
                    PcieGen = 4,
                    PcieWidth = 16,
                    PcieMaxGen = 4,
                    PcieMaxWidth = 16,
                    LargestBarBytes = 16UL * GiB,
                },
            ],
            Gaming = new GamingInfo { Width = 2560, Height = 1440, RefreshHz = 165, MaxRefreshHz = 165, GameMode = true, HardwareGpuScheduling = true, MemoryIntegrity = false, BackgroundRecording = false, NativeWidth = 2560, NativeHeight = 1440, MouseAcceleration = true, VCacheDriver = true },
            Network = DemoNetwork(now),
            Power = new PowerInfo
            {
                PlanGuid = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"),
                PlanName = "Balanced",
                SleepAfterSeconds = 1800,
                HibernateAfterSeconds = 0,
                HybridSleep = false,
                FastStartup = true,
                HibernationAvailable = true,
                ModernStandby = false,
                PcieLinkPowerManagement = 1,
                UsbSelectiveSuspend = true,
                DiskOffAfterSeconds = 1200,
                ProcessorMaxState = 100,
                CpuBoostMode = 2,
            },
            Stability = new StabilityInfo
            {
                UpdateFailures =
                [
                    new UpdateFailure { Time = now.AddDays(-6), Title = "2026-10 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11 (KB5066128)", ErrorCode = "0x80073712" },
                    new UpdateFailure { Time = now.AddDays(-4), Title = "2026-10 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11 (KB5066128)", ErrorCode = "0x80073712" },
                    new UpdateFailure { Time = now.AddDays(-2), Title = "2026-10 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11 (KB5066128)", ErrorCode = "0x80073712" },
                ],
                Available = true,
                WheaCorrected = 2,
                Whea =
                [
                    new WheaRecord { Time = now.AddDays(-6).AddHours(-4), Component = "pcie" },
                    new WheaRecord { Time = now.AddDays(-19).AddHours(-9), Component = "pcie" },
                ],
                RecentEvents =
                [
                    WheaEvent(now.AddDays(-6).AddHours(-4)),
                    WheaEvent(now.AddDays(-19).AddHours(-9)),
                ],
                // Since the graphics driver update four days ago: a freeze after a driver reset, then a blue screen.
                GpuDriverEvents = [Evening(3, 21, 46), Evening(1, 22, 11)],
                LiveKernelEvents = [new LiveKernelRecord { Time = Evening(3, 21, 46), Code = 0x141 }],
                Bugchecks = [new BugcheckRecord { Time = Evening(1, 22, 14), Code = 0x116 }],
                Shutdowns =
                [
                    new ShutdownRecord { RestartTime = Evening(3, 21, 55), LastActivity = Evening(3, 21, 47), Kind = ShutdownKind.ForcedOff },
                    new ShutdownRecord { RestartTime = Evening(1, 22, 14), LastActivity = Evening(1, 22, 12), Kind = ShutdownKind.Crash, BugcheckCode = 0x116 },
                ],
            },
            Changes = DemoChanges(now),
            Devices = DemoDevices(),
            DeviceLogs = new Devices.DeviceLogInfo { WifiDrops = 7, WifiFailures = 2 },
            PastEvents = DemoPastEvents(),
            Updates = DemoUpdates(now),
        };
        snapshot.Stability.Incidents = IncidentBuilder.Build(snapshot.Stability);
        snapshot.Stability.UnexpectedShutdowns = snapshot.Stability.Shutdowns.Count;
        snapshot.Stability.BlueScreens = snapshot.Stability.Incidents.Count(i => i.Kind == ShutdownKind.Crash);
        if (Laptop) snapshot.Battery = DemoBattery(now);
        // The sample laptop has another card than the sample desktop.
        snapshot.Updates.KnownGraphics = [snapshot.Gpus[0].Name];
        snapshot.Updates.Items[0].Name = snapshot.Gpus[0].Name;
        SnapshotCollector.ApplyLiveSensors(snapshot, sensors);
        ApplyMemory(snapshot.Memory);
        return snapshot;
    }

    /// <summary>
    /// The demo game's settings file: an Unreal Engine game in exclusive fullscreen at 1080p on the 1440p monitor,
    /// so the settings tips have something to say.
    /// </summary>
    public static Performance.GameSettings GameSettings() => new()
    {
        Source = "Unreal Engine",
        File = "GameUserSettings.ini",
        Width = 1920,
        Height = 1080,
        DisplayMode = Performance.GameDisplayMode.Fullscreen,
        VSync = false,
        FrameLimit = 0,
        RenderScale = 100,
        UpscalerKnown = true,
        Upscaler = "",
        Quality =
        [
            new("shadows", 3, "ue"), new("lighting", 2, "ue"), new("reflections", 2, "ue"), new("effects", 3, "ue"),
            new("postprocess", 2, "ue"), new("antialiasing", 3, "ue"), new("textures", 3, "ue"), new("viewdistance", 3, "ue"), new("foliage", 2, "ue"),
        ],
    };

    /// <summary>What the demo game's window looked like: fullscreen, which switched the monitor to 1080p.</summary>
    public static Performance.GameDisplay GameDisplay() => new()
    {
        WindowWidth = 1920, WindowHeight = 1080, CoversMonitor = true, HasTitleBar = false,
        ModeWidth = 1920, ModeHeight = 1080, RefreshHz = 165,
        NativeWidth = 2560, NativeHeight = 1440, MaxRefreshHz = 165,
    };

    /// <summary>A two-year-old battery that lost capacity a little too fast, charging at scan time.</summary>
    private BatteryInfo DemoBattery(DateTime now)
    {
        const uint design = 90_000;
        var history = new List<BatteryHistoryPoint>();
        var random = new Random(7);
        for (var day = 400; day >= 0; day -= 7)
        {
            var health = 100 - (400 - day) * 0.05 - random.NextDouble() * 1.5;
            history.Add(new BatteryHistoryPoint(now.Date.AddDays(-day), (uint)(design * health / 100), design, (uint)((400 - day) * 0.72) + 40));
        }
        var last = history[^1];
        return new BatteryInfo
        {
            Name = "DEMO-BAT 90Wh",
            Manufacturer = "Demo Battery Co.",
            Chemistry = "Li-ion",
            DesignCapacityMWh = design,
            FullChargeCapacityMWh = last.FullChargeMWh,
            CycleCount = last.Cycles,
            ChargePercent = (int)_batteryPercent,
            IsCharging = true,
            OnAcPower = true,
            RateW = 31.5,
            RemainingMWh = (uint)(last.FullChargeMWh * _batteryPercent / 100),
            History = history,
        };
    }

    /// <summary>
    /// The demo laptop's battery each second. While gaming its charger is too weak: plugged in, yet the battery drains.
    /// </summary>
    public BatteryState? BatteryNow()
    {
        if (!Laptop) return null;
        lock (_sync)
        {
            var rate = Gaming ? -17 - _random.NextDouble() * 6 : 28 + _random.NextDouble() * 4;
            _batteryPercent = Math.Clamp(_batteryPercent + rate / 3600 / 0.75, 5, 100);
            return new BatteryState(true, rate > 0, _batteryPercent, rate);
        }
    }

    /// <summary>Next set of live values: a gentle random walk so charts and tiles look alive.</summary>
    public SensorSnapshot NextSensors()
    {
        lock (_sync) return NextSensorsCore();
    }

    private SensorSnapshot NextSensorsCore()
    {
        _cpuLoad = Gaming ? Walk(_cpuLoad, 38, 6, 20, 70) : Walk(_cpuLoad, 12, 7, 2, 85, spike: 0.08);
        _cpuTemp = Walk(_cpuTemp, 44 + _cpuLoad * 0.25, 1.2, 38, 82);
        _gpuLoad = Gaming ? Walk(_gpuLoad, 86, 8, 60, 99) : Walk(_gpuLoad, 6, 4, 1, 70, spike: 0.05);
        _gpuTemp = Walk(_gpuTemp, 39 + _gpuLoad * 0.3, 0.8, 34, 78);
        _ramLoad = Walk(_ramLoad, Gaming ? 63 : 46, 1.5, 38, 80);
        _nvmeTemp = Walk(_nvmeTemp, 39, 0.4, 35, 50);
        for (var i = 0; i < _coreLoads.Length; i++)
            _coreLoads[i] = Math.Clamp(_cpuLoad + (_random.NextDouble() - 0.5) * 30, 0, 100);
        // One render thread is the bottleneck in many games: keep a core near 100% when the GPU is not maxed.
        if (Gaming && _gpuLoad < 88) _coreLoads[3] = 92 + _random.NextDouble() * 8;

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
            R("/amdcpu/0/power/0", "Package", "Power", Laptop ? 12 + _cpuLoad * 0.45 : 32 + _cpuLoad * 0.7),
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
                R("/lpc/nct6799d/voltage/3", "+3.3V", "Voltage", 3.31 + (_random.NextDouble() - 0.5) * 0.01),
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
                R("/gpu-nvidia/0/power/0", "GPU Package", "Power", Laptop ? 9 + _gpuLoad * 1.05 : 14 + _gpuLoad * 2.1),
                R("/gpu-nvidia/0/fan/1", "GPU Fan 1", "Fan", _gpuTemp > 55 ? 1100 + _gpuTemp * 8 : 0),
                R("/gpu-nvidia/0/smalldata/2", "GPU Memory Total", "SmallData", 12282),
                R("/gpu-nvidia/0/smalldata/1", "GPU Memory Used", "SmallData", (Gaming ? 8200 : 1450) + _gpuLoad * 12),
                R("/gpu-nvidia/0/smalldata/0", "GPU Memory Free", "SmallData", 12282 - (Gaming ? 8200 : 1450) - _gpuLoad * 12),
                R("/gpu-nvidia/0/load/4", "GPU Power", "Load", 12 + _gpuLoad * 0.85),
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
        if (Laptop)
        {
            var rate = Gaming ? -19.5 : 30.2;
            devices.Add(new("/battery/0", "DEMO-BAT 90Wh", "Battery", null,
            [
                R("/battery/0/level/0", "Charge Level", "Level", _batteryPercent),
                R("/battery/0/voltage/0", "Voltage", "Voltage", 16.4 + _batteryPercent * 0.012),
                R("/battery/0/power/0", rate > 0 ? "Charge Rate" : "Discharge Rate", "Power", Math.Abs(rate) + (_random.NextDouble() - 0.5)),
            ]));
        }
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
        RatedSpeedMTs = 6000,
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

    private static NetworkInfo DemoNetwork(DateTime now)
    {
        var adapter = new NetAdapterInfo
        {
            Id = "{00000000-0000-0000-0000-000000000000}",
            Name = "Ethernet",
            Description = "Realtek Gaming 2.5GbE Family Controller",
            LinkMbps = 2500,
            MaxMbps = 2500,
            FullDuplex = true,
            DriverVersion = "10.79.50.1003",
            DriverDate = now.AddMonths(-5).Date,
            Gateway = "192.168.1.1",
            DnsServers = ["192.168.1.1"],
            PowerSaving = false,
        };
        return new NetworkInfo { Primary = adapter, Physical = adapter, WifiPowerSaving = 0 };
    }

    private static SmartAttributeInfo Smart(byte id, string name, string kind, ulong raw, byte current = 0, byte worst = 0, byte threshold = 0) =>
        new() { Id = id, Name = name, Kind = kind, Raw = raw, Current = current, Worst = worst, Threshold = threshold };

    /// <summary>A time <paramref name="daysAgo"/> days back, in the evening when the demo PC is gamed on.</summary>
    /// <summary>What a look for updates could find: made up like the rest, and nothing goes online for it.</summary>
    private static Updates.UpdateInfo DemoUpdates(DateTime now) => new()
    {
        CheckedAt = now.AddHours(-3),
        KnownGraphics = ["NVIDIA GeForce RTX 4070 SUPER"],
        Items =
        [
            new() { Kind = Updates.UpdateKind.GraphicsDriver, Name = "NVIDIA GeForce RTX 4070 SUPER", Installed = "581.57", Latest = "582.16", Released = now.AddDays(-5).Date, Url = Updates.MakerPages.NvidiaDrivers },
            new() { Kind = Updates.UpdateKind.ChipsetDriver, Name = "AMD Chipset Software", Installed = "7.12.04.858", Latest = "8.09.28.1124", Url = Updates.MakerPages.AmdDrivers },
            new() { Kind = Updates.UpdateKind.WindowsDriver, Name = "Realtek - Net - 10.82.1205.2026", Url = Updates.MakerPages.OptionalUpdates },
            new() { Kind = Updates.UpdateKind.Runtime, Name = "Microsoft Windows Desktop Runtime - 8.0.29 (x64)", Installed = "8.0.29", Latest = "8.0.33" },
            new() { Kind = Updates.UpdateKind.Runtime, Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64)", Installed = "14.42.34433", Latest = "14.44.35208" },
            new() { Kind = Updates.UpdateKind.Program, Name = "Discord", Installed = "1.0.9212", Latest = "1.0.9231" },
            new() { Kind = Updates.UpdateKind.Program, Name = "Microsoft Visual Studio Code", Installed = "1.105.1", Latest = "1.143.1" },
            new() { Kind = Updates.UpdateKind.Program, Name = "VLC media player", Installed = "3.0.20", Latest = "3.0.23" },
        ],
    };

    private static DateTime Evening(int daysAgo, int hour, int minute) => DateTime.Today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute);

    private static List<Devices.ConnectedDevice> DemoDevices() =>
    [
        new() { Key = "demo-mouse", Name = "Logitech G502 X", Kind = Devices.DeviceKind.Mouse, Bus = Devices.DeviceBus.Usb, Speed = Devices.UsbSpeed.Full },
        new() { Key = "demo-keyboard", Name = "Keychron K2", Kind = Devices.DeviceKind.Keyboard, Bus = Devices.DeviceBus.Bluetooth, Connected = true, BatteryPercent = 14 },
        new() { Key = "demo-pad", Name = "Xbox Wireless Controller", Kind = Devices.DeviceKind.GameController, Bus = Devices.DeviceBus.Bluetooth, Connected = false, BatteryPercent = 72 },
        new() { Key = "demo-headset", Name = "Sony WH-1000XM5", Kind = Devices.DeviceKind.Headset, Bus = Devices.DeviceBus.Bluetooth, Connected = true, BatteryPercent = 58 },
        new() { Key = "demo-sound", Name = "Realtek(R) Audio", Manufacturer = "Realtek", Kind = Devices.DeviceKind.Audio, Bus = Devices.DeviceBus.Internal, BuiltIn = true },
        new() { Key = "demo-camera", Name = "Logitech C920", Kind = Devices.DeviceKind.Camera, Bus = Devices.DeviceBus.Usb, Speed = Devices.UsbSpeed.High },
        new() { Key = "demo-ssd", Name = "Samsung PSSD T7", Kind = Devices.DeviceKind.Storage, Bus = Devices.DeviceBus.Usb, Speed = Devices.UsbSpeed.High, SuperSpeedCapable = true },
        new() { Key = "demo-monitor", Name = "LG 27GP850", Kind = Devices.DeviceKind.Display, Bus = Devices.DeviceBus.Video, VideoOutput = "displayport" },
        new() { Key = "demo-net", Name = "Intel(R) Wi-Fi 6E AX211", Manufacturer = "Intel", Kind = Devices.DeviceKind.Network, Bus = Devices.DeviceBus.Internal, BuiltIn = true },
        new() { Key = "demo-bt", Name = "Intel(R) Wireless Bluetooth(R)", Manufacturer = "Intel", Kind = Devices.DeviceKind.BluetoothAdapter, Bus = Devices.DeviceBus.Usb, Speed = Devices.UsbSpeed.Full, BuiltIn = true },
        new() { Key = "demo-rgb", Name = "AURA LED Controller", Kind = Devices.DeviceKind.Other, Bus = Devices.DeviceBus.Usb, Speed = Devices.UsbSpeed.Full, BuiltIn = true },
    ];

    private static Collection.PastEvents DemoPastEvents()
    {
        var events = new Collection.PastEvents();
        var today = DateTime.Today;
        foreach (var day in new[] { 6, 13, 20 })
            events.AppEvents.Add(new AppEvent { Time = today.AddDays(-day).AddHours(20), App = "EldenRing.exe", Module = "nvwgf2umx.dll" });
        return events;
    }

    /// <summary>Two months of evenings, with games on most of them, as Windows' records would have them.</summary>
    public static List<Past.PastBlock> PastBlocks()
    {
        var list = new List<Past.PastBlock>();
        var random = new Random(23);
        var today = DateTime.Today;
        (string Exe, string Path, double Cpu)[] games =
        [
            ("Cyberpunk2077.exe", @"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", 46),
            ("FortniteClient-Win64-Shipping.exe", @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", 38),
            ("EldenRing.exe", @"C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game\EldenRing.exe", 41),
        ];
        for (var day = 59; day >= 0; day--)
        {
            var date = today.AddDays(-day);
            var start = date.AddHours(9);
            // The whole day: a browser and a few things beside it, in front less often once the evening's game is on.
            for (var hour = 0; hour < 14; hour++)
            {
                var end = start.AddHours(hour + 1);
                if (end > DateTime.Now) break;
                list.Add(new(end.AddHours(-1), end, "chrome.exe", null, 3600, hour < 10 ? 2700 : 300, 900, 9 * 3600, 400_000_000, 300_000_000));
                list.Add(new(end.AddHours(-1), end, "Code.exe", null, 3600, 700, 400, 4 * 3600, 90_000_000, 2_000_000));
                list.Add(new(end.AddHours(-1), end, "Discord.exe", null, 3600, 60, 20, 1.2 * 3600, 20_000_000, 10_000_000));
                list.Add(new(end.AddHours(-1), end, "Spotify.exe", null, 3600, 0, 0, 0.8 * 3600, 30_000_000, 60_000_000));
            }
            if (day % 7 == 3) continue;
            var (exe, path, cpu) = games[day % 3];
            var from = date.AddHours(19).AddMinutes(random.Next(0, 40));
            var length = 40 + random.Next(0, 110);
            var to = from.AddMinutes(length);
            if (to > DateTime.Now) continue;
            var seconds = length * 60.0;
            list.Add(new(from, to, exe, path, seconds, seconds * 0.97, seconds * 0.8, cpu * seconds, 4_000_000_000, 250_000_000));
            list.Add(new(from, to, "PhotoSyncHelper.exe", null, seconds, 0, 0, 9 * seconds, 5_000_000_000, 40_000_000));
            list.Add(new(from, to, "Discord.exe", null, seconds, 0, 0, 1.5 * seconds, 30_000_000, 40_000_000));
        }
        return list;
    }

    /// <summary>On in the day, asleep at night, off now and then.</summary>
    public static List<Past.PowerSpan> PowerSpans(int days)
    {
        var list = new List<Past.PowerSpan>();
        var today = DateTime.Today;
        for (var day = days - 1; day >= 0; day--)
        {
            var date = today.AddDays(-day);
            var morning = date.AddHours(8).AddMinutes(45);
            var night = date.AddDays(1).AddMinutes(40);
            if (morning > DateTime.Now) break;
            list.Add(new(morning, night < DateTime.Now ? night : DateTime.Now, Past.PowerState.On));
            if (night < DateTime.Now && day % 5 != 0) list.Add(new(night, date.AddDays(1).AddHours(8).AddMinutes(45), Past.PowerState.Asleep));
        }
        return list;
    }

    /// <summary>A week of Wi-Fi: fine most of the day, weak in the evenings.</summary>
    public static List<(DateTime Time, int Signal)> WifiSignals()
    {
        var list = new List<(DateTime, int)>();
        var random = new Random(11);
        var now = DateTime.Now;
        for (var minutes = 7 * 24 * 60; minutes > 0; minutes -= 5)
        {
            var time = now.AddMinutes(-minutes);
            var signal = time.Hour is >= 19 and <= 23 ? random.Next(28, 48) : random.Next(62, 84);
            list.Add((time, signal));
        }
        return list;
    }

    /// <summary>The demo mouse dropped out and came back eleven times this week.</summary>
    public static List<Devices.DeviceEvent> DeviceEvents()
    {
        var list = new List<Devices.DeviceEvent>();
        var now = DateTime.Now;
        for (var i = 0; i < 11; i++)
        {
            var gone = now.AddDays(-i * 0.55 - 0.1).AddMinutes(i * 7);
            list.Add(new(gone, "demo-mouse", "Logitech G502 X", false, false));
            list.Add(new(gone.AddSeconds(3), "demo-mouse", "Logitech G502 X", true, false));
        }
        return list;
    }

    /// <summary>
    /// The demo PC's recent changes. The graphics driver update four days ago is followed by both incidents above,
    /// so the timeline and the Stability page can show "the problems started after this change".
    /// </summary>
    private static List<SystemChange> DemoChanges(DateTime now)
    {
        SystemChange C(DateTime time, ChangeKind kind, string title, string? before, string? after, bool exact = true, string? setting = null, bool stability = false) => new()
        {
            Time = time,
            Kind = kind,
            Title = title,
            Before = before,
            After = after,
            Exact = exact,
            Setting = setting,
            CanAffectStability = stability,
            Key = $"demo:{kind}:{title}:{after}",
        };
        var day = now.Date;
        return
        [
            C(day.AddDays(-2).AddHours(10.5), ChangeKind.WindowsUpdate, "2026-10 Cumulative Update for Windows 11 Version 25H2 for x64-based Systems (KB5066835)", null, null, stability: true),
            C(Evening(4, 20, 5), ChangeKind.Driver, "NVIDIA GeForce RTX 4070 SUPER", "32.0.15.8094", "32.0.15.8157", stability: true),
            C(day.AddDays(-9).AddHours(18.2), ChangeKind.AppUpdated, "Discord", "1.0.9205", "1.0.9212", exact: false),
            C(day.AddDays(-12), ChangeKind.AppInstalled, "Steam", null, "2.10.91.91", exact: false),
            C(day.AddDays(-15).AddHours(11.3), ChangeKind.Driver, "Realtek High Definition Audio", "6.0.9600.1", "6.0.9733.1", stability: true),
            C(day.AddDays(-23).AddHours(19.6), ChangeKind.Bios, "BIOS", "American Megatrends Inc. 3101", "American Megatrends Inc. 3201", exact: false, stability: true),
            C(day.AddDays(-23).AddHours(19.6), ChangeKind.Setting, "memoryspeed", "4800", "6000", exact: false, setting: "memoryspeed", stability: true),
            C(day.AddDays(-30), ChangeKind.AppInstalled, "Microsoft Visual Studio Code", null, "1.105.1", exact: false),
            C(day.AddDays(-41).AddHours(9.1), ChangeKind.WindowsUpdate, "Windows 11, version 25H2", null, null, stability: true),
        ];
    }

    private static EventEntry WheaEvent(DateTime time) => new()
    {
        Time = time,
        Source = "Microsoft-Windows-WHEA-Logger",
        EventId = 17,
        Level = "Warning",
        Category = "whea-corrected",
    };
}
