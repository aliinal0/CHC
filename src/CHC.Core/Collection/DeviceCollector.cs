using System.Globalization;
using CHC.Core.Devices;

namespace CHC.Core.Collection;

/// <summary>
/// What is connected to the PC. Windows' device list says what each device is, its USB hubs say how fast each runs and
/// whether its port has a problem, and the Bluetooth stack says what is paired and connected.
/// </summary>
internal static class DeviceCollector
{
    private sealed record Entity(string Id, string Name, string Class, string Manufacturer, int Error);

    public static List<ConnectedDevice> Collect()
    {
        var entities = Wmi.Query("SELECT Name, DeviceID, PNPClass, Manufacturer, ConfigManagerErrorCode FROM Win32_PnPEntity")
            .Select(o => new Entity(o.Str("DeviceID"), o.Str("Name"), o.Str("PNPClass"), o.Str("Manufacturer"), (int)o.U64("ConfigManagerErrorCode")))
            .Where(e => e.Id.Length > 0)
            .ToList();
        var byId = entities.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var devices = new List<ConnectedDevice>();
        var usb = Usb(byId, devices);
        Attach(entities, usb);
        Bluetooth(entities, devices);
        Monitors(devices);
        OtherHardware(entities, usb, devices);
        return devices;
    }

    // ---------------------------------------------------------------- USB

    /// <summary>One device per used USB port, except hubs; a port with a problem is listed even when no device could be named.</summary>
    private static Dictionary<string, ConnectedDevice> Usb(Dictionary<string, Entity> byId, List<ConnectedDevice> devices)
    {
        var byKey = new Dictionary<string, ConnectedDevice>(StringComparer.OrdinalIgnoreCase);
        var idOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in Native.DevicesOf("USB"))
            if (Native.DriverKey(id) is { Length: > 0 } driver)
                idOf[driver] = id;

        var failed = 0;
        foreach (var port in Native.UsbPorts())
        {
            if (port.IsHub && port.Status == 1) continue;
            var id = port.DriverKey is not null ? idOf.GetValueOrDefault(port.DriverKey) : null;
            var entity = id is null ? null : byId.GetValueOrDefault(id);
            var device = new ConnectedDevice
            {
                Key = id ?? $"usb-port-{failed++}",
                Name = (id is null ? null : Native.BusReportedName(id)) ?? entity?.Name ?? "",
                Manufacturer = Manufacturer(entity?.Manufacturer),
                Bus = DeviceBus.Usb,
                BuiltIn = !port.UserConnectable,
                Speed = (UsbSpeed)Math.Clamp(port.Speed, 0, 4),
                SuperSpeedCapable = port.SuperSpeedCapable,
                UsbPortStatus = port.Status == 1 ? 0 : port.Status,
                ProblemCode = entity?.Error ?? 0,
            };
            devices.Add(device);
            if (id is not null) byKey[id] = device;
        }
        return byKey;
    }

    /// <summary>The USB device a keyboard, a mouse or a sound device belongs to: what it is follows from the parts Windows sees in it.</summary>
    private static void Attach(List<Entity> entities, Dictionary<string, ConnectedDevice> usb)
    {
        // Gaming mice add a keyboard part for their macro buttons, and keyboards a mouse part: the device's first part says which it is.
        var first = new Dictionary<ConnectedDevice, DeviceKind>();
        foreach (var e in entities)
        {
            if (e.Error is 24 or 45 || RootOf(e.Id, usb) is not { } device) continue;
            if (e.Error != 0 && device.ProblemCode == 0) device.ProblemCode = e.Error;
            var kind = KindOf(e);
            if (Better(kind, device.Kind)) device.Kind = kind;
            if (kind is DeviceKind.Keyboard or DeviceKind.Mouse && (e.Id.Contains("&MI_00", StringComparison.OrdinalIgnoreCase) || !e.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
                first[device] = kind;
        }
        foreach (var device in usb.Values)
        {
            bool Named(string word) => device.Name.Contains(word, StringComparison.OrdinalIgnoreCase);
            if (Named("mystic light") || Named("aura led") || Named("rgb")) device.BuiltIn = true;
            if (device.Kind is DeviceKind.Keyboard or DeviceKind.Mouse && first.TryGetValue(device, out var kind)) device.Kind = kind;
            if (Named("mouse")) device.Kind = DeviceKind.Mouse;
            else if (Named("keyboard")) device.Kind = DeviceKind.Keyboard;
            else if (Named("receiver") || Named("dongle")) device.Kind = DeviceKind.Other;
            else if (device.Kind == DeviceKind.Audio && Named("head")) device.Kind = DeviceKind.Headset;
        }
    }

    private static ConnectedDevice? RootOf(string id, Dictionary<string, ConnectedDevice> usb)
    {
        // The device's own id, or an ancestor's: a receiver -> its interface -> a HID device -> a mouse.
        var current = id;
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            if (usb.TryGetValue(current, out var found)) return found;
            current = Native.ParentDevice(current);
        }
        return null;
    }

    /// <summary>A device with several parts counts as the one that says most: a keyboard with a media-key HID part is a keyboard.</summary>
    private static bool Better(DeviceKind candidate, DeviceKind current) => Rank(candidate) < Rank(current);

    private static int Rank(DeviceKind kind) => kind switch
    {
        DeviceKind.GameController => 0,
        DeviceKind.Storage => 1,
        DeviceKind.Keyboard => 2,
        DeviceKind.Mouse => 3,
        DeviceKind.Camera => 4,
        DeviceKind.Printer => 5,
        DeviceKind.Phone => 6,
        DeviceKind.BluetoothAdapter => 7,
        DeviceKind.Network => 8,
        DeviceKind.Headset => 9,
        DeviceKind.Audio => 10,
        _ => 20,
    };

    private static DeviceKind KindOf(Entity e) => e.Class.ToUpperInvariant() switch
    {
        "KEYBOARD" => DeviceKind.Keyboard,
        "MOUSE" => DeviceKind.Mouse,
        "HIDCLASS" when e.Name.Contains("game controller", StringComparison.OrdinalIgnoreCase) => DeviceKind.GameController,
        "XNACOMPOSITE" or "XBOXCOMPOSITE" or "XINPUT" => DeviceKind.GameController,
        "CAMERA" or "IMAGE" => DeviceKind.Camera,
        "MEDIA" or "AUDIOENDPOINT" => DeviceKind.Audio,
        "DISKDRIVE" or "CDROM" => DeviceKind.Storage,
        "WPD" => DeviceKind.Phone,
        "NET" => DeviceKind.Network,
        "BLUETOOTH" => DeviceKind.BluetoothAdapter,
        "PRINTER" => DeviceKind.Printer,
        _ => DeviceKind.Other,
    };

    /// <summary>Drivers often give a generic maker: it only helps when it names one.</summary>
    private static string Manufacturer(string? maker) =>
        maker is null || maker.StartsWith('(') || maker.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || maker.Contains("Generic", StringComparison.OrdinalIgnoreCase) || maker.Contains("standard", StringComparison.OrdinalIgnoreCase) ? "" : maker;

    // ---------------------------------------------------------------- Bluetooth

    private static void Bluetooth(List<Entity> entities, List<ConnectedDevice> devices)
    {
        var paired = Native.BluetoothDevices();
        foreach (var e in entities)
        {
            // BTHENUM\DEV_XXXXXXXXXXXX\... (classic), BTHLE\DEV_XXXXXXXXXXXX\... (low energy); services hang below them.
            var classic = e.Id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);
            if (!classic && !e.Id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)) continue;
            var start = e.Id.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase) + 4;
            var address = e.Id.Length >= start + 12 && ulong.TryParse(e.Id.AsSpan(start, 12), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ? a : 0;
            bool? connected = Native.BluetoothConnected(e.Id);
            uint type = 0;
            if (paired.TryGetValue(address, out var known))
            {
                connected ??= known.Connected;
                type = known.Class;
            }
            devices.Add(new ConnectedDevice
            {
                Key = e.Id,
                Name = e.Name,
                Manufacturer = "",
                Bus = DeviceBus.Bluetooth,
                Kind = BluetoothKind(type, e.Name),
                Connected = connected,
                BatteryPercent = Native.BluetoothBatteryPercent(e.Id),
                ProblemCode = e.Error,
            });
        }
    }

    /// <summary>From the device's class when it has one (major at bits 8-12, for peripherals keyboard and pointer at bits 6-7), else its name.</summary>
    private static DeviceKind BluetoothKind(uint type, string name)
    {
        var major = (type >> 8) & 0x1F;
        if (major == 2) return DeviceKind.Phone;
        if (major == 4) return DeviceKind.Headset;
        if (major == 5)
        {
            var minor = (type >> 6) & 3;
            if (minor == 1 || minor == 3) return DeviceKind.Keyboard;
            if (minor == 2) return DeviceKind.Mouse;
            return DeviceKind.GameController;
        }
        bool Has(string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
        return Has("mouse") || Has("trackball") ? DeviceKind.Mouse
            : Has("keyboard") || Has("keys") ? DeviceKind.Keyboard
            : Has("controller") || Has("gamepad") ? DeviceKind.GameController
            : Has("buds") || Has("headset") || Has("headphone") || Has("airpods") || Has("speaker") ? DeviceKind.Headset
            : Has("phone") || Has("galaxy") ? DeviceKind.Phone
            : DeviceKind.Other;
    }

    // ---------------------------------------------------------------- Monitors

    private static void Monitors(List<ConnectedDevice> devices)
    {
        var outputs = Wmi.Query("SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", Wmi.Wmi_)
            .GroupBy(o => o.Str("InstanceName"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().U64("VideoOutputTechnology"), StringComparer.OrdinalIgnoreCase);
        foreach (var m in Wmi.Query("SELECT InstanceName, UserFriendlyName, ManufacturerName FROM WmiMonitorID", Wmi.Wmi_))
        {
            var id = m.Str("InstanceName");
            var technology = outputs.TryGetValue(id, out var value) ? value : ulong.MaxValue;
            devices.Add(new ConnectedDevice
            {
                Key = id,
                Name = Text(m, "UserFriendlyName"),
                Manufacturer = "",
                Kind = DeviceKind.Display,
                Bus = DeviceBus.Video,
                VideoOutput = technology switch
                {
                    0 => "vga",
                    4 => "dvi",
                    5 => "hdmi",
                    10 => "displayport",
                    11 or 0x80000000 or 6 => "internal",
                    _ => "other",
                },
            });
        }
    }

    /// <summary>WMI hands monitor names over as an array of character codes.</summary>
    private static string Text(System.Management.ManagementBaseObject o, string property)
    {
        try
        {
            if (o[property] is ushort[] codes)
                return new string(codes.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
        }
        catch (System.Management.ManagementException) { }
        return "";
    }

    // ---------------------------------------------------------------- what isn't on USB

    /// <summary>Sound, cameras, printers, network and Bluetooth hardware on other buses; virtual devices are left out.</summary>
    private static void OtherHardware(List<Entity> entities, Dictionary<string, ConnectedDevice> usb, List<ConnectedDevice> devices)
    {
        foreach (var e in entities)
        {
            var kind = KindOf(e);
            var wanted = e.Class.ToUpperInvariant() is "MEDIA" or "CAMERA" or "IMAGE" or "PRINTER" or "NET" or "BLUETOOTH" or "KEYBOARD" or "MOUSE" or "XNACOMPOSITE" or "XBOXCOMPOSITE";
            // 24 and 45: Windows remembers the device, but it isn't there (the PS/2 mouse of every desktop PC).
            if (!wanted || e.Error is 24 or 45 || IsVirtual(e) || RootOf(e.Id, usb) is not null) continue;
            devices.Add(new ConnectedDevice
            {
                Key = e.Id,
                Name = e.Name,
                Manufacturer = Manufacturer(e.Manufacturer),
                Kind = kind,
                Bus = DeviceBus.Internal,
                BuiltIn = true,
                ProblemCode = e.Error,
            });
        }
    }

    private static bool IsVirtual(Entity e) =>
        e.Id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) || e.Id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
        || e.Id.StartsWith(@"BTH", StringComparison.OrdinalIgnoreCase) || e.Id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)
        || e.Id.StartsWith(@"RDP", StringComparison.OrdinalIgnoreCase) || e.Id.StartsWith(@"TS_", StringComparison.OrdinalIgnoreCase)
        || e.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
        || e.Name.Contains("Microsoft Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase);
}
