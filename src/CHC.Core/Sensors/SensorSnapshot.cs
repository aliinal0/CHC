namespace CHC.Core.Sensors;

/// <summary>One sensor value (temperature, load, clock...) read from LibreHardwareMonitor.</summary>
public sealed record SensorReading(string Id, string Name, string Type, float? Value, float? Min, float? Max);

/// <summary>A hardware node (CPU, GPU, disk...) and its sensors.</summary>
public sealed record SensorDevice(string Id, string Name, string Kind, string? ParentId, IReadOnlyList<SensorReading> Sensors);

/// <summary>Immutable copy of all sensor values, safe to hand to the UI thread.</summary>
public sealed class SensorSnapshot
{
    public static SensorSnapshot Empty { get; } = new([], DateTime.MinValue);

    public SensorSnapshot(IReadOnlyList<SensorDevice> devices, DateTime time)
    {
        Devices = devices;
        Time = time;
        ById = devices.SelectMany(d => d.Sensors).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public DateTime Time { get; }
    public IReadOnlyList<SensorDevice> Devices { get; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, SensorReading> ById { get; }

    public SensorReading? Get(string? id) => id is not null && ById.TryGetValue(id, out var s) ? s : null;

    public IEnumerable<SensorDevice> OfKind(string kindPrefix) =>
        Devices.Where(d => d.Kind.StartsWith(kindPrefix, StringComparison.OrdinalIgnoreCase));

    public SensorDevice? Device(string? id) => id is null ? null : Devices.FirstOrDefault(d => d.Id == id);

    /// <summary>Devices whose id equals or is nested under <paramref name="id"/> (e.g. motherboard SuperIO chips).</summary>
    public IEnumerable<SensorDevice> DeviceTree(string? id) =>
        id is null ? [] : Devices.Where(d => d.Id == id || d.ParentId == id);
}

public static class SensorDeviceExtensions
{
    /// <summary>First sensor of the given type whose name matches one of the candidates (in priority order).</summary>
    public static SensorReading? Find(this SensorDevice? device, string type, params string[] names)
    {
        if (device is null) return null;
        foreach (var name in names)
        {
            var hit = device.Sensors.FirstOrDefault(s => s.Type == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Value if the sensor exists and reports something meaningful (LHM reports 0 for unreadable temperatures).</summary>
    public static double? Positive(this SensorReading? reading) =>
        reading?.Value is { } v && v > 0 && !float.IsNaN(v) ? v : null;
}
