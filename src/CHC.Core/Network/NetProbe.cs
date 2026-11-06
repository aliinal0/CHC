using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using CHC.Core.Collection;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace CHC.Core.Network;

/// <summary>Where a network test gets its measurements: the real network, or the demo generator.</summary>
public interface INetProbe : IDisposable
{
    /// <summary>Round trip in ms, or null if the ping was lost.</summary>
    Task<double?> PingAsync(string target, int timeoutMs);

    /// <summary>Time of an uncached DNS lookup in ms, or null if it failed.</summary>
    Task<double?> LookupAsync();

    /// <summary>Bytes received and sent by the adapter so far.</summary>
    (ulong Down, ulong Up) Counters();

    /// <summary>Starts counting traffic per program; false when that needs administrator rights.</summary>
    bool StartTraffic();

    IReadOnlyList<NetProgramUsage> Traffic();
}


/// <summary>Pings with ICMP (no administrator rights needed) and reads the adapter's byte counters.</summary>
/// <param name="measurePrograms">Count traffic per program (false when the caller already does, e.g. during a game session).</param>
public sealed class RealNetProbe(string adapterId, bool measurePrograms = true) : INetProbe
{
    private const string LookupName = "www.microsoft.com";
    private readonly NetworkInterface? _adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapterId);
    private EtwNetTraffic? _traffic;

    public async Task<double?> PingAsync(string target, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    public Task<double?> LookupAsync() => Task.Run(() => Native.DnsLookupMs(LookupName));

    public (ulong Down, ulong Up) Counters()
    {
        try
        {
            var stats = _adapter?.GetIPStatistics();
            return stats is null ? (0, 0) : ((ulong)stats.BytesReceived, (ulong)stats.BytesSent);
        }
        catch (NetworkInformationException) { return (0, 0); }
    }

    public bool StartTraffic()
    {
        if (!measurePrograms) return false;
        _traffic = new EtwNetTraffic("CHC-NetTraffic");
        if (_traffic.Start()) return true;
        _traffic.Dispose();
        _traffic = null;
        return false;
    }

    public IReadOnlyList<NetProgramUsage> Traffic() => _traffic?.Totals() ?? [];

    public void Dispose()
    {
        _traffic?.Dispose();
        _traffic = null;
    }
}

/// <summary>
/// Bytes sent and received per program and per remote address, from the Microsoft-Windows-Kernel-Network
/// ETW provider. Every TCP/UDP event starts with the owning process id and the size (two UInt32 values),
/// followed by the destination and source address (4 bytes each for IPv4, 16 for IPv6).
/// </summary>
internal sealed class EtwNetTraffic(string sessionName) : IDisposable
{
    private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");

    // IPv4/IPv6 TCP and UDP send and receive events.
    private static readonly HashSet<int> Sends = [10, 26, 42, 58];
    private static readonly HashSet<int> Receives = [11, 27, 43, 59];
    private static readonly HashSet<int> IPv6 = [26, 27, 58, 59];

    private readonly ConcurrentDictionary<int, (long Down, long Up)> _bytes = new();
    private readonly ConcurrentDictionary<(int Pid, UInt128 Address), long> _peers = new();
    private readonly ConcurrentDictionary<int, string> _names = new();
    private TraceEventSession? _session;
    private Thread? _thread;

    public bool Start()
    {
        if (TraceEventSession.IsElevated() != true) return false;
        try
        {
            _session = new TraceEventSession(sessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
            _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x30);
            _session.Source.AllEvents += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session?.Source.Process(); }
                catch (Exception) { /* The session was stopped. */ }
            })
            { IsBackground = true, Name = "CHC network traffic" };
            _thread.Start();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Dispose();
            return false;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        if (e.ProviderGuid != KernelNetwork) return;
        var id = (int)e.ID;
        var send = Sends.Contains(id);
        if (!send && !Receives.Contains(id)) return;
        var data = e.EventData();
        if (data is null || data.Length < 8) return;
        var pid = BitConverter.ToInt32(data, 0);
        var size = BitConverter.ToUInt32(data, 4);
        if (pid <= 0 || size > 16 * 1024 * 1024) return;
        _names.GetOrAdd(pid, Name);
        _bytes.AddOrUpdate(pid, send ? (0, size) : (size, 0), (_, t) => send ? (t.Down, t.Up + size) : (t.Down + size, t.Up));

        // The remote end is the public one of the two addresses (the PC itself is normally behind a router).
        var length = IPv6.Contains(id) ? 16 : 4;
        if (data.Length < 8 + 2 * length) return;
        var first = new IPAddress(data.AsSpan(8, length));
        var second = new IPAddress(data.AsSpan(8 + length, length));
        var remote = GameNetwork.IsPublic(first) ? first : GameNetwork.IsPublic(second) ? second : null;
        if (remote is null) return;
        var key = (pid, Key(remote));
        _peers.AddOrUpdate(key, size, (_, total) => total + size);
    }

    private static UInt128 Key(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.MapToIPv6().TryWriteBytes(bytes, out _);
        return new UInt128(BitConverter.ToUInt64(bytes[..8]), BitConverter.ToUInt64(bytes[8..]));
    }

    private static IPAddress Address(UInt128 key)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), (ulong)(key >> 64));
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), (ulong)key);
        var address = new IPAddress(bytes);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static string Name(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return $"PID {pid}"; }
    }

    /// <summary>All processes of a program added together, busiest first.</summary>
    public IReadOnlyList<NetProgramUsage> Totals() => _bytes
        .GroupBy(b => _names.GetValueOrDefault(b.Key, $"PID {b.Key}"), StringComparer.OrdinalIgnoreCase)
        .Select(g => new NetProgramUsage(g.Key, (ulong)g.Sum(x => x.Value.Down), (ulong)g.Sum(x => x.Value.Up)))
        .OrderByDescending(p => p.BytesDown + p.BytesUp)
        .Take(10)
        .ToList();

    /// <summary>Bytes the process exchanged with the internet so far.</summary>
    public ulong BytesOf(int pid) => _bytes.TryGetValue(pid, out var b) ? (ulong)(b.Down + b.Up) : 0;

    /// <summary>Public addresses the process exchanged data with, busiest first.</summary>
    public IReadOnlyList<(IPAddress Address, ulong Bytes)> PeersOf(int pid) => _peers
        .Where(p => p.Key.Pid == pid)
        .OrderByDescending(p => p.Value)
        .Select(p => (Address(p.Key.Address), (ulong)p.Value))
        .ToList();

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        _thread?.Join(1000);
        _thread = null;
    }
}
