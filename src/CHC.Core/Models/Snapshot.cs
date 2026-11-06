using CHC.Core.Network;
using CHC.Core.Performance;
using CHC.Core.Sensors;
using CHC.Core.Updates;

namespace CHC.Core.Models;

/// <summary>Everything CHC knows about the computer at one point in time.</summary>
public sealed class SystemSnapshot
{
    public DateTime TakenAt { get; init; } = DateTime.Now;
    public bool IsElevated { get; init; }
    public string AppVersion { get; init; } = "";

    public OsInfo Os { get; set; } = new();

    /// <summary>What the last look for updates found. Written to reports on its own, as the program list is optional there.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public UpdateInfo Updates { get; set; } = new();

    public BoardInfo Board { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public MemoryInfo Memory { get; set; } = new();
    public List<DiskInfo> Disks { get; set; } = [];
    public List<VolumeInfo> Volumes { get; set; } = [];
    public List<GpuInfo> Gpus { get; set; } = [];
    public BatteryInfo? Battery { get; set; }
    public StabilityInfo Stability { get; set; } = new();
    public PowerInfo Power { get; set; } = new();
    public GamingInfo Gaming { get; set; } = new();
    public NetworkInfo Network { get; set; } = new();

    /// <summary>The last game session measured in the app, if any (included in reports).</summary>
    public PerfSession? Performance { get; set; }

    /// <summary>The last network test run in the app, if any (included in reports).</summary>
    public NetSession? NetworkTest { get; set; }
    public List<ProblemDevice> ProblemDevices { get; set; } = [];

    /// <summary>What is connected to the PC: by cable, wirelessly, and its monitors.</summary>
    public List<Devices.ConnectedDevice> Devices { get; set; } = [];

    /// <summary>Devices that kept dropping out lately, from the history.</summary>
    public List<Devices.DeviceDrops> DeviceDrops { get; set; } = [];

    /// <summary>What Windows logged about devices.</summary>
    public Devices.DeviceLogInfo DeviceLogs { get; set; } = new();

    /// <summary>Crashes and low memory from Windows' logs, further back than the stability findings look.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Collection.PastEvents PastEvents { get; set; } = new();

    /// <summary>The Wi-Fi signal lately, from the history; null without Wi-Fi.</summary>
    public Devices.WifiHealth? Wifi { get; set; }
    public SensorSnapshot Sensors { get; set; } = SensorSnapshot.Empty;

    /// <summary>The power supply: its wattage (from the user or the supply itself), rails and the recent peak draw.</summary>
    public Power.PowerSupplyInfo? PowerSupply { get; set; }

    /// <summary>
    /// What Windows logged as changed in the last 90 days (updates, drivers, program installs), to explain problems
    /// that began after one. Kept out of exports: the list of installed programs is nobody else's business.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<Timeline.SystemChange> Changes { get; set; } = [];
}

public sealed class OsInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Build { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public string Architecture { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public DateTime? LastBoot { get; set; }
    public TimeSpan? Uptime => LastBoot is { } boot ? DateTime.Now - boot : null;

    /// <summary>"Core" (Home), "Professional", "Enterprise", "EnterpriseS" (LTSC)...</summary>
    public string EditionId { get; set; } = "";

    /// <summary>When this Windows version stops (or stopped) getting security updates; null if unknown.</summary>
    public DateTime? SupportEnds { get; set; }

    /// <summary>Installed updates are waiting for a restart.</summary>
    public bool? RestartPending { get; set; }

    /// <summary>TRIM is turned off for NTFS (fsutil DisableDeleteNotify = 1).</summary>
    public bool? TrimDisabled { get; set; }

    /// <summary>Programs that start with Windows (Run keys and Startup folders, minus the ones turned off in Task Manager).</summary>
    public List<string> StartupApps { get; set; } = [];
}

public sealed class BoardInfo
{
    public string SystemManufacturer { get; set; } = "";
    public string SystemModel { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string BiosVendor { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public DateTime? BiosDate { get; set; }
    public bool? IsUefi { get; set; }
    public bool? SecureBoot { get; set; }
    public bool? TpmPresent { get; set; }
    public string TpmVersion { get; set; } = "";
    public bool IsLaptop { get; set; }
}

public sealed class CpuInfo
{
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string Socket { get; set; } = "";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int BaseClockMHz { get; set; }
    public int CurrentClockMHz { get; set; }
    public int L2CacheKB { get; set; }
    public int L3CacheKB { get; set; }
    public bool? VirtualizationEnabled { get; set; }
    public double? LoadPercent { get; set; }
    public double? TemperatureC { get; set; }
    public double? PackagePowerW { get; set; }
    public string? SensorHardwareId { get; set; }
}

public sealed class MemoryInfo
{
    public ulong TotalBytes { get; set; }
    public ulong AvailableBytes { get; set; }
    public ulong UsedBytes => TotalBytes > AvailableBytes ? TotalBytes - AvailableBytes : 0;
    public double LoadPercent { get; set; }
    public ulong CommitTotalBytes { get; set; }
    public ulong CommitLimitBytes { get; set; }
    public int SlotCount { get; set; }
    public ulong MaxCapacityBytes { get; set; }
    public List<MemoryModule> Modules { get; set; } = [];

    /// <summary>Kernel memory used by drivers; a very large non-paged pool means a driver leaks memory.</summary>
    public ulong NonPagedPoolBytes { get; set; }
    public ulong PagedPoolBytes { get; set; }

    /// <summary>Programs using the most memory at scan time (all processes of a program added together).</summary>
    public List<ProcessMemory> TopProcesses { get; set; } = [];
}

public sealed record ProcessMemory(string Name, ulong Bytes, int Processes);

public sealed class MemoryModule
{
    public string Slot { get; set; } = "";
    public string Bank { get; set; } = "";
    public ulong CapacityBytes { get; set; }
    public int SpeedMTs { get; set; }
    public int ConfiguredSpeedMTs { get; set; }
    public string Manufacturer { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Type { get; set; } = "";
    public string FormFactor { get; set; } = "";
    public int VoltageMv { get; set; }

    /// <summary>Speed the module is sold for (its XMP/EXPO profile), read from the part number; null if it doesn't say.</summary>
    public int? RatedSpeedMTs { get; set; }
}

public enum DiskMedia { Unknown, Hdd, Ssd, Nvme }

public enum DiskHealth { Unknown, Good, Caution, Bad }

public sealed class DiskInfo
{
    public int Index { get; set; }
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string BusType { get; set; } = "";
    public DiskMedia Media { get; set; }
    public ulong SizeBytes { get; set; }
    public bool IsSystemDisk { get; set; }
    public bool IsRemovable { get; set; }
    public bool? TrimSupported { get; set; }

    /// <summary>Health reported by Windows (Storage Management API).</summary>
    public DiskHealth WindowsHealth { get; set; }
    public string OperationalStatus { get; set; } = "";

    /// <summary>Health calculated from SMART (CrystalDiskInfo rules, via DiskInfoToolkit).</summary>
    public DiskHealth SmartHealth { get; set; }
    public bool SmartAvailable { get; set; }
    public int? TemperatureC { get; set; }
    public int? TemperatureWarningC { get; set; }
    public int? TemperatureCriticalC { get; set; }
    public int? LifePercent { get; set; }
    public ulong? PowerOnHours { get; set; }
    public ulong? PowerOnCount { get; set; }
    public ulong? HostWritesGB { get; set; }
    public ulong? HostReadsGB { get; set; }
    public ulong? ReadErrorsUncorrected { get; set; }
    public List<SmartAttributeInfo> SmartAttributes { get; set; } = [];
    public List<string> DriveLetters { get; set; } = [];
    public string? SensorHardwareId { get; set; }
}

public sealed class SmartAttributeInfo
{
    public byte Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public byte Current { get; set; }
    public byte Worst { get; set; }
    public byte Threshold { get; set; }
    public ulong Raw { get; set; }
}

public sealed class VolumeInfo
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public ulong TotalBytes { get; set; }
    public ulong FreeBytes { get; set; }
    public bool IsSystem { get; set; }
    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * (TotalBytes - FreeBytes) / TotalBytes;
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public ulong VramBytes { get; set; }
    public string Resolution { get; set; } = "";
    public int RefreshRateHz { get; set; }
    public string Status { get; set; } = "";
    public string PnpDeviceId { get; set; } = "";
    public bool IsVirtual { get; set; }

    /// <summary>Windows' fallback driver ("Microsoft Basic Display Adapter") is in use: no real graphics driver.</summary>
    public bool UsesBasicDriver { get; set; }

    /// <summary>PCI Express link: lanes and generation now, and the most the card supports.</summary>
    public int? PcieWidth { get; set; }
    public int? PcieMaxWidth { get; set; }
    public int? PcieGen { get; set; }
    public int? PcieMaxGen { get; set; }

    /// <summary>Largest memory window the card was given. With Resizable BAR it covers all of the VRAM, otherwise 256 MB.</summary>
    public ulong? LargestBarBytes { get; set; }
    public bool? ResizableBar => LargestBarBytes is { } bar && !IsIntegrated ? bar >= 1024UL * 1024 * 1024 : null;

    /// <summary>Graphics built into the processor (shares system memory) rather than a separate card.</summary>
    public bool IsIntegrated
    {
        get
        {
            var name = Name.ToUpperInvariant();
            // Intel: everything but the Arc A/B-series cards. AMD: integrated names end in "Graphics" (Radeon Graphics, Vega 8 Graphics, 780M Graphics).
            if (PnpDeviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || name.StartsWith("INTEL", StringComparison.Ordinal))
                return !System.Text.RegularExpressions.Regex.IsMatch(name, @"\bARC\b.*\b[AB]\d{3}");
            if (PnpDeviceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AMD", StringComparison.Ordinal))
                return name.Contains("GRAPHICS", StringComparison.Ordinal);
            return false;
        }
    }

    public double? TemperatureC { get; set; }
    public double? HotSpotC { get; set; }
    public double? MemoryJunctionC { get; set; }
    public double? LoadPercent { get; set; }
    public double? PowerW { get; set; }
    public string? SensorHardwareId { get; set; }
}

public sealed class BatteryInfo
{
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Chemistry { get; set; } = "";
    public uint? DesignCapacityMWh { get; set; }
    public uint? FullChargeCapacityMWh { get; set; }
    public uint? CycleCount { get; set; }
    public int? ChargePercent { get; set; }
    public bool? IsCharging { get; set; }
    public bool? OnAcPower { get; set; }
    public double? HealthPercent =>
        DesignCapacityMWh is > 0 && FullChargeCapacityMWh is { } full
            ? Math.Min(100.0, 100.0 * full / DesignCapacityMWh.Value)
            : null;

    /// <summary>Power going into the battery (positive, charging) or coming out of it (negative) at scan time, in watts.</summary>
    public double? RateW { get; set; }
    public uint? RemainingMWh { get; set; }

    /// <summary>Plugged in, yet the battery supplies power: the charger gives less than the laptop uses.</summary>
    public bool DrainingOnAc => OnAcPower == true && RateW <= -2;

    /// <summary>Time left on battery, and how long a full charge lasts, at the power drawn at scan time.</summary>
    public TimeSpan? Runtime => RateW is < -0.5 && OnAcPower != true && RemainingMWh is > 0 ? TimeSpan.FromHours(RemainingMWh.Value / 1000.0 / -RateW.Value) : null;
    public TimeSpan? FullRuntime => RateW is < -0.5 && OnAcPower != true && FullChargeCapacityMWh is > 0 ? TimeSpan.FromHours(FullChargeCapacityMWh.Value / 1000.0 / -RateW.Value) : null;

    /// <summary>Capacity over time from Windows' battery report, oldest first (only the battery fitted now).</summary>
    public List<BatteryHistoryPoint> History { get; set; } = [];
}

public sealed record BatteryHistoryPoint(DateTime Date, uint FullChargeMWh, uint DesignMWh, uint? Cycles)
{
    public double HealthPercent => DesignMWh == 0 ? 0 : Math.Min(100.0, 100.0 * FullChargeMWh / DesignMWh);
}

/// <summary>The battery right now: read every second during a game session.</summary>
/// <param name="RateW">Power into (+) or out of (−) the battery in watts, null if the battery doesn't report it.</param>
public sealed record BatteryState(bool OnAc, bool Charging, double? Percent, double? RateW);

public sealed class StabilityInfo
{
    public const int PeriodDays = 30;
    public bool Available { get; set; }

    // Totals, for the summary tiles.
    public int UnexpectedShutdowns { get; set; }
    public int BlueScreens { get; set; }
    public int WheaErrors { get; set; }
    public int WheaCorrected { get; set; }
    public int DiskErrors { get; set; }
    public int FileSystemErrors { get; set; }
    public List<EventEntry> RecentEvents { get; set; } = [];

    // Details used to work out what went wrong.
    public List<ShutdownRecord> Shutdowns { get; set; } = [];
    public List<BugcheckRecord> Bugchecks { get; set; } = [];
    public List<DateTime> GpuDriverEvents { get; set; } = [];
    public List<LiveKernelRecord> LiveKernelEvents { get; set; } = [];
    public List<WheaRecord> Whea { get; set; } = [];
    public List<DateTime> StorageResets { get; set; } = [];
    public List<DateTime> SleepEntries { get; set; } = [];
    public List<DateTime> LowMemoryEvents { get; set; } = [];
    public List<AppProblem> AppProblems { get; set; } = [];
    public List<AppEvent> AppEvents { get; set; } = [];

    /// <summary>Windows shut down (or hibernated) because a temperature sensor reached its critical limit.</summary>
    public List<DateTime> ThermalShutdowns { get; set; } = [];

    /// <summary>Network adapters that stopped responding and had to be reset (one entry per burst).</summary>
    public List<NetworkReset> NetworkResets { get; set; } = [];

    /// <summary>Windows and driver updates that failed to install (Store app updates are left out).</summary>
    public List<UpdateFailure> UpdateFailures { get; set; } = [];

    /// <summary>"Name resolution timed out": none of the DNS servers answered.</summary>
    public List<DateTime> DnsTimeouts { get; set; } = [];

    /// <summary>Result of the last Windows Memory Diagnostic run (null if it never ran in the last year).</summary>
    public bool? MemoryTestFoundErrors { get; set; }
    public DateTime? MemoryTestDate { get; set; }

    /// <summary>Freezes, crashes and sudden shutdowns with their most likely cause (built by IncidentBuilder).</summary>
    public List<Incident> Incidents { get; set; } = [];

    public int GpuDriverResets => GpuDriverEvents.Count;
    public int Freezes => Shutdowns.Count(s => s.Kind == ShutdownKind.ForcedOff);
    public int SleepFailures => Shutdowns.Count(s => s.Kind == ShutdownKind.Sleep);
    public int PowerLosses => Shutdowns.Count(s => s.Kind == ShutdownKind.PowerLoss);
    public int AppCrashes => AppProblems.Sum(a => a.Crashes);
}

/// <summary>How a Kernel-Power 41 shutdown happened, from the data Windows stores with the event.</summary>
public enum ShutdownKind { PowerLoss, ForcedOff, Sleep, Crash, Thermal }

public sealed class ShutdownRecord
{
    /// <summary>When Windows started again and logged the problem.</summary>
    public DateTime RestartTime { get; set; }

    /// <summary>The last event written before the shutdown: roughly when it happened.</summary>
    public DateTime? LastActivity { get; set; }

    public ShutdownKind Kind { get; set; }
    public uint BugcheckCode { get; set; }
}

public sealed class NetworkReset
{
    public DateTime Time { get; set; }
    public string Adapter { get; set; } = "";

    /// <summary>Resets in this burst (a hung adapter is often reset many times in a row).</summary>
    public int Resets { get; set; }
}

public sealed class UpdateFailure
{
    public DateTime Time { get; set; }
    public string Title { get; set; } = "";
    public string ErrorCode { get; set; } = "";
}

public sealed class BugcheckRecord
{
    public DateTime Time { get; set; }
    public uint Code { get; set; }
}

public sealed class LiveKernelRecord
{
    public DateTime Time { get; set; }
    public uint Code { get; set; }
}

public sealed class WheaRecord
{
    public DateTime Time { get; set; }
    public bool Fatal { get; set; }

    /// <summary>"cpu", "memory", "pcie" or "other".</summary>
    public string Component { get; set; } = "other";
}

/// <summary>A program crash or hang from the Application log.</summary>
public sealed class AppEvent
{
    public DateTime Time { get; set; }
    public string App { get; set; } = "";
    public string Module { get; set; } = "";
    public bool IsHang { get; set; }
}

public sealed class AppProblem
{
    public string App { get; set; } = "";
    public int Crashes { get; set; }
    public int Hangs { get; set; }
    public DateTime Last { get; set; }

    /// <summary>Module that crashed most often (e.g. the graphics driver DLL).</summary>
    public string Module { get; set; } = "";
}

public sealed class Incident
{
    /// <summary>Best estimate of when it happened (last activity, or the restart time).</summary>
    public DateTime Time { get; set; }
    public DateTime RestartTime { get; set; }
    public ShutdownKind Kind { get; set; }
    public uint BugcheckCode { get; set; }

    /// <summary>Cause category: heat, gpu, memory, cpu, hardware, storage, driver, sleep, lowmemory, system, silent or unknown.</summary>
    public string Cause { get; set; } = "unknown";
    public List<Clue> Clues { get; set; } = [];

    /// <summary>What the PC was doing just before, if the history was recording then.</summary>
    public IncidentMoment? Moment { get; set; }
}

/// <summary>The last recorded minute before a freeze or crash: the program in front, the load and the temperatures.</summary>
public sealed record IncidentMoment(string? App, string? Exe, bool Fullscreen, double? CpuLoad, double? GpuLoad, double? CpuTemp, double? GpuTemp, double? RamLoad);

/// <summary>Something that happened shortly before an incident and hints at its cause.</summary>
public sealed class Clue
{
    /// <summary>gpu, livekernel, whea, storage, appcrash, apphang, lowmem, sleep, thermal or network.</summary>
    public string Kind { get; set; } = "";
    public DateTime Time { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>Cause category this clue points to (gpu, cpu, memory, sleep...), empty if none.</summary>
    public string Hint { get; set; } = "";
}

/// <summary>Power plan settings that are known to cause freezes when the PC sits idle.</summary>
public sealed class PowerInfo
{
    public Guid? PlanGuid { get; set; }
    public string PlanName { get; set; } = "";
    public uint? SleepAfterSeconds { get; set; }
    public uint? HibernateAfterSeconds { get; set; }
    public uint? DiskOffAfterSeconds { get; set; }
    public bool? HybridSleep { get; set; }
    public bool? FastStartup { get; set; }
    public bool? HibernationAvailable { get; set; }
    public bool? ModernStandby { get; set; }

    /// <summary>PCI Express Link State Power Management: 0 off, 1 moderate, 2 maximum savings.</summary>
    public uint? PcieLinkPowerManagement { get; set; }
    public bool? UsbSelectiveSuspend { get; set; }

    /// <summary>"Maximum processor state" in percent; below 100 caps the processor's speed.</summary>
    public uint? ProcessorMaxState { get; set; }

    /// <summary>"Processor performance boost mode": 0 turns boost (Turbo Boost / Precision Boost) off.</summary>
    public uint? CpuBoostMode { get; set; }
}

public sealed class EventEntry
{
    public DateTime Time { get; set; }
    public string Source { get; set; } = "";
    public int EventId { get; set; }
    public string Level { get; set; } = "";
    public string Category { get; set; } = "";
}

public sealed class ProblemDevice
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public int ErrorCode { get; set; }
    public string DeviceId { get; set; } = "";
}

/// <summary>Display and Windows settings that affect games.</summary>
public sealed class GamingInfo
{
    /// <summary>The program files Windows itself recognised as games.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> KnownGames { get; set; } = [];

    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }

    /// <summary>Highest refresh rate the main display offers at the current resolution.</summary>
    public int MaxRefreshHz { get; set; }
    public bool? GameMode { get; set; }
    public bool? HardwareGpuScheduling { get; set; }

    /// <summary>Memory Integrity (HVCI, core isolation) is running.</summary>
    public bool? MemoryIntegrity { get; set; }

    /// <summary>Xbox Game Bar keeps recording the last minutes of gameplay ("Record what happened").</summary>
    public bool? BackgroundRecording { get; set; }

    /// <summary>The main display's own (native) resolution; 0 when unknown.</summary>
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }

    /// <summary>"Enhance pointer precision" (mouse acceleration) is on.</summary>
    public bool? MouseAcceleration { get; set; }

    /// <summary>Windows 11's "Optimizations for windowed games"; null while it is at the Windows default.</summary>
    public bool? WindowedOptimizations { get; set; }

    /// <summary>Games set to "Power saving" in Settings → Display → Graphics (they run on the integrated graphics).</summary>
    public List<string> PowerSavingGames { get; set; } = [];

    /// <summary>AMD's 3D V-Cache Performance Optimizer driver (from the chipset software) is installed.</summary>
    public bool? VCacheDriver { get; set; }
}
