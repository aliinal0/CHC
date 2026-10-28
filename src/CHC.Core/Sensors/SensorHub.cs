using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace CHC.Core.Sensors;

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/>. LHM is not thread safe,
/// so every call goes through one lock; callers get immutable snapshots back.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly object _sync = new();
    private Computer? _computer;

    public bool IsOpen { get; private set; }
    public string? OpenError { get; private set; }

    public void Open()
    {
        lock (_sync)
        {
            if (IsOpen) return;
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsBatteryEnabled = true,
                IsControllerEnabled = true,
                IsPsuEnabled = true,
            };
            try
            {
                _computer.Open();
                IsOpen = true;
            }
            catch (Exception ex)
            {
                // Sensors are a bonus: CHC still works with WMI data only.
                OpenError = ex.Message;
            }
        }
    }

    public SensorSnapshot Refresh()
    {
        lock (_sync)
        {
            if (!IsOpen || _computer is null) return SensorSnapshot.Empty;
            var devices = new List<SensorDevice>();
            foreach (var hardware in _computer.Hardware)
                Collect(hardware, null, devices);
            return new SensorSnapshot(devices, DateTime.Now);
        }
    }

    /// <summary>Gives the collector access to the SMART data LHM already reads (needs administrator rights).</summary>
    internal IReadOnlyList<DiskInfoToolkit.Storage> GetStorages()
    {
        lock (_sync)
        {
            if (!IsOpen || _computer is null) return [];
            return _computer.Hardware.OfType<StorageDevice>().Select(d => d.Storage).Where(s => s is not null).ToList();
        }
    }

    internal string? StorageHardwareId(int driveNumber)
    {
        lock (_sync)
        {
            return _computer?.Hardware.OfType<StorageDevice>()
                .FirstOrDefault(d => d.Storage?.DriveNumber == driveNumber)?.Identifier.ToString();
        }
    }

    public string GetFullReport()
    {
        lock (_sync)
        {
            if (!IsOpen || _computer is null) return "";
            try { return _computer.GetReport(); }
            catch (Exception ex) { return ex.Message; }
        }
    }

    private static void Collect(IHardware hardware, string? parentId, List<SensorDevice> into)
    {
        try { hardware.Update(); }
        catch { /* One misbehaving device must not break the whole refresh. */ }

        var sensors = hardware.Sensors
            .Select(s => new SensorReading(s.Identifier.ToString(), s.Name, s.SensorType.ToString(), Clean(s.Value), Clean(s.Min), Clean(s.Max)))
            .ToList();
        var id = hardware.Identifier.ToString();
        into.Add(new SensorDevice(id, hardware.Name, hardware.HardwareType.ToString(), parentId, sensors));

        foreach (var sub in hardware.SubHardware)
            Collect(sub, id, into);
    }

    private static float? Clean(float? value) => value is { } v && (float.IsNaN(v) || float.IsInfinity(v)) ? null : value;

    public void Dispose()
    {
        lock (_sync)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            IsOpen = false;
        }
    }
}
