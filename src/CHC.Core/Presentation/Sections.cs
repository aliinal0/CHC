using CHC.Core.Health;

namespace CHC.Core.Presentation;

/// <summary>
/// Display model shared by the app and the exported report. Each component goes from
/// plain-language (summary, highlights, meters) to technical (spec groups, tables, raw sensors).
/// </summary>
public sealed class ComponentSection
{
    public required ComponentKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required Severity Severity { get; init; }
    public required int Score { get; init; }
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public List<DeviceSection> Devices { get; } = [];
}

public sealed class DeviceSection
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public Severity? Severity { get; init; }
    public List<Highlight> Highlights { get; } = [];
    public List<Meter> Meters { get; } = [];
    public List<SpecGroup> Specs { get; } = [];
    public List<TableBlock> Tables { get; } = [];

    /// <summary>LibreHardwareMonitor hardware ids whose sensors belong to this device.</summary>
    public List<string> SensorDeviceIds { get; } = [];
}

/// <summary>A headline value. When <see cref="SensorId"/> is set the app keeps it live.</summary>
public sealed record Highlight(string Label, string Value, string? SensorId = null, string? SensorType = null);

public sealed record Meter(string Label, double Percent, string Caption, Severity Severity, string? SensorId = null);

public sealed record SpecRow(string Key, string Value);

public sealed record SpecGroup(string Title, IReadOnlyList<SpecRow> Rows);

public sealed record TableBlock(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);
