using CHC.Core.Sensors;

namespace CHC.Core.Models;

/// <summary>Everything CHC knows about the computer at one point in time.</summary>
public sealed class SystemSnapshot
{
    public DateTime TakenAt { get; init; } = DateTime.Now;
    public bool IsElevated { get; init; }
    public string AppVersion { get; init; } = "";

    public OsInfo Os { get; set; } = new();
    public BoardInfo Board { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public MemoryInfo Memory { get; set; } = new();
    public List<DiskInfo> Disks { get; set; } = [];
    public List<VolumeInfo> Volumes { get; set; } = [];
    public List<GpuInfo> Gpus { get; set; } = [];
    public BatteryInfo? Battery { get; set; }
    public StabilityInfo Stability { get; set; } = new();
    public List<ProblemDevice> ProblemDevices { get; set; } = [];
    public SensorSnapshot Sensors { get; set; } = SensorSnapshot.Empty;
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
}

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
}

public sealed class StabilityInfo
{
    public const int PeriodDays = 30;
    public bool Available { get; set; }
    public int UnexpectedShutdowns { get; set; }
    public int BlueScreens { get; set; }
    public int WheaErrors { get; set; }
    public int WheaCorrected { get; set; }
    public int DiskErrors { get; set; }
    public int FileSystemErrors { get; set; }
    public List<EventEntry> RecentEvents { get; set; } = [];
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
