using System.Diagnostics;
using System.Net;
using CHC.Core.Collection;
using CHC.Core.Network;
using CHC.Core.Sensors;

namespace CHC.Core.Performance;

/// <summary>What the app shows every second while measuring.</summary>
public sealed record PerfLiveState(
    string? Game,
    TimeSpan Elapsed,
    bool FpsAvailable,
    double? Fps,
    double? FrameTimeMs,
    double? OnePercentLowFps,
    int Stutters,
    PerfSample Sample,
    bool Online = false,
    double? PingMs = null,
    double? LossPercent = null);

/// <summary>
/// Records a game session: frame times from an <see cref="IFrameSource"/> and, once per second,
/// the sensors and background programs that explain them. When the game turns out to be online,
/// a <see cref="NetworkMonitor"/> measures ping, jitter and loss alongside. Thread safe; ticks on a timer thread.
/// </summary>
public sealed class PerformanceMonitor : IDisposable
{
    private const int MinPresentsForGame = 15;

    private readonly Func<SensorSnapshot> _readSensors;
    private readonly Func<IFrameSource> _createFrameSource;
    private readonly Func<IGameTraffic>? _createTraffic;
    private readonly Func<NetworkMonitor>? _createNetwork;
    private readonly Func<Models.BatteryState?> _readBattery;
    private readonly bool _scanProcesses;
    private readonly object _sync = new();
    private readonly int _ownPid = Environment.ProcessId;

    private readonly Dictionary<int, double> _lastPresent = [];
    private readonly Dictionary<int, List<double>> _frameTimes = [];
    private readonly Dictionary<int, int> _foregroundSeconds = [];
    private readonly Dictionary<int, bool> _desktop = [];
    private readonly List<double> _window = [];
    private readonly Dictionary<int, TimeSpan> _processCpu = [];
    private readonly Dictionary<string, (double Sum, double Peak)> _background = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = new();

    private IFrameSource? _frames;
    private Timer? _timer;
    private PerfSession? _session;
    private int _target;
    private double _median = 16.7;
    private double _lastTick;
    private int _processScans;
    private DateTime _lastProcessScan;
    private double _lastDisplayCheck;
    private string _windowTitle = "";

    private IGameTraffic? _traffic;
    private NetworkMonitor? _net;
    private NetworkInfo? _network;
    private bool _laptop;
    private volatile NetLiveState? _lastNet;
    private double _lastOnlineCheck;
    private readonly Dictionary<IPAddress, ulong> _peerBytes = [];
    private IPAddress? _candidate;
    private int _candidateVotes;

    /// <param name="scanProcesses">Measure other programs' CPU use (off in demo mode, which must not show real programs).</param>
    /// <param name="createTraffic">Watches the game's traffic to notice that it is online; null to never measure the network.</param>
    /// <param name="createNetwork">Creates the ping test started for online games.</param>
    /// <param name="readBattery">Reads the laptop battery every second; null reads the real one (none on desktops).</param>
    public PerformanceMonitor(Func<SensorSnapshot> readSensors, Func<IFrameSource> createFrameSource, bool scanProcesses = true,
        Func<IGameTraffic>? createTraffic = null, Func<NetworkMonitor>? createNetwork = null, Func<Models.BatteryState?>? readBattery = null)
    {
        _readBattery = readBattery ?? (() => Native.Battery()?.State);
        _readSensors = readSensors;
        _createFrameSource = createFrameSource;
        _scanProcesses = scanProcesses;
        _createTraffic = createTraffic;
        _createNetwork = createNetwork;
    }

    public event EventHandler<PerfLiveState>? Updated;

    public bool IsRunning { get; private set; }

    /// <summary>Why frame times are not available ("admin" or an error message), or null.</summary>
    public string? FrameError { get; private set; }

    /// <param name="network">The connection to test if the game turns out to be online.</param>
    /// <param name="laptop">Record the battery every second (a desktop's "battery" is a UPS and is left out).</param>
    public void Start(int refreshRateHz, NetworkInfo? network = null, bool laptop = false)
    {
        lock (_sync)
        {
            if (IsRunning) return;
            Reset();
            _frames = _createFrameSource();
            if (!_frames.Start(out var error))
            {
                FrameError = error;
                _frames.Dispose();
                _frames = null;
            }
            _session = new PerfSession { Started = DateTime.Now, RefreshRateHz = refreshRateHz, FpsMeasured = _frames is not null };
            _network = network;
            _laptop = laptop;
            if (network?.Physical is not null && _createTraffic is not null)
            {
                // Without administrator rights this only sees TCP connections, which is still enough for most online games.
                _traffic = _createTraffic();
                _traffic.Start();
            }
            _clock.Restart();
            _lastProcessScan = DateTime.UtcNow;
            if (_scanProcesses) ScanProcesses(record: false);
            IsRunning = true;
            _timer = new Timer(_ => Tick(), null, 1000, 1000);
        }
    }

    public PerfSession? Stop()
    {
        Timer? timer;
        string? invented;
        NetworkMonitor? net;
        IGameTraffic? traffic;
        lock (_sync)
        {
            if (!IsRunning || _session is null) return null;
            IsRunning = false;
            timer = _timer;
            _timer = null;
            net = _net;
            _net = null;
            traffic = _traffic;
            _traffic = null;
        }
        timer?.Dispose();
        var netSession = net?.Stop();
        net?.Dispose();
        lock (_sync)
        {
            DrainFrames();
            Finish(_session);
            if (traffic is not null)
            {
                _session.GameBytes = traffic.Bytes(_target);
                if (netSession is not null)
                {
                    netSession.Programs = traffic.Programs().ToList();
                    netSession.ProgramsMeasured = traffic.MeasuresBytes;
                }
                traffic.Dispose();
            }
            _session.Network = netSession;
            invented = _frames?.ProcessName(_target);
            if (invented is not null) _session.Game = invented;
            else _session.Settings = GameSettingsReader.Read(_session.GamePath, _windowTitle);
            _frames?.Dispose();
            _frames = null;
            return _session;
        }
    }

    private void Reset()
    {
        _lastPresent.Clear();
        _frameTimes.Clear();
        _foregroundSeconds.Clear();
        _desktop.Clear();
        _window.Clear();
        _processCpu.Clear();
        _background.Clear();
        _target = 0;
        _median = 16.7;
        _lastTick = 0;
        _processScans = 0;
        FrameError = null;
        _lastNet = null;
        _lastOnlineCheck = 0;
        _lastDisplayCheck = 0;
        _windowTitle = "";
        _peerBytes.Clear();
        _candidate = null;
        _candidateVotes = 0;
    }

    private void Tick()
    {
        PerfLiveState state;
        lock (_sync)
        {
            if (!IsRunning || _session is null) return;
            var now = _clock.Elapsed.TotalSeconds;
            var seconds = Math.Max(0.2, now - _lastTick);
            _lastTick = now;

            var counts = DrainFrames();
            PickGame(counts);

            SensorSnapshot sensors;
            try { sensors = _readSensors(); }
            catch { sensors = SensorSnapshot.Empty; }
            var sample = SampleFrom(sensors, now);
            Models.BatteryState? battery = null;
            if (_laptop)
            {
                try { battery = _readBattery(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            if (battery is not null)
            {
                sample.OnBattery = !battery.OnAc;
                sample.BatteryPercent = battery.Percent;
                sample.BatteryRateW = battery.RateW;
            }

            var stutters = 0;
            if (_target != 0 && counts.TryGetValue(_target, out var presents))
            {
                sample.Fps = presents / seconds;
                var times = _frameTimes[_target];
                var thisSecond = times.Skip(Math.Max(0, times.Count - presents)).ToList();
                sample.WorstFrameMs = thisSecond.Count > 0 ? thisSecond.Max() : null;
                stutters = thisSecond.Count(IsStutter);
                for (var i = 0; i < stutters; i++) _session.StutterTimes.Add(DateTime.Now);
                UpdateMedian(thisSecond);
            }
            sample.Stutters = stutters;
            if (now - _lastDisplayCheck >= 5 && GamePid() is var game and not 0)
            {
                _lastDisplayCheck = now;
                CheckDisplay(game);
            }
            if (_traffic is not null && _target != 0 && now - _lastOnlineCheck >= 3)
            {
                _lastOnlineCheck = now;
                CheckOnline();
            }
            if (_lastNet is { } ping)
            {
                sample.PingMs = ping.Sample.InternetMs;
                sample.ServerPingMs = ping.Sample.ServerMs;
            }
            _session.Samples.Add(sample);

            if (_scanProcesses && (DateTime.UtcNow - _lastProcessScan).TotalSeconds >= 2) ScanProcesses(record: true);

            var all = _target != 0 ? _frameTimes[_target] : null;
            state = new PerfLiveState(
                GameName(),
                _clock.Elapsed,
                _session.FpsMeasured,
                sample.Fps,
                all is { Count: > 0 } ? all.TakeLast(Math.Max(1, (int)(sample.Fps ?? 1))).Average() : null,
                all is { Count: >= 100 } ? 1000 / WorstAverage(all, 0.01) : null,
                _session.StutterTimes.Count,
                sample,
                _session.Online,
                _lastNet is { } live ? live.ServerMs ?? live.InternetMs : null,
                _lastNet?.LossPercent);
        }
        Updated?.Invoke(this, state);
    }

    /// <summary>
    /// Online games exchange data with internet servers all the time. Once that is seen, the ping test starts;
    /// the address with the most traffic in the last seconds is the game server (it can change from menu to match).
    /// </summary>
    private void CheckOnline()
    {
        var peers = _traffic!.Peers(_target);
        if (!_session!.Online)
        {
            var online = _traffic.MeasuresBytes ? peers.Sum(p => (double)p.Bytes) >= 20_000 : peers.Count > 0;
            if (!online || _network is null || _createNetwork is null) return;
            _session.Online = true;
            _net = _createNetwork();
            _net.Updated += (_, state) => _lastNet = state;
            _net.Start(_network);
        }
        if (!_traffic.MeasuresBytes) return;

        (IPAddress Address, ulong Delta) busiest = default;
        foreach (var (address, bytes) in peers)
        {
            var delta = bytes - _peerBytes.GetValueOrDefault(address);
            _peerBytes[address] = bytes;
            if (delta > busiest.Delta) busiest = (address, delta);
        }
        if (busiest.Address is null || busiest.Delta < 5_000 || busiest.Address.ToString() == _session.ServerAddress) return;
        // Switch only when another address keeps carrying the traffic, not after one burst (a download, a login).
        _candidateVotes = Equals(_candidate, busiest.Address) ? _candidateVotes + 1 : 1;
        _candidate = busiest.Address;
        if (_session.ServerAddress.Length > 0 && _candidateVotes < 3) return;
        _session.ServerAddress = busiest.Address.ToString();
        _net?.SetServer(_session.ServerAddress);
    }

    /// <summary>
    /// While the game is in front: where its exe is (for its settings file), its window (fullscreen, borderless or
    /// a window) and the monitor's mode, which exclusive fullscreen can change to a lower resolution or refresh rate.
    /// </summary>
    private void CheckDisplay(int pid)
    {
        if (_frames?.ProcessName(pid) is not null) return;
        if (Native.ProcessPath(pid) is { } path) _session!.GamePath = path;
        if (Native.ForegroundWindowOf(pid) is not { } window) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            _windowTitle = p.MainWindowTitle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        if (Native.DisplayModes(window.Device) is not { } mode) return;
        _session!.Display = new GameDisplay
        {
            WindowWidth = window.Width,
            WindowHeight = window.Height,
            CoversMonitor = window.CoversMonitor,
            HasTitleBar = window.HasTitleBar,
            ModeWidth = mode.Width,
            ModeHeight = mode.Height,
            RefreshHz = mode.Current,
            NativeWidth = mode.NativeWidth,
            NativeHeight = mode.NativeHeight,
            MaxRefreshHz = mode.NativeMax,
        };
    }

    /// <summary>Turns queued presents into per-process frame times; returns presents per process for this tick.</summary>
    private Dictionary<int, int> DrainFrames()
    {
        var counts = new Dictionary<int, int>();
        if (_frames is null) return counts;
        foreach (var f in _frames.Drain())
        {
            // The desktop composer draws at the refresh rate in windowed games; it must never count as the game.
            if (f.ProcessId == _ownPid || IsDesktop(f.ProcessId)) continue;
            counts[f.ProcessId] = counts.GetValueOrDefault(f.ProcessId) + 1;
            if (_lastPresent.TryGetValue(f.ProcessId, out var last))
            {
                var delta = f.TimeMs - last;
                if (delta is > 0 and < 2000)
                {
                    if (!_frameTimes.TryGetValue(f.ProcessId, out var list)) _frameTimes[f.ProcessId] = list = [];
                    list.Add(delta);
                }
            }
            _lastPresent[f.ProcessId] = f.TimeMs;
        }
        return counts;
    }

    /// <summary>The game is the program in front that draws frames; without frame data, the foreground program.</summary>
    private void PickGame(Dictionary<int, int> counts)
    {
        var foreground = Native.ForegroundProcessId();
        if (foreground != 0 && foreground != _ownPid)
            _foregroundSeconds[foreground] = _foregroundSeconds.GetValueOrDefault(foreground) + 1;

        var drawing = counts.Where(c => c.Value >= MinPresentsForGame).ToList();
        if (drawing.Count == 0) return;
        var best = drawing.Any(c => c.Key == foreground) ? foreground : drawing.MaxBy(c => c.Value).Key;
        if (_target == 0 || !counts.ContainsKey(_target) || counts[_target] < MinPresentsForGame / 3)
            _target = best;
    }

    /// <summary>The game: the program drawing frames or, without frame data, the program longest in front (not the desktop).</summary>
    private int GamePid() => _target != 0 ? _target
        : _foregroundSeconds.Where(f => f.Key != _ownPid && !IsDesktop(f.Key)).OrderByDescending(f => f.Value).Select(f => f.Key).FirstOrDefault();

    /// <summary>Checked once per process, while it is still running (it just drew a frame).</summary>
    private bool IsDesktop(int pid)
    {
        if (!_desktop.TryGetValue(pid, out var desktop))
            _desktop[pid] = desktop = _frames?.ProcessName(pid) is null && IsDesktopProcess(pid);
        return desktop;
    }

    private static bool IsDesktopProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("dwm", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>A frame that took far longer than the frames around it: felt as a hitch.</summary>
    private bool IsStutter(double frameMs) => frameMs > 33 && frameMs > Math.Max(_median * 2.5, _median + 16);

    private void UpdateMedian(List<double> recent)
    {
        _window.AddRange(recent);
        if (_window.Count > 240) _window.RemoveRange(0, _window.Count - 240);
        if (_window.Count > 0) _median = Percentile(_window, 50);
    }

    private void ScanProcesses(bool record)
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Max(0.5, (now - _lastProcessScan).TotalSeconds);
        _lastProcessScan = now;
        var cores = Environment.ProcessorCount;
        var seen = new HashSet<int>();
        var usage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4 || p.Id == _ownPid || p.Id == _target) continue;
                try
                {
                    var cpu = p.TotalProcessorTime;
                    seen.Add(p.Id);
                    if (record && _processCpu.TryGetValue(p.Id, out var before))
                    {
                        var percent = (cpu - before).TotalSeconds / elapsed / cores * 100;
                        if (percent > 0) usage[p.ProcessName] = usage.GetValueOrDefault(p.ProcessName) + percent;
                    }
                    _processCpu[p.Id] = cpu;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        foreach (var gone in _processCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _processCpu.Remove(gone);
        if (!record) return;

        _processScans++;
        foreach (var (name, percent) in usage)
        {
            var (sum, peak) = _background.GetValueOrDefault(name);
            _background[name] = (sum + percent, Math.Max(peak, percent));
        }
    }

    private string? GameName()
    {
        var pid = _target != 0 ? _target : _foregroundSeconds.Where(f => f.Key != _ownPid).OrderByDescending(f => f.Value).Select(f => f.Key).FirstOrDefault();
        if (pid == 0) return null;
        if (_frames?.ProcessName(pid) is { } invented) return invented;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private void Finish(PerfSession session)
    {
        session.Ended = DateTime.Now;

        // The game is the program with the most frames over the whole session.
        var game = _frameTimes.OrderByDescending(f => f.Value.Count).FirstOrDefault();
        if (game.Value is { Count: >= 30 })
        {
            _target = game.Key;
            var frames = game.Value;
            session.Frames = frames.Count;
            session.AverageFps = 1000 / frames.Average();
            session.MedianFrameMs = Percentile(frames, 50);
            session.OnePercentLowFps = 1000 / WorstAverage(frames, 0.01);
            session.PointOnePercentLowFps = 1000 / WorstAverage(frames, 0.001);
        }
        else
        {
            session.FpsMeasured = false;
        }

        session.Game = GameName() ?? "";
        var pid = _target != 0 ? _target : _foregroundSeconds.OrderByDescending(f => f.Value).Select(f => f.Key).FirstOrDefault();
        // The path seen while playing stays when the game has already been closed.
        if (Native.ProcessPath(pid) is { } path) session.GamePath = path;

        var scans = Math.Max(1, _processScans);
        session.Background = _background
            .Where(b => !b.Key.Equals(Path.GetFileNameWithoutExtension(session.Game), StringComparison.OrdinalIgnoreCase))
            .Select(b => new BackgroundUsage(b.Key + ".exe", b.Value.Sum / scans, b.Value.Peak))
            .Where(b => b.AverageCpu >= 1)
            .OrderByDescending(b => b.AverageCpu)
            .Take(8)
            .ToList();
    }

    public static PerfSample SampleFrom(SensorSnapshot s, double second)
    {
        var cpu = s.OfKind("Cpu").FirstOrDefault();
        // NVIDIA first: laptops often pair it with AMD graphics built into the processor.
        var gpu = s.Devices.FirstOrDefault(d => d.Kind == "GpuNvidia") ?? s.Devices.FirstOrDefault(d => d.Kind == "GpuAmd") ?? s.OfKind("Gpu").FirstOrDefault();
        var ram = s.Device("/ram");
        return new PerfSample
        {
            Second = second,
            CpuLoad = cpu.Find("Load", "CPU Total")?.Value,
            CpuMaxCoreLoad = cpu.Find("Load", "CPU Core Max")?.Value,
            CpuTemp = SystemCollector.CpuTemperature(cpu).Positive(),
            CpuClock = cpu.Find("Clock", "Cores (Average Effective)", "Cores (Average)").Positive(),
            GpuLoad = gpu.Find("Load", "GPU Core", "D3D 3D")?.Value,
            GpuTemp = gpu.Find("Temperature", "GPU Core").Positive(),
            GpuHotSpot = gpu.Find("Temperature", "GPU Hot Spot").Positive(),
            GpuClock = gpu.Find("Clock", "GPU Core").Positive(),
            GpuPowerPercent = gpu.Find("Load", "GPU Power", "GPU Board Power")?.Value,
            VramUsedMb = gpu.Find("SmallData", "GPU Memory Used", "D3D Dedicated Memory Used")?.Value,
            VramTotalMb = gpu.Find("SmallData", "GPU Memory Total").Positive(),
            RamLoad = ram.Find("Load", "Memory")?.Value,
            DiskActivity = s.OfKind("Storage").Select(d => (double?)d.Find("Load", "Total Activity")?.Value).Max(),
            CpuPowerW = cpu.Find("Power", "Package", "CPU Package").Positive(),
            GpuPowerW = gpu.Find("Power", "GPU Package", "GPU Power", "GPU Core").Positive(),
        };
    }

    /// <summary>
    /// Average of the slowest frames ("1% low" = the slowest 1%), the way FPS overlays report it.
    /// Unlike a percentile it still notices a handful of long hitches.
    /// </summary>
    public static double WorstAverage(IReadOnlyList<double> frameTimes, double fraction)
    {
        if (frameTimes.Count == 0) return 0;
        var take = Math.Max(1, (int)Math.Ceiling(frameTimes.Count * fraction));
        return frameTimes.OrderByDescending(f => f).Take(take).Average();
    }

    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        var rank = percentile / 100 * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = Math.Min(sorted.Length - 1, low + 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    public void Dispose()
    {
        Stop();
    }
}
