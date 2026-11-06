using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CHC.Core.Collection;

/// <summary>Windows' device tree and USB hubs, read without administrator rights.</summary>
internal static partial class Native
{
    private const uint CrSuccess = 0;
    private const uint DevPropTypeByte = 0x03;
    private const uint DevPropTypeBoolean = 0x11;
    private const uint DevPropTypeStringValue = 0x12;
    private static readonly Guid BusReported = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2");
    private static readonly Guid BluetoothBattery = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5");
    private static readonly Guid BluetoothDevice = new("83da6326-97a6-4088-9453-a1923f573b29");
    private static readonly Guid UsbHubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint GetParent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDeviceId(uint devInst, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDeviceIdListSize(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDeviceIdList(string? filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetInterfaceListSize(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetInterfaceList(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial uint GetRegistryProperty(uint devInst, uint property, out uint type, [Out] byte[]? buffer, ref uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    private static List<string> MultiString(char[] buffer) =>
        new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The device a device hangs from in the device tree.</summary>
    public static string? ParentDevice(string instanceId)
    {
        if (LocateDevNode(out var node, instanceId, 0) != CrSuccess || GetParent(out var parent, node, 0) != CrSuccess) return null;
        var buffer = new char[512];
        return GetDeviceId(parent, buffer, (uint)buffer.Length, 0) == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
    }

    /// <summary>All present devices whose id starts with this enumerator ("USB").</summary>
    public static List<string> DevicesOf(string enumerator)
    {
        const uint FilterEnumerator = 1, FilterPresent = 0x100;
        if (GetDeviceIdListSize(out var length, enumerator, FilterEnumerator | FilterPresent) != CrSuccess || length == 0) return [];
        var buffer = new char[length];
        return GetDeviceIdList(enumerator, buffer, length, FilterEnumerator | FilterPresent) == CrSuccess ? MultiString(buffer) : [];
    }

    /// <summary>The device's registry key under the driver class: how a USB hub names what is on its ports.</summary>
    public static string? DriverKey(string instanceId)
    {
        const uint PropertyDriver = 0x0A;
        if (LocateDevNode(out var node, instanceId, 0) != CrSuccess) return null;
        uint size = 512;
        var buffer = new byte[size];
        return GetRegistryProperty(node, PropertyDriver, out _, buffer, ref size, 0) == CrSuccess
            ? Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0')
            : null;
    }

    private static byte[]? DeviceProperty(string instanceId, Guid format, uint id, out uint type)
    {
        type = 0;
        const uint LocatePhantom = 1;
        if (LocateDevNode(out var node, instanceId, LocatePhantom) != CrSuccess) return null;
        var key = new DevPropKey { FormatId = format, PropertyId = id };
        uint size = 0;
        GetDevNodePropertyBytes(node, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        return GetDevNodePropertyBytes(node, ref key, out type, buffer, ref size, 0) == CrSuccess ? buffer : null;
    }

    /// <summary>The name the device reports for itself ("G502 HERO Gaming Mouse"); Windows' own names are often just "USB Input Device".</summary>
    public static string? BusReportedName(string instanceId)
    {
        var bytes = DeviceProperty(instanceId, BusReported, 4, out var type);
        return bytes is not null && type == DevPropTypeStringValue ? Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim() : null;
    }

    /// <summary>The charge a Bluetooth device reports, if it does.</summary>
    public static int? BluetoothBatteryPercent(string instanceId)
    {
        var bytes = DeviceProperty(instanceId, BluetoothBattery, 2, out var type);
        return bytes is { Length: >= 1 } && type == DevPropTypeByte && bytes[0] <= 100 ? bytes[0] : null;
    }

    /// <summary>Whether a Bluetooth device is connected right now; null when Windows doesn't say.</summary>
    public static bool? BluetoothConnected(string instanceId)
    {
        var bytes = DeviceProperty(instanceId, BluetoothDevice, 15, out var type);
        return bytes is { Length: >= 1 } && type == DevPropTypeBoolean ? bytes[0] != 0 : null;
    }

    /// <summary>One used port of a USB hub.</summary>
    /// <param name="Speed">0 low, 1 full, 2 high, 3 super, 4 super plus.</param>
    /// <param name="Status">1 is working; anything else is what went wrong.</param>
    public readonly record struct UsbPort(string? DriverKey, int Speed, bool? SuperSpeedCapable, int Status, bool IsHub, bool UserConnectable);

    /// <summary>What is plugged into every USB hub, from the hubs themselves.</summary>
    public static List<UsbPort> UsbPorts()
    {
        const uint InterfacePresent = 0;
        var result = new List<UsbPort>();
        var hubClass = UsbHubInterface;
        if (GetInterfaceListSize(out var length, ref hubClass, null, InterfacePresent) != CrSuccess || length == 0) return result;
        var buffer = new char[length];
        if (GetInterfaceList(ref hubClass, null, buffer, length, InterfacePresent) != CrSuccess) return result;
        foreach (var hub in MultiString(buffer))
        {
            try { ReadHub(hub, result); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return result;
    }

    private static void ReadHub(string path, List<UsbPort> into)
    {
        const uint GenericWrite = 0x40000000, ShareWrite = 2, OpenExisting = 3;
        const uint GetNodeInformation = 0x220408, GetConnectionEx = 0x220448, GetConnectionExV2 = 0x22045C, GetDriverKey = 0x220420, GetConnector = 0x220458;
        using var hub = CreateFile(path, GenericWrite, ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (hub.IsInvalid) return;

        var node = new byte[76];
        if (!DeviceIoControl(hub, GetNodeInformation, node, (uint)node.Length, node, (uint)node.Length, out _, IntPtr.Zero)) return;
        // USB_NODE_INFORMATION: the node type, then the hub descriptor with the port count as its third byte.
        int ports = node[6];
        for (var port = 1; port <= ports; port++)
        {
            var info = new byte[512];
            BitConverter.TryWriteBytes(info, port);
            if (!DeviceIoControl(hub, GetConnectionEx, info, (uint)info.Length, info, (uint)info.Length, out _, IntPtr.Zero)) continue;
            // USB_NODE_CONNECTION_INFORMATION_EX, packed: speed at 23, is-hub at 24, connection status at 31.
            var status = BitConverter.ToInt32(info, 31);
            if (status == 0) continue;
            var speed = (int)info[23];
            var isHub = info[24] != 0;

            bool? capable = null;
            var v2 = new byte[16];
            BitConverter.TryWriteBytes(v2, port);
            BitConverter.TryWriteBytes(v2.AsSpan(4), 16);
            BitConverter.TryWriteBytes(v2.AsSpan(8), 7); // asks about USB 1.1, 2.0 and 3
            if (DeviceIoControl(hub, GetConnectionExV2, v2, 16, v2, 16, out _, IntPtr.Zero))
            {
                var flags = BitConverter.ToUInt32(v2, 12);
                capable = (flags & 2) != 0 || (flags & 8) != 0;
                if ((flags & 4) != 0) speed = 4; // running at SuperSpeedPlus (10 Gbit/s or more)
            }

            var connectable = true;
            var connector = new byte[256];
            BitConverter.TryWriteBytes(connector, port);
            if (DeviceIoControl(hub, GetConnector, connector, (uint)connector.Length, connector, (uint)connector.Length, out _, IntPtr.Zero))
                connectable = (BitConverter.ToUInt32(connector, 8) & 1) != 0;

            string? driverKey = null;
            var key = new byte[1024];
            BitConverter.TryWriteBytes(key, port);
            if (DeviceIoControl(hub, GetDriverKey, key, (uint)key.Length, key, (uint)key.Length, out _, IntPtr.Zero))
            {
                var actual = BitConverter.ToInt32(key, 4);
                if (actual > 8 && actual <= key.Length) driverKey = Encoding.Unicode.GetString(key, 8, actual - 8).TrimEnd('\0');
            }
            into.Add(new UsbPort(driverKey, speed, capable, status, isHub, connectable));
        }
    }

    // ---- Paired Bluetooth devices (classic), for their type and state.

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothSearchParams
    {
        public uint Size;
        public int ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

#pragma warning disable SYSLIB1054 // The struct holds an inline string; classic marshalling keeps it simple.
    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothSearchParams search, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr find, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr find);
#pragma warning restore SYSLIB1054

    /// <summary>Paired devices by address: what kind of device each says it is, and whether it is connected.</summary>
    public static Dictionary<ulong, (uint Class, bool Connected)> BluetoothDevices()
    {
        var result = new Dictionary<ulong, (uint, bool)>();
        try
        {
            var search = new BluetoothSearchParams { Size = (uint)Marshal.SizeOf<BluetoothSearchParams>(), ReturnAuthenticated = 1, ReturnRemembered = 1, ReturnConnected = 1 };
            var info = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(), Name = "" };
            var find = BluetoothFindFirstDevice(ref search, ref info);
            if (find == IntPtr.Zero) return result;
            do result[info.Address] = (info.ClassOfDevice, info.Connected != 0);
            while (BluetoothFindNextDevice(find, ref info));
            BluetoothFindDeviceClose(find);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return result;
    }
}
