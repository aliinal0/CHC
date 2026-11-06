using System.Diagnostics;
using CHC.Core.Performance;

namespace CHC.Core.Demo;

/// <summary>
/// Made-up frame times for demo mode: a game swinging between roughly 150 and 230 FPS
/// (busy scenes are CPU limited) that hitches now and then.
/// </summary>
public sealed class DemoFrameSource : IFrameSource
{
    public const int GameProcessId = 424242;
    public const string GameName = "DemoGame.exe";

    private readonly Random _random = new(77);
    private readonly Stopwatch _clock = new();
    private double _lastFrame;

    public bool Start(out string? error)
    {
        error = null;
        _clock.Restart();
        _lastFrame = 0;
        return true;
    }

    public IReadOnlyList<FramePresent> Drain()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var frames = new List<FramePresent>();
        while (true)
        {
            // A slow wave between busy and quiet scenes, a little jitter, and an occasional 40-70 ms hitch.
            var fps = 190 + 40 * Math.Sin(_lastFrame / 7000) + (_random.NextDouble() - 0.5) * 20;
            var frame = _random.NextDouble() < 0.00025 ? 40 + _random.NextDouble() * 30 : 1000 / fps;
            if (_lastFrame + frame > now) break;
            _lastFrame += frame;
            frames.Add(new FramePresent(GameProcessId, _lastFrame));
        }
        return frames;
    }

    public string? ProcessName(int processId) => processId == GameProcessId ? GameName : null;

    /// <summary>Sample background programs for the demo session (real ones are never shown in demo mode).</summary>
    public static List<BackgroundUsage> Background() =>
    [
        new("Discord.exe", 2.4, 6.1),
        new("DemoLauncher.exe", 1.6, 9.8),
        new("OneDrive.exe", 1.1, 4.0),
    ];

    public void Dispose() => _clock.Stop();
}
