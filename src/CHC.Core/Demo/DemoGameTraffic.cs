using System.Diagnostics;
using System.Net;
using CHC.Core.Network;

namespace CHC.Core.Demo;

/// <summary>
/// Made-up traffic for demo mode: the demo game talks to a match server (a documentation address)
/// at about 40 KB/s, so the game session shows how online games are measured.
/// </summary>
public sealed class DemoGameTraffic : IGameTraffic
{
    /// <summary>TEST-NET-3, reserved for documentation: never a real server.</summary>
    public const string ServerAddress = "203.0.113.25";

    private readonly Stopwatch _clock = new();

    public bool MeasuresBytes => true;

    public bool Start()
    {
        _clock.Restart();
        return true;
    }

    private ulong GameBytes => (ulong)(_clock.Elapsed.TotalSeconds * 40_000);

    public IReadOnlyList<(IPAddress Address, ulong Bytes)> Peers(int processId) =>
        processId == DemoFrameSource.GameProcessId ? [(IPAddress.Parse(ServerAddress), GameBytes)] : [];

    public ulong Bytes(int processId) => processId == DemoFrameSource.GameProcessId ? GameBytes : 0;

    public IReadOnlyList<NetProgramUsage> Programs()
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        var download = Math.Clamp(seconds - 20, 0, 14) * 86 * 1_000_000 / 8;
        return new List<NetProgramUsage>
            {
                new("steam.exe", (ulong)download, (ulong)(download / 50)),
                new(DemoFrameSource.GameName, GameBytes * 3 / 5, GameBytes * 2 / 5),
                new("Discord.exe", (ulong)(seconds * 30_000), (ulong)(seconds * 10_000)),
            }
            .Where(p => p.BytesDown + p.BytesUp > 0)
            .OrderByDescending(p => p.BytesDown + p.BytesUp)
            .ToList();
    }

    public void Dispose() => _clock.Stop();
}
