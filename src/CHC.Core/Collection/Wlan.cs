using System.Runtime.InteropServices;

namespace CHC.Core.Collection;

/// <summary>
/// Signal, channel and band of the connected Wi-Fi network (wlanapi.dll). Structures are read by
/// offset; every value is checked for plausibility, and anything that fails is simply left empty.
/// Since Windows 11 24H2 these calls may be refused unless desktop apps may use the location.
/// </summary>
internal static partial class Wlan
{
    public sealed record Connection(int? SignalPercent, int? RssiDbm, int? Channel, double? BandGHz, string Standard);

    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeChannelNumber = 8;
    private const int OpcodeRssi = 0x10000102;
    private const int StateConnected = 1;

    // WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] description (512) + state (4).
    private const int InterfaceInfoSize = 532;

    // WLAN_CONNECTION_ATTRIBUTES: state, mode, WCHAR[256] profile name, then WLAN_ASSOCIATION_ATTRIBUTES at 520.
    private const int AssocOffset = 520;
    private const int BssidOffset = AssocOffset + 40;
    private const int PhyTypeOffset = AssocOffset + 48;
    private const int SignalOffset = AssocOffset + 56;

    // WLAN_BSS_ENTRY (360 bytes): BSSID at 40, center frequency (kHz) at 92.
    private const int BssEntrySize = 360;
    private const int BssEntryBssid = 40;
    private const int BssEntryFrequency = 92;

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr list);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(IntPtr memory);

    /// <summary>How strong the signal of the connected Wi-Fi network is, or null without one.</summary>
    public static int? CurrentSignal()
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return null;
            try
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return null;
                try
                {
                    var count = Marshal.ReadInt32(list);
                    for (var i = 0; i < count; i++)
                    {
                        var item = list + 8 + i * InterfaceInfoSize;
                        var guid = Marshal.PtrToStructure<Guid>(item);
                        if (Marshal.ReadInt32(item + 16 + 512) != StateConnected) continue;
                        if (WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0) continue;
                        try
                        {
                            if (size >= SignalOffset + 4 && Marshal.ReadInt32(data + SignalOffset) is var quality and >= 0 and <= 100) return quality;
                        }
                        finally { WlanFreeMemory(data); }
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    /// <summary>The connected Wi-Fi network of the adapter with this id (NetworkInterface.Id), or null.</summary>
    public static Connection? Query(string adapterId)
    {
        if (!Guid.TryParse(adapterId, out var wanted)) return null;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return null;
            try
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return null;
                try
                {
                    var count = Marshal.ReadInt32(list);
                    for (var i = 0; i < count; i++)
                    {
                        var item = list + 8 + i * InterfaceInfoSize;
                        var guid = Marshal.PtrToStructure<Guid>(item);
                        if (guid != wanted || Marshal.ReadInt32(item + 16 + 512) != StateConnected) continue;
                        return Read(handle, guid);
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    private static Connection? Read(IntPtr handle, Guid iface)
    {
        int? signal = null;
        int phy = 0;
        byte[]? bssid = null;
        if (WlanQueryInterface(handle, ref iface, OpcodeCurrentConnection, IntPtr.Zero, out var size, out var data, IntPtr.Zero) == 0)
        {
            try
            {
                if (size >= SignalOffset + 4)
                {
                    var quality = Marshal.ReadInt32(data + SignalOffset);
                    if (quality is >= 0 and <= 100) signal = quality;
                    phy = Marshal.ReadInt32(data + PhyTypeOffset);
                    bssid = new byte[6];
                    Marshal.Copy(data + BssidOffset, bssid, 0, 6);
                }
            }
            finally { WlanFreeMemory(data); }
        }

        var channel = ReadInt(handle, iface, OpcodeChannelNumber) is { } ch and >= 1 and <= 233 ? ch : (int?)null;
        var rssi = ReadInt(handle, iface, OpcodeRssi) is { } r and >= -120 and < 0 ? r : (int?)null;
        var band = bssid is null ? null : BandFromBssList(handle, iface, bssid);
        band ??= BandFromChannel(channel, phy);
        if (signal is null && channel is null && rssi is null) return null;
        return new Connection(signal, rssi, channel, band, Standard(phy));
    }

    private static int? ReadInt(IntPtr handle, Guid iface, int opcode)
    {
        if (WlanQueryInterface(handle, ref iface, opcode, IntPtr.Zero, out var size, out var data, IntPtr.Zero) != 0) return null;
        try { return size >= 4 ? Marshal.ReadInt32(data) : null; }
        finally { WlanFreeMemory(data); }
    }

    /// <summary>The access point's center frequency tells the band exactly (2.4, 5 or 6 GHz).</summary>
    private static double? BandFromBssList(IntPtr handle, Guid iface, byte[] bssid)
    {
        const int AnyBssType = 3;
        if (WlanGetNetworkBssList(handle, ref iface, IntPtr.Zero, AnyBssType, false, IntPtr.Zero, out var list) != 0) return null;
        try
        {
            var count = Marshal.ReadInt32(list + 4);
            var entry = new byte[6];
            for (var i = 0; i < Math.Min(count, 512); i++)
            {
                var item = list + 8 + i * BssEntrySize;
                Marshal.Copy(item + BssEntryBssid, entry, 0, 6);
                if (!entry.AsSpan().SequenceEqual(bssid)) continue;
                var kHz = (uint)Marshal.ReadInt32(item + BssEntryFrequency);
                return kHz switch
                {
                    >= 2_400_000 and < 2_500_000 => 2.4,
                    >= 5_150_000 and < 5_925_000 => 5,
                    >= 5_925_000 and < 7_200_000 => 6,
                    _ => null,
                };
            }
        }
        finally { WlanFreeMemory(list); }
        return null;
    }

    /// <summary>Fallback: channels 1-14 are 2.4 GHz and 36+ are 5 GHz, unless Wi-Fi 6E/7 could be on 6 GHz.</summary>
    private static double? BandFromChannel(int? channel, int phy)
    {
        var maybe6GHz = phy >= 10 && channel % 4 == 1;
        return channel switch
        {
            null => null,
            <= 14 => maybe6GHz ? null : 2.4,
            >= 36 and <= 177 => maybe6GHz && channel is < 149 or > 165 ? null : 5,
            _ => null,
        };
    }

    private static string Standard(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => "",
    };
}
