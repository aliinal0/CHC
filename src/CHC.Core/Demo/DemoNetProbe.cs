using System.Diagnostics;
using CHC.Core.Network;

namespace CHC.Core.Demo;

/// <summary>
/// Made-up network for demo mode: a steady wired connection, until a game launcher starts a
/// download about 20 seconds in and the ping climbs, so the analysis has something to explain.
/// </summary>
public sealed class DemoNetProbe : INetProbe
{
    private const double DownloadStart = 20, DownloadEnd = 34, DownloadMbps = 86;
    private readonly Random _random = new(7);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();

    private double Now => _clock.Elapsed.TotalSeconds;
    private bool Downloading => Now is >= DownloadStart and < DownloadEnd;

    public Task<double?> PingAsync(string target, int timeoutMs)
    {
        lock (_sync)
        {
            var noise = Math.Abs(Gaussian());
            double? ms = target switch
            {
                NetworkMonitor.InternetTarget => 13.5 + noise * 1.4,
                NetworkMonitor.SecondaryTarget => 17 + noise * 1.6,
                DemoGameTraffic.ServerAddress => 31 + noise * 1.8,
                _ => 0.5 + noise * 0.2,
            };
            if (target is NetworkMonitor.InternetTarget or NetworkMonitor.SecondaryTarget or DemoGameTraffic.ServerAddress && Downloading)
            {
                ms += 38 + _random.NextDouble() * 30;
                if (_random.NextDouble() < 0.03) ms = null;
            }
            return Task.FromResult(ms);
        }
    }

    public Task<double?> LookupAsync()
    {
        lock (_sync) return Task.FromResult<double?>(16 + _random.NextDouble() * 9);
    }

    public (ulong Down, ulong Up) Counters()
    {
        // Background chatter of ~0.3 Mbit/s, plus the download while it runs.
        var t = Now;
        var downloadSeconds = Math.Clamp(t - DownloadStart, 0, DownloadEnd - DownloadStart);
        var down = (t * 0.3 + downloadSeconds * DownloadMbps) * 1_000_000 / 8;
        var up = (t * 0.08 + downloadSeconds * 1.6) * 1_000_000 / 8;
        return ((ulong)down, (ulong)up);
    }

    public bool StartTraffic() => true;

    public IReadOnlyList<NetProgramUsage> Traffic()
    {
        var downloadSeconds = Math.Clamp(Now - DownloadStart, 0, DownloadEnd - DownloadStart);
        var minutes = Now / 60;
        var list = new List<NetProgramUsage>
        {
            new("steam.exe", (ulong)(downloadSeconds * DownloadMbps * 1_000_000 / 8), (ulong)(downloadSeconds * 1.5 * 1_000_000 / 8)),
            new("Discord.exe", (ulong)(minutes * 1.9 * 1024 * 1024), (ulong)(minutes * 0.6 * 1024 * 1024)),
            new("chrome.exe", (ulong)(minutes * 0.8 * 1024 * 1024), (ulong)(minutes * 0.2 * 1024 * 1024)),
            new("svchost.exe", (ulong)(minutes * 0.3 * 1024 * 1024), (ulong)(minutes * 0.1 * 1024 * 1024)),
        };
        return list.Where(p => p.BytesDown + p.BytesUp > 0).OrderByDescending(p => p.BytesDown + p.BytesUp).ToList();
    }

    private double Gaussian()
    {
        var u1 = 1 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public void Dispose() { }
}
