namespace CHC.Core.Timeline;

/// <summary>What changed on the PC.</summary>
public enum ChangeKind
{
    WindowsUpdate,
    Driver,
    AppInstalled,
    AppUpdated,
    AppRemoved,
    Bios,
    Windows,
    HardwareAdded,
    HardwareRemoved,
    HardwareChanged,
    Setting,
}

/// <summary>
/// One change on the PC's timeline: a Windows update, a driver, a program, the BIOS, a part or a setting.
/// <see cref="Exact"/> is false when only the day is known (a program's install date) or when CHC noticed the change
/// at a scan rather than Windows logging it (a BIOS update, a part swapped, XMP turned off).
/// </summary>
public sealed class SystemChange
{
    public DateTime Time { get; set; }
    public ChangeKind Kind { get; set; }

    /// <summary>What it is: "NVIDIA GeForce RTX 4070 SUPER", "2026-10 Cumulative Update for Windows 11", "Discord".</summary>
    public string Title { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public bool Exact { get; set; } = true;

    /// <summary>Identifies the change, so the same one is kept once however often it is found.</summary>
    public string Key { get; set; } = "";

    /// <summary>For a <see cref="ChangeKind.Setting"/>: which setting ("xmp", "memoryintegrity", "rebar", "powerplan").</summary>
    public string? Setting { get; set; }

    /// <summary>A driver or update that touches the graphics card, the chipset, storage or the network: the likely suspects for crashes.</summary>
    public bool CanAffectStability { get; set; }
}
