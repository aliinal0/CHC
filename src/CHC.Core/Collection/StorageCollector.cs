using CHC.Core.Models;
using CHC.Core.Sensors;
using DiskInfoToolkit.Enums.Interop;

namespace CHC.Core.Collection;

/// <summary>
/// Disks come from two sources: the Windows Storage Management API (always available)
/// and SMART data read by LibreHardwareMonitor/DiskInfoToolkit (administrator only).
/// </summary>
internal static class StorageCollector
{
    public static (List<DiskInfo> Disks, List<VolumeInfo> Volumes) Collect(SensorHub hub)
    {
        var disks = new Dictionary<int, DiskInfo>();

        foreach (var d in Wmi.Query("SELECT DeviceId, FriendlyName, SerialNumber, FirmwareVersion, Size, MediaType, BusType, HealthStatus, OperationalStatus, SpindleSpeed FROM MSFT_PhysicalDisk", Wmi.Storage))
        {
            if (!int.TryParse(d.Str("DeviceId"), out var index)) continue;
            var bus = d.I32("BusType");
            disks[index] = new DiskInfo
            {
                Index = index,
                Model = d.Str("FriendlyName"),
                SerialNumber = d.Str("SerialNumber").TrimEnd('.'),
                Firmware = d.Str("FirmwareVersion"),
                SizeBytes = d.U64("Size"),
                BusType = BusName(bus),
                IsRemovable = bus is 7 or 12 or 13,
                Media = bus == 17 ? DiskMedia.Nvme : d.I32("MediaType") switch { 3 => DiskMedia.Hdd, 4 => DiskMedia.Ssd, _ => DiskMedia.Unknown },
                WindowsHealth = d.I32("HealthStatus") switch { 0 => DiskHealth.Good, 1 => DiskHealth.Caution, 2 => DiskHealth.Bad, _ => DiskHealth.Unknown },
                OperationalStatus = string.Join(", ", d.IntArray("OperationalStatus").Select(OperationalName)),
            };
        }

        // Windows' own reliability counters (administrator only).
        foreach (var r in Wmi.Query("SELECT DeviceId, Temperature, Wear, PowerOnHours, ReadErrorsUncorrected FROM MSFT_StorageReliabilityCounter", Wmi.Storage))
        {
            if (!int.TryParse(r.Str("DeviceId"), out var index) || !disks.TryGetValue(index, out var disk)) continue;
            if (r.I32("Temperature") is > 0 and var t) disk.TemperatureC = t;
            if (r.I32("Wear") is > 0 and <= 100 and var wear) disk.LifePercent = 100 - wear;
            if (r.U64("PowerOnHours") is > 0 and var hours) disk.PowerOnHours = hours;
            disk.ReadErrorsUncorrected = r.U64("ReadErrorsUncorrected");
        }

        // SMART through DiskInfoToolkit (the same engine as CrystalDiskInfo).
        foreach (var storage in hub.GetStorages())
        {
            try
            {
                if (!disks.TryGetValue(storage.DriveNumber, out var disk))
                {
                    disk = new DiskInfo { Index = storage.DriveNumber, Model = storage.Model?.Trim() ?? "", SizeBytes = storage.TotalSize };
                    disks[storage.DriveNumber] = disk;
                }
                ApplySmart(disk, storage);
                disk.SensorHardwareId = hub.StorageHardwareId(storage.DriveNumber);
            }
            catch
            {
                // A single unreadable disk (USB bridge, RAID...) should not hide the others.
            }
        }

        var volumes = CollectVolumes();
        MapVolumes(disks, volumes);
        return (disks.Values.OrderBy(d => d.Index).ToList(), volumes);
    }

    private static void ApplySmart(DiskInfo disk, DiskInfoToolkit.Storage storage)
    {
        if (disk.Model.Length == 0) disk.Model = storage.Model?.Trim() ?? "";
        if (disk.Firmware.Length == 0) disk.Firmware = storage.Firmware?.Trim() ?? "";
        if (disk.SerialNumber.Length == 0) disk.SerialNumber = storage.SerialNumber?.Trim() ?? "";
        if (disk.Media == DiskMedia.Unknown) disk.Media = storage.IsNVMe ? DiskMedia.Nvme : storage.IsSSD ? DiskMedia.Ssd : DiskMedia.Hdd;
        disk.TrimSupported = storage.IsTrimSupported;

        var smart = storage.Smart;
        if (smart is null) return;
        disk.SmartAvailable = smart.SmartAttributes is { Count: > 0 } || smart.DiskStatus != DiskStatus.Unknown;
        disk.SmartHealth = smart.DiskStatus switch
        {
            DiskStatus.Good => DiskHealth.Good,
            DiskStatus.Caution => DiskHealth.Caution,
            DiskStatus.Bad => DiskHealth.Bad,
            _ => DiskHealth.Unknown,
        };
        if (smart.Temperature is > 0 and < 150) disk.TemperatureC = smart.Temperature;
        if (smart.TemperatureWarning is > 0) disk.TemperatureWarningC = smart.TemperatureWarning;
        if (smart.TemperatureCritical is > 0) disk.TemperatureCriticalC = smart.TemperatureCritical;
        if (smart.Life is >= 0 and <= 100) disk.LifePercent = smart.Life;
        var hours = smart.DetectedPowerOnHours > 0 ? smart.DetectedPowerOnHours : smart.MeasuredPowerOnHours;
        if (hours > 0) disk.PowerOnHours = hours;
        if (smart.PowerOnCount > 0) disk.PowerOnCount = smart.PowerOnCount;
        if (smart.HostWrites is > 0) disk.HostWritesGB = smart.HostWrites;
        if (smart.HostReads is > 0) disk.HostReadsGB = smart.HostReads;

        disk.SmartAttributes = (smart.SmartAttributes ?? [])
            .Select(a => new SmartAttributeInfo
            {
                Id = a.Info?.ID ?? a.Attribute.ID,
                Name = a.Info?.Name ?? "",
                Kind = a.Info?.Type.ToString() ?? "",
                Current = a.Attribute.CurrentValue,
                Worst = a.Attribute.WorstValue,
                Threshold = a.Attribute.Threshold,
                Raw = a.Attribute.RawValueULong,
            })
            .ToList();
    }

    private static List<VolumeInfo> CollectVolumes()
    {
        var systemLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var list = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var letter = drive.Name.TrimEnd('\\');
                list.Add(new VolumeInfo
                {
                    Letter = letter,
                    Label = drive.VolumeLabel,
                    FileSystem = drive.DriveFormat,
                    TotalBytes = (ulong)drive.TotalSize,
                    FreeBytes = (ulong)drive.TotalFreeSpace,
                    IsSystem = letter.Equals(systemLetter, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    private static void MapVolumes(Dictionary<int, DiskInfo> disks, List<VolumeInfo> volumes)
    {
        foreach (var p in Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", Wmi.Storage))
        {
            if (p.Char("DriveLetter") is not { } driveLetter || !disks.TryGetValue(p.I32("DiskNumber"), out var disk)) continue;
            var letter = $"{char.ToUpperInvariant(driveLetter)}:";
            disk.DriveLetters.Add(letter);
            if (volumes.Any(v => v.IsSystem && v.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase)))
                disk.IsSystemDisk = true;
        }
        foreach (var disk in disks.Values) disk.DriveLetters.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private static string BusName(int bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "",
    };

    private static string OperationalName(int code) => code switch
    {
        2 => "OK",
        3 => "Degraded",
        5 => "Predictive Failure",
        6 => "Error",
        10 => "Stopped",
        13 => "Lost Communication",
        0xD00D => "In Maintenance",
        0xD010 => "Transient Error",
        0xD012 => "Failed Media",
        _ => code.ToString(),
    };
}
