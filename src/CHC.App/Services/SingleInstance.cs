using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CHC.App.Services;

/// <summary>
/// Keeps one CHC per Windows session. A second start (the Start menu while CHC records in the tray) asks the
/// running one to show its window and quits. The running copy may be elevated (it starts at sign-in with the
/// highest rights), so the "show" signal is opened up to the normal rights a Start menu click has.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\CHC.App.Instance";
    private const string ShowName = @"Local\CHC.App.Show";
    private static Mutex? _mutex;
    private static EventWaitHandle? _show;
    private static RegisteredWaitHandle? _wait;
    private static Action? _onShow;

    /// <summary>True if this is the only CHC; false if another one runs (it has been asked to show itself).</summary>
    public static bool TryAcquire(bool signalOther)
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy is elevated and this one is not: it exists all the same.
            _mutex = null;
        }

        if (_mutex is null)
        {
            if (signalOther) Signal();
            return false;
        }

        _show = OpenToNormalRights() ?? new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        if (_onShow is { } show) Listen(show);
        return true;
    }

    /// <summary>
    /// The "show" event with a medium integrity label, so a normal Start menu click may raise it when this copy runs
    /// elevated. The label goes in at creation: .NET's ACL classes would set it as an audit list, which needs a privilege.
    /// </summary>
    private static EventWaitHandle? OpenToNormalRights()
    {
        // Everyone may use it; a medium integrity process may write to it.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;0x1F0003;;;WD)S:(ML;;NW;;;ME)", 1, out var descriptor, IntPtr.Zero))
            return null;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateEventW(ref attributes, false, false, ShowName);
            if (handle.IsInvalid) return null;
            return new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>Calls <paramref name="show"/> (on a pool thread) whenever another start asks for the window.</summary>
    public static void OnShowRequested(Action show)
    {
        _onShow = show;
        Listen(show);
    }

    private static void Listen(Action show)
    {
        if (_show is null || _wait is not null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, false);
    }

    private static void Signal()
    {
        try
        {
            using var other = EventWaitHandle.OpenExisting(ShowName);
            other.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>Lets a restarted (elevated) copy take over before this one has quit.</summary>
    public static void Release()
    {
        _wait?.Unregister(null);
        _wait = null;
        _show?.Dispose();
        _show = null;
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _mutex = null;
    }
}
