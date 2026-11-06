using System.Text.Json.Serialization;

namespace CHC.Core.Devices;

/// <summary>What a device is, for grouping it.</summary>
public enum DeviceKind
{
    Mouse,
    Keyboard,
    GameController,
    Headset,
    Audio,
    Camera,
    Storage,
    Display,
    Network,
    BluetoothAdapter,
    Printer,
    Phone,
    Other,
}

/// <summary>How a device is connected.</summary>
public enum DeviceBus
{
    Usb,
    Bluetooth,
    Video,
    Internal,
}

/// <summary>The speed a USB device runs at.</summary>
public enum UsbSpeed
{
    Low,
    Full,
    High,
    Super,
    SuperPlus,
}

/// <summary>A device connected to the PC: by cable or wirelessly, or a monitor.</summary>
public sealed class ConnectedDevice
{
    /// <summary>Windows' id for the device, which can hold a serial number: for telling devices apart, never for reports.</summary>
    [JsonIgnore]
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public DeviceKind Kind { get; set; } = DeviceKind.Other;
    public DeviceBus Bus { get; set; }

    /// <summary>On a port inside the case (a motherboard header): LED controllers and built-in radios, nothing the user plugged in.</summary>
    public bool BuiltIn { get; set; }

    public UsbSpeed? Speed { get; set; }

    /// <summary>The device could run at USB 3 speed or faster, whatever it runs at now.</summary>
    public bool? SuperSpeedCapable { get; set; }

    /// <summary>What Windows reports for its USB port when it isn't working: 2 enumeration failed, 3 general failure, 4 overcurrent, 5 not enough power, 6 bandwidth...</summary>
    public int UsbPortStatus { get; set; }

    /// <summary>Device Manager's error code for the device or part of it (0 when all is well).</summary>
    public int ProblemCode { get; set; }

    /// <summary>A Bluetooth device's state; null when Windows doesn't say.</summary>
    public bool? Connected { get; set; }
    public int? BatteryPercent { get; set; }

    /// <summary>For a monitor: hdmi, displayport, internal, dvi, vga or other.</summary>
    public string? VideoOutput { get; set; }
}

/// <summary>A device arriving or going, as the history keeps it.</summary>
public sealed record DeviceEvent(DateTime Time, string Key, string Name, bool Arrived, bool Wireless);

/// <summary>A device that keeps dropping out: gone and back within moments.</summary>
/// <summary>A device Windows failed to start, from its log.</summary>
public sealed record DeviceStartFailure(string Name, int Count, DateTime Last);

/// <summary>What Windows logged about devices lately.</summary>
public sealed class DeviceLogInfo
{
    public List<DeviceStartFailure> StartFailures { get; set; } = [];

    /// <summary>Disconnects the Wi-Fi came back from within moments.</summary>
    public int WifiDrops { get; set; }

    public int WifiFailures { get; set; }
}

/// <summary>The Wi-Fi signal in the history. No network names are kept, only its strength.</summary>
public sealed record WifiHealth(int Minutes, int AverageSignal, int LowestSignal, int WeakPercent);

public sealed record DeviceDrops([property: JsonIgnore] string Key, string Name, int Count, DateTime Last, bool Wireless);
