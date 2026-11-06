using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CHC.Core.Collection;

namespace CHC.Core.Devices;

/// <summary>
/// Notes USB devices and Bluetooth input devices arriving and going, so one that keeps dropping out can be named.
/// Windows reports each event by itself; nothing is polled and no device is touched.
/// </summary>
public sealed partial class DeviceWatcher : IDisposable
{
    private static readonly Guid UsbDevice = new("a5dcbf10-6530-11d2-901f-00c04fb951ed");
    private static readonly Guid HidDevice = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(IntPtr notify, IntPtr context, int action, IntPtr data, uint size);

    private readonly ConcurrentQueue<(DateTime Time, string Id, string Key, bool Arrived, bool Wireless)> _events = new();
    private readonly List<IntPtr> _registrations = [];
    private Callback? _callback;

    public void Start()
    {
        if (_callback is not null) return;
        _callback = OnEvent;
        foreach (var interfaceClass in new[] { UsbDevice, HidDevice })
        {
            // CM_NOTIFY_FILTER: size, flags, type (0 = device interface), reserved, then a union of 400 bytes.
            var filter = new byte[416];
            BitConverter.TryWriteBytes(filter, 416);
            interfaceClass.TryWriteBytes(filter.AsSpan(16));
            try
            {
                if (CM_Register_Notification(filter, IntPtr.Zero, _callback, out var registration) == 0)
                    _registrations.Add(registration);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }

    /// <summary>What happened since the last call.</summary>
    public List<DeviceEvent> Drain()
    {
        var list = new List<DeviceEvent>();
        while (_events.TryDequeue(out var e))
        {
            string name;
            try { name = Name(e.Id); }
            catch (Exception) { name = ""; }
            list.Add(new DeviceEvent(e.Time, e.Key, name, e.Arrived, e.Wireless));
        }
        return list;
    }

    private uint OnEvent(IntPtr notify, IntPtr context, int action, IntPtr data, uint size)
    {
        // Windows calls this on its own thread, and a slow or failing handler holds up its device notifications.
        try
        {
            if (action is 0 or 1 && data != IntPtr.Zero && Marshal.PtrToStringUni(data + 24) is { } link && Parse(link) is { } device)
                _events.Enqueue((DateTime.Now, device.Id, device.Key, action == 0, device.Wireless));
        }
        catch (Exception) { /* a device we can't name isn't worth losing the next one for */ }
        return 0;
    }

    /// <summary>
    /// "\\?\USB#VID_046D&amp;PID_C52B#serial#{guid}" is the device "USB\VID_046D&amp;PID_C52B\serial". Parts of a composite
    /// device are left out, its USB device speaks for them, and of HID devices only Bluetooth ones, since USB ones are counted as USB.
    /// A Bluetooth keyboard appears as several HID collections at once: the key leaves out what tells them apart, so they count as one device.
    /// </summary>
    internal static (string Id, string Key, bool Wireless)? Parse(string link)
    {
        var parts = link.TrimStart('\\', '?').Split('#');
        if (parts.Length < 3) return null;
        var id = $@"{parts[0]}\{parts[1]}\{parts[2]}";
        if (parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase))
            return parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase) ? null : (id, id, false);
        if (parts[0].Equals("HID", StringComparison.OrdinalIgnoreCase) && (parts[1].Contains("00001124", StringComparison.Ordinal) || parts[1].Contains("00001812", StringComparison.Ordinal)))
            return (id, $@"HID\{Collection().Replace(parts[1], "")}", true);
        return null;
    }

    /// <summary>The device's own name; a Bluetooth input device's generic one is replaced by the paired device above it.</summary>
    private static string Name(string key)
    {
        if (key.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
            return Native.BusReportedName(key) ?? Native.DeviceName(key) ?? "";
        for (var current = Native.ParentDevice(key); current is not null; current = Native.ParentDevice(current))
            if (current.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) || current.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase))
                return Native.DeviceName(current) ?? "";
        return Native.DeviceName(key) ?? "";
    }

    [GeneratedRegex("&Col[0-9A-F]+", RegexOptions.IgnoreCase)]
    private static partial Regex Collection();

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification([In] byte[] filter, IntPtr context, Callback callback, out IntPtr registration);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(IntPtr registration);

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            try { CM_Unregister_Notification(registration); }
            catch (DllNotFoundException) { }
        }
        _registrations.Clear();
        _callback = null;
    }
}
