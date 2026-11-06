using System.Net;
using System.Net.Sockets;
using CHC.Core.Collection;

namespace CHC.Core.Network;

/// <summary>Watches a game's own traffic to tell whether it is online and which server it plays on.</summary>
public interface IGameTraffic : IDisposable
{
    /// <summary>Starts counting traffic per program and address; false without administrator rights (TCP connections are still visible).</summary>
    bool Start();

    /// <summary>True when per-address byte counts are available (not just open connections).</summary>
    bool MeasuresBytes { get; }

    /// <summary>Public addresses the process talks to, busiest first (bytes are 0 when only connections are known).</summary>
    IReadOnlyList<(IPAddress Address, ulong Bytes)> Peers(int processId);

    /// <summary>Bytes the process sent and received so far (0 when unknown).</summary>
    ulong Bytes(int processId);

    /// <summary>Every program that used the network, busiest first.</summary>
    IReadOnlyList<NetProgramUsage> Programs();
}

public static class GameNetwork
{
    /// <summary>An address on the internet: not private, loopback, link-local, multicast or carrier-grade NAT.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] is >= 64 and <= 127));
        }
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
    }
}

/// <summary>
/// The game's traffic per address through ETW (administrator), and its TCP connections as a fallback:
/// most online games keep at least one TCP connection to their servers.
/// </summary>
public sealed class RealGameTraffic : IGameTraffic
{
    private EtwNetTraffic? _etw;

    public bool MeasuresBytes => _etw is not null;

    public bool Start()
    {
        _etw = new EtwNetTraffic("CHC-GameTraffic");
        if (_etw.Start()) return true;
        _etw.Dispose();
        _etw = null;
        return false;
    }

    public IReadOnlyList<(IPAddress Address, ulong Bytes)> Peers(int processId)
    {
        if (_etw?.PeersOf(processId) is { Count: > 0 } measured) return measured;
        return Native.TcpPeers(processId).Where(GameNetwork.IsPublic).Distinct().Select(a => (a, 0UL)).ToList();
    }

    public ulong Bytes(int processId) => _etw?.BytesOf(processId) ?? 0;

    public IReadOnlyList<NetProgramUsage> Programs() => _etw?.Totals() ?? [];

    public void Dispose()
    {
        _etw?.Dispose();
        _etw = null;
    }
}
