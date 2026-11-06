using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CHC.Core.Collection;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFirmwareType(out int firmwareType);

    public static MemoryStatusEx? MemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status : null;
    }

    /// <summary>True for UEFI, false for legacy BIOS, null if unknown.</summary>
    public static bool? IsUefi()
    {
        try
        {
            if (!GetFirmwareType(out var type)) return null;
            return type switch { 1 => false, 2 => true, _ => null };
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    // ---- Power plan (powrprof.dll); values are read from the active plan, on AC power.

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr settingGuid, IntPtr buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool GetPwrCapabilities(IntPtr capabilities);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    public static Guid? ActivePowerScheme()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
            try { return Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
        }
        catch (DllNotFoundException) { return null; }
    }

    public static uint? PowerAcValue(Guid scheme, Guid subGroup, Guid setting) =>
        PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out var value) == 0 ? value : null;

    public static string? PowerSchemeName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>SYSTEM_POWER_CAPABILITIES: HiberFilePresent is byte 8, AoAc (Modern Standby) byte 20.</summary>
    public static (bool HiberFilePresent, bool ModernStandby)? PowerCapabilities()
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (var i = 0; i < 256; i++) Marshal.WriteByte(buffer, i, 0);
            if (!GetPwrCapabilities(buffer)) return null;
            return (Marshal.ReadByte(buffer, 8) != 0, Marshal.ReadByte(buffer, 20) != 0);
        }
        catch (DllNotFoundException) { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ---- Battery right now (no administrator rights needed).

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBatteryState
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public byte Tag;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, out SystemBatteryState outputBuffer, uint outputBufferLength);

    /// <summary>
    /// SYSTEM_BATTERY_STATE: AC power, charge and the charge/discharge rate in mW (0x80000000 = unknown).
    /// Returns null on PCs without a battery. Remaining and full capacity are in mWh.
    /// </summary>
    public static (Models.BatteryState State, uint FullMWh, uint RemainingMWh)? Battery()
    {
        try
        {
            if (CallNtPowerInformation(5, IntPtr.Zero, 0, out var s, (uint)Marshal.SizeOf<SystemBatteryState>()) != 0 || s.BatteryPresent == 0) return null;
            double? rate = s.Rate == int.MinValue ? null : s.Rate / 1000.0;
            // Some drivers report the rate without a sign; the flags say which way the power flows.
            if (rate is { } r && (s.Discharging != 0 && r > 0 || s.Charging != 0 && r < 0)) rate = -r;
            double? percent = s.MaxCapacity > 0 ? Math.Min(100, 100.0 * s.RemainingCapacity / s.MaxCapacity) : null;
            return (new Models.BatteryState(s.AcOnLine != 0, s.Charging != 0, percent, rate), s.MaxCapacity, s.RemainingCapacity);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    // ---- Foreground window and display modes.

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static int ForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out var pid);
        return (int)pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Seconds since the last key press or mouse move in this session.</summary>
    public static double? IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        return unchecked((uint)Environment.TickCount - info.Time) / 1000.0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

#pragma warning disable SYSLIB1054 // DEVMODE holds inline strings; classic marshalling keeps it simple.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode mode);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// A display's current mode, the highest refresh rate at that resolution, and its largest (native)
    /// resolution with the highest refresh rate there. Null device = the main display.
    /// </summary>
    public static (int Width, int Height, int Current, int Max, int NativeWidth, int NativeHeight, int NativeMax)? DisplayModes(string? device = null)
    {
        var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettings(device, -1, ref mode)) return null;
        var (width, height, current) = ((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency);
        var max = current;
        var (nativeWidth, nativeHeight, nativeMax) = (width, height, current);
        for (var i = 0; i < 2000; i++)
        {
            var m = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(device, i, ref m)) break;
            var (w, h, hz) = ((int)m.PelsWidth, (int)m.PelsHeight, (int)m.DisplayFrequency);
            if (w == width && h == height) max = Math.Max(max, hz);
            if ((long)w * h > (long)nativeWidth * nativeHeight) (nativeWidth, nativeHeight, nativeMax) = (w, h, hz);
            else if (w == nativeWidth && h == nativeHeight) nativeMax = Math.Max(nativeMax, hz);
        }
        return (width, height, current, max, nativeWidth, nativeHeight, nativeMax);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

#pragma warning disable SYSLIB1054 // MONITORINFOEX holds an inline string, like DEVMODE.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// The window in front, if it belongs to <paramref name="processId"/>: its size, whether it fills its monitor
    /// (fullscreen or borderless) or has a title bar, and that monitor's device name.
    /// </summary>
    public static (int Width, int Height, bool CoversMonitor, bool HasTitleBar, string Device)? ForegroundWindowOf(int processId)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out var owner);
        if (owner != processId || !GetWindowRect(window, out var r)) return null;
        var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return null;
        const nint caption = 0x00C00000;
        var hasCaption = (GetWindowLongPtr(window, -16) & caption) == caption;
        var m = info.Monitor;
        var covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        return (r.Right - r.Left, r.Bottom - r.Top, covers, hasCaption, info.Device);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr process, uint flags, IntPtr name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    /// <summary>Full path of a program's exe. Needs only limited access, so it also works for games protected by anti-cheat.</summary>
    public static string? ProcessPath(int processId)
    {
        var process = OpenProcess(0x1000, false, (uint)processId);
        if (process == IntPtr.Zero) return null;
        var buffer = Marshal.AllocHGlobal(2048 * sizeof(char));
        try
        {
            uint size = 2048;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? Marshal.PtrToStringUni(buffer, (int)size) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(process);
        }
    }

    // ---- Kernel memory pools (GetPerformanceInfo).

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

    /// <summary>Paged and non-paged kernel pool in bytes.</summary>
    public static (ulong Paged, ulong NonPaged)? KernelPools()
    {
        var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref info, info.Size)) return null;
        var page = (ulong)info.PageSize;
        return ((ulong)info.KernelPaged * page, (ulong)info.KernelNonpaged * page);
    }

    // ---- Device properties (cfgmgr32), e.g. the PCI Express link of a graphics card.

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint LocateDevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint GetDevNodeProperty(uint devInst, ref DevPropKey key, out uint type, out uint value, ref uint size, uint flags);

    /// <summary>DEVPKEY_PciDevice_*: 9 current link speed, 10 current link width, 11 max link speed, 12 max link width.</summary>
    private static readonly Guid PciDeviceProperties = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");

    public static uint? PciProperty(string instanceId, uint propertyId)
    {
        const uint DevPropTypeUInt32 = 7;
        try
        {
            if (LocateDevNode(out var node, instanceId, 0) != 0) return null;
            var key = new DevPropKey { FormatId = PciDeviceProperties, PropertyId = propertyId };
            uint size = sizeof(uint);
            return GetDevNodeProperty(node, ref key, out var type, out var value, ref size, 0) == 0 && type == DevPropTypeUInt32 ? value : null;
        }
        catch (DllNotFoundException) { return null; }
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint GetDevNodePropertyBytes(uint devInst, ref DevPropKey key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);

    /// <summary>DEVPKEY_Device_*: 2 description, 14 friendly name.</summary>
    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    /// <summary>The name Device Manager shows for a device, also for one that has been unplugged since.</summary>
    public static string? DeviceName(string instanceId)
    {
        const uint LocatePhantom = 1, DevPropTypeString = 0x12;
        try
        {
            if (LocateDevNode(out var node, instanceId, LocatePhantom) != 0) return null;
            foreach (var id in new uint[] { 14, 2 })
            {
                var key = new DevPropKey { FormatId = DeviceProperties, PropertyId = id };
                uint size = 0;
                GetDevNodePropertyBytes(node, ref key, out _, null, ref size, 0);
                if (size < 2) continue;
                var buffer = new byte[size];
                if (GetDevNodePropertyBytes(node, ref key, out var type, buffer, ref size, 0) == 0 && type == DevPropTypeString)
                    return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
            }
        }
        catch (DllNotFoundException) { }
        return null;
    }

    // ---- Memory ranges a device was given (cfgmgr32), e.g. a graphics card's VRAM window for Resizable BAR.

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_First_Log_Conf")]
    private static partial uint GetFirstLogConf(out IntPtr logConf, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Next_Res_Des")]
    private static partial uint GetNextResDes(out IntPtr resDes, IntPtr current, uint forResource, out uint resourceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Res_Des_Data_Size")]
    private static partial uint GetResDesDataSize(out uint size, IntPtr resDes, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Res_Des_Data")]
    private static partial uint GetResDesData(IntPtr resDes, IntPtr buffer, uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Free_Res_Des_Handle")]
    private static partial uint FreeResDes(IntPtr resDes);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Free_Log_Conf_Handle")]
    private static partial uint FreeLogConf(IntPtr logConf);

    /// <summary>
    /// Size of the largest memory range assigned to a device, or null. MEM_DES and MEM_LARGE_DES both
    /// start with Count, Type, then the 64-bit base (offset 8) and end (offset 16) of the range.
    /// </summary>
    public static ulong? LargestMemoryRange(string instanceId)
    {
        const uint AllocLogConf = 2, ResTypeMem = 1, ResTypeMemLarge = 7;
        try
        {
            if (LocateDevNode(out var node, instanceId, 0) != 0 || GetFirstLogConf(out var logConf, node, AllocLogConf) != 0) return null;
            ulong? largest = null;
            try
            {
                var current = logConf;
                while (GetNextResDes(out var resDes, current, 0, out var type, 0) == 0)
                {
                    if (current != logConf) FreeResDes(current);
                    current = resDes;
                    if (type is not (ResTypeMem or ResTypeMemLarge) || GetResDesDataSize(out var size, resDes, 0) != 0 || size < 24) continue;
                    var buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetResDesData(resDes, buffer, size, 0) != 0) continue;
                        var start = (ulong)Marshal.ReadInt64(buffer, 8);
                        var end = (ulong)Marshal.ReadInt64(buffer, 16);
                        if (end > start) largest = Math.Max(largest ?? 0, end - start + 1);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                if (current != logConf) FreeResDes(current);
            }
            finally { FreeLogConf(logConf); }
            return largest;
        }
        catch (DllNotFoundException) { return null; }
    }

    // ---- Routing (iphlpapi).

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterface(uint destAddress, out uint bestIfIndex);

    /// <summary>Index of the network interface Windows would use to reach this IPv4 address, or -1.</summary>
    public static int BestInterfaceIndex(System.Net.IPAddress address)
    {
        try
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && GetBestInterface(BitConverter.ToUInt32(bytes, 0), out var index) == 0 ? (int)index : -1;
        }
        catch (DllNotFoundException) { return -1; }
    }

    // ---- TCP connections per process (iphlpapi), used to notice that a game is online.

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    /// <summary>Remote addresses of the established TCP connections a process owns (IPv4 and IPv6).</summary>
    public static List<System.Net.IPAddress> TcpPeers(int processId)
    {
        const int AfInet = 2, AfInet6 = 23, OwnerPidAll = 5, Established = 5;
        var peers = new List<System.Net.IPAddress>();
        foreach (var (family, rowSize) in new[] { (AfInet, 24), (AfInet6, 56) })
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, OwnerPidAll, 0);
            if (size <= 0) continue;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, OwnerPidAll, 0) != 0) continue;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * rowSize;
                    if (family == AfInet)
                    {
                        // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, pid.
                        if (Marshal.ReadInt32(row) != Established || Marshal.ReadInt32(row + 20) != processId) continue;
                        peers.Add(new System.Net.IPAddress((uint)Marshal.ReadInt32(row + 12)));
                    }
                    else
                    {
                        // MIB_TCP6ROW_OWNER_PID: local address (16), scope, port, remote address (16), scope, port, state, pid.
                        if (Marshal.ReadInt32(row + 48) != Established || Marshal.ReadInt32(row + 52) != processId) continue;
                        var address = new byte[16];
                        Marshal.Copy(row + 24, address, 0, 16);
                        peers.Add(new System.Net.IPAddress(address));
                    }
                }
            }
            catch (DllNotFoundException) { }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return peers;
    }

    // ---- DNS (dnsapi), bypassing the Windows DNS cache so the resolver itself is timed.

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsRecordListFree(IntPtr list, int freeType);

    /// <summary>Milliseconds an A-record lookup took, or null if it failed.</summary>
    public static double? DnsLookupMs(string name)
    {
        const ushort TypeA = 1;
        const uint BypassCache = 0x8;
        const int FreeRecordList = 1;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var status = DnsQuery(name, TypeA, BypassCache, IntPtr.Zero, out var results, IntPtr.Zero);
            watch.Stop();
            if (results != IntPtr.Zero) DnsRecordListFree(results, FreeRecordList);
            return status == 0 ? watch.Elapsed.TotalMilliseconds : null;
        }
        catch (DllNotFoundException) { return null; }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
