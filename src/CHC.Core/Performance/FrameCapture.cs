using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace CHC.Core.Performance;

/// <summary>One frame handed to the screen by a program.</summary>
public readonly record struct FramePresent(int ProcessId, double TimeMs);

/// <summary>Where frame timings come from: real ETW events, or the demo generator.</summary>
public interface IFrameSource : IDisposable
{
    /// <summary>False with a reason when frames cannot be captured (for example without administrator rights).</summary>
    bool Start(out string? error);

    /// <summary>Frames presented since the previous call.</summary>
    IReadOnlyList<FramePresent> Drain();

    /// <summary>Name for a process the source invents itself (demo mode); null for real processes.</summary>
    string? ProcessName(int processId) => null;
}

/// <summary>
/// Listens to the "present" events DirectX and the graphics kernel write to Event Tracing for Windows,
/// the same technique PresentMon uses. Nothing is injected into the game, so anti-cheat software is not
/// involved, but Windows only allows ETW sessions for administrators.
/// </summary>
/// <param name="sessionName">Windows keeps one session per name: the recorder and a measured session each use their own.</param>
public sealed class EtwFrameSource(string sessionName = "CHC-FrameCapture") : IFrameSource
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const int DxgiPresentStart = 42;
    private const int DxgiPresentMultiplaneOverlayStart = 55;
    private const int D3D9PresentStart = 1;

    /// <summary>DxgKrnl: PresentHistory (start), Present and PresentHistoryDetailed (start), all under the "Present" keyword.</summary>
    private static readonly int[] KernelPresents = [171, 184, 215];
    private const ulong KernelPresentKeyword = 0x8000000;

    private readonly ConcurrentQueue<FramePresent> _queue = new();
    private readonly KernelPresentFilter _kernel = new();
    private TraceEventSession? _session;
    private Thread? _thread;

    public bool Start(out string? error)
    {
        error = null;
        if (TraceEventSession.IsElevated() != true)
        {
            error = "admin";
            return false;
        }
        try
        {
            // Create replaces a session left behind by a previous run that did not exit cleanly.
            _session = new TraceEventSession(sessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Verbose, ulong.MaxValue);
            _session.EnableProvider(D3D9, TraceEventLevel.Verbose, ulong.MaxValue);
            // Vulkan and OpenGL games skip DirectX's runtime; only the kernel sees their frames.
            // Only these three event ids are switched on, so the very busy kernel provider stays cheap.
            try { _session.EnableProvider(DxgKrnl, TraceEventLevel.Verbose, KernelPresentKeyword, new TraceEventProviderOptions { EventIDsToEnable = KernelPresents }); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            _session.Source.AllEvents += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session?.Source.Process(); }
                catch (Exception) { /* The session was stopped. */ }
            })
            { IsBackground = true, Name = "CHC frame capture" };
            _thread.Start();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            error = ex.Message;
            Dispose();
            return false;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        var id = (int)e.ID;
        if ((e.ProviderGuid == Dxgi && id is DxgiPresentStart or DxgiPresentMultiplaneOverlayStart) || (e.ProviderGuid == D3D9 && id == D3D9PresentStart))
        {
            _kernel.Runtime(e.ProcessID);
            _queue.Enqueue(new FramePresent(e.ProcessID, e.TimeStampRelativeMSec));
        }
        else if (e.ProviderGuid == DxgKrnl && _kernel.Accept(e.ProcessID, id))
        {
            _queue.Enqueue(new FramePresent(e.ProcessID, e.TimeStampRelativeMSec));
        }
    }

    public IReadOnlyList<FramePresent> Drain()
    {
        var list = new List<FramePresent>(_queue.Count);
        while (_queue.TryDequeue(out var frame)) list.Add(frame);
        return list;
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        _thread?.Join(1000);
        _thread = null;
    }
}

/// <summary>
/// Picks the kernel present events that stand for frames, like PresentMon's "Other" runtime (Vulkan, OpenGL).
/// One present can log several kernel events, so each program's frames come from a single event type: the
/// first one seen 30 times. Programs that present through DirectX are counted from DirectX's own events,
/// which come first in every present, so their kernel events are ignored. Called from one thread only.
/// </summary>
public sealed class KernelPresentFilter
{
    private const int EventsToPick = 30;
    private readonly HashSet<int> _runtime = [];
    private readonly Dictionary<int, int> _chosen = [];
    private readonly Dictionary<(int Process, int Id), int> _counts = [];

    /// <summary>This program presents through DirectX.</summary>
    public void Runtime(int processId)
    {
        if (_runtime.Add(processId)) _chosen.Remove(processId);
    }

    /// <summary>True when this kernel event is a frame of a program that DirectX events don't cover.</summary>
    public bool Accept(int processId, int eventId)
    {
        if (_runtime.Contains(processId)) return false;
        if (_chosen.TryGetValue(processId, out var chosen)) return chosen == eventId;
        var count = _counts[(processId, eventId)] = _counts.GetValueOrDefault((processId, eventId)) + 1;
        if (count < EventsToPick) return false;
        _chosen[processId] = eventId;
        foreach (var key in _counts.Keys.Where(k => k.Process == processId).ToList()) _counts.Remove(key);
        return true;
    }
}
