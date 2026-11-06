using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using CHC.Core.Collection;
using CHC.Core.Devices;
using CHC.Core.Diagnostics;
using CHC.Core.Performance;
using CHC.Core.Sensors;

namespace CHC.Core.History;

/// <summary>
/// Takes a reading every few seconds and writes one row per minute to the history. It keeps numbers and the file
/// name of the program in front (for what runs fullscreen also the folder of its exe, to find a game's settings file
/// and its frame rate), and once an hour what each program used in total; never window titles, what is typed, what is
/// on screen or which sites are visited.
/// </summary>
public sealed partial class HistoryRecorder : IDisposable
{
    private readonly HistoryStore _store;
    private readonly Func<SensorSnapshot> _read;
    private readonly object _sync = new();
    private readonly Dictionary<int, (string Name, string? Title, string? Path)?> _names = [];
    private readonly Func<IFrameSource> _createFrames;
    private Timer? _timer;
    private Minute? _minute;
    private (long Down, long Up)? _bytes;
    private DateTime _lastPrune = DateTime.MinValue;
    private DateTime _namesCleared = DateTime.UtcNow;
    private int _busy;
    private IFrameSource? _frames;
    private bool _framesUnavailable;
    private DateTime _lastPlayed;
    private int _framePid;
    private double _lastFrame;
    private readonly ProgramTracker _programs = new(new ProcessSampler());
    private readonly DeviceWatcher _devices = new();
    private int _programTicks;
    private int _frontPid;
    private string? _game;
    private DateTime _programsSaved;

    /// <param name="read">Gives the current sensor values (the shared sensor hub, so the app and the recorder read once).</param>
    /// <param name="createFrames">Where frame timings come from; ETW events by default.</param>
    public HistoryRecorder(HistoryStore store, Func<SensorSnapshot> read, Func<IFrameSource>? createFrames = null)
    {
        _store = store;
        _read = read;
        _createFrames = createFrames ?? (() => new EtwFrameSource("CHC-History"));
    }

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A laptop: battery charge is recorded, and the rest of the PC counts as 15 W instead of 60 W.</summary>
    public bool Laptop { get; set; } = Native.Battery() is not null;

    /// <summary>
    /// A separate graphics card: without its power reading no whole-PC figure is made (half a figure would mislead).
    /// Guessed from the sensors until the app sets it from a full scan.
    /// </summary>
    public bool DiscreteGpu
    {
        get => _discreteGpu ?? true;
        set => _discreteGpu = value;
    }

    private bool? _discreteGpu;

    /// <summary>Away from the keyboard this long (and nothing fullscreen), no program counts as in use.</summary>
    public TimeSpan AwayAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The frame timing stays on this long after a game, so alt-tabbing doesn't restart it.</summary>
    private static readonly TimeSpan FramesIdle = TimeSpan.FromMinutes(2);

    private const int ProgramEveryTicks = 5;
    private static readonly TimeSpan ProgramSaveInterval = TimeSpan.FromMinutes(5);

    /// <summary>A minute with fewer seconds of frames than this was mostly menus or alt-tabbing.</summary>
    private const double MinFrameMilliseconds = 20_000;

    public bool IsRunning => _timer is not null;

    /// <summary>The last write error (disk full, file locked), or null; recording carries on regardless.</summary>
    public string? LastError { get; private set; }

    public event EventHandler<MinuteRecord>? MinuteWritten;

    public void Start()
    {
        lock (_sync)
        {
            if (_timer is not null) return;
            _bytes = null;
            _devices.Start();
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, Interval);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_timer is null) return;
            _timer.Dispose();
            _timer = null;
            if (_minute is { Samples: > 0 } m) Save(m);
            _minute = null;
            StopFrames();
            Try(SavePrograms);
            Try(SaveDeviceEvents);
        }
    }

    private void Tick()
    {
        // A slow reading must not pile up ticks behind it.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            lock (_sync)
            {
                if (_timer is null) return;
                var now = DateTime.UtcNow;
                var start = new DateTime(now.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
                if (_minute is { } done && done.Start != start)
                {
                    if (done.Samples > 0) Save(done);
                    _minute = null;
                }
                var m = _minute ??= new Minute(start);
                // Each part on its own: a sensor that fails must not cost the minute its traffic or program.
                Try(() => ReadSensors(m));
                Try(() => ReadForeground(m));
                Try(() => ReadTraffic(m));
                Try(() => ReadPrograms(now));
                Try(SaveDeviceEvents);
                if (m.Samples == 0) Try(() => ReadWifi(start));
                if (Laptop)
                    Try(() =>
                    {
                        if (Native.Battery() is not { } battery) return;
                        m.OnBattery = !battery.State.OnAc;
                        m.Battery = battery.State.Percent ?? m.Battery;
                    });
                m.Samples++;
                if (now - _lastPrune > TimeSpan.FromHours(12))
                {
                    _lastPrune = now;
                    try { _store.Prune(); } catch (Exception ex) { LastError = ex.Message; }
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void Try(Action read)
    {
        try { read(); }
        catch (Exception ex) { LastError = ex.Message; }
    }

    private void Save(Minute m)
    {
        var record = m.ToRecord();
        try
        {
            _store.Write(record);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return;
        }
        MinuteWritten?.Invoke(this, record);
    }

    private void ReadSensors(Minute m)
    {
        var s = _read();
        if (s.Devices.Count == 0) return;
        _discreteGpu ??= HasDiscreteGpu(s);
        var x = PerformanceMonitor.SampleFrom(s, 0);
        m.CpuTemp.Add(x.CpuTemp);
        m.GpuTemp.Add(x.GpuTemp);
        m.CpuLoad.Add(x.CpuLoad);
        m.GpuLoad.Add(x.GpuLoad);
        m.Ram.Add(x.RamLoad);
        m.CpuPower.Add(x.CpuPowerW);
        m.GpuPower.Add(x.GpuPowerW);

        var psu = s.OfKind("Psu").FirstOrDefault();
        if (PsuOutput(psu) is { } measured)
        {
            m.SystemPower.Add(measured);
            m.Measured = true;
        }
        else if (x.CpuPowerW is { } cpu && (x.GpuPowerW is not null || !DiscreteGpu))
        {
            m.SystemPower.Add(cpu + (x.GpuPowerW ?? 0) + (Laptop ? 15 : 60));
        }

        // A smart power supply measures its own rails; otherwise the motherboard's readings.
        SensorDevice[] supply = psu is null ? [] : [psu];
        var boards = s.Devices.Where(d => d.Kind is "Motherboard" or "SuperIO").ToList();
        double? RailOf(Regex name, double low, double high) => Rail(supply, name, low, high) ?? Rail(boards, name, low, high);
        m.Volt12.Add(RailOf(Twelve(), 10.8, 13.2));
        m.Volt5Min = Min(m.Volt5Min, RailOf(Five(), 4.5, 5.5));
        m.Volt33Min = Min(m.Volt33Min, RailOf(ThreeThree(), 3.0, 3.6));

        foreach (var device in s.Devices)
            foreach (var r in device.Sensors)
            {
                if (r.Value is not { } v || v <= 0) continue;
                if (r.Type == "Fan" && v < 10000) m.FanMax = Math.Max(m.FanMax ?? 0, v);
                else if (r.Type == "Temperature" && device.Kind == "Storage" && v < 120) m.DiskMax = Math.Max(m.DiskMax ?? 0, v);
            }
    }

    /// <summary>NVIDIA cards, Radeon RX / Pro and Intel Arc A/B cards; graphics built into the processor are not separate.</summary>
    private static bool HasDiscreteGpu(SensorSnapshot s) => s.Devices.Any(d =>
        d.Kind == "GpuNvidia"
        || d.Kind == "GpuAmd" && (d.Name.Contains(" RX ", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase))
        || d.Kind == "GpuIntel" && DiscreteArc().IsMatch(d.Name));

    [GeneratedRegex(@"Arc\(TM\)\s+[AB]\d", RegexOptions.IgnoreCase)]
    private static partial Regex DiscreteArc();

    /// <summary>Power the supply delivers to the PC, from a power supply that reports it (Corsair HXi/RMi/AXi, some MSI models).</summary>
    public static double? PsuOutput(SensorDevice? psu)
    {
        if (psu is null) return null;
        var power = psu.Sensors.Where(r => r.Type == "Power" && r.Value is > 0 and < 3000).ToList();
        var total = power.FirstOrDefault(r => r.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) || r.Name.Contains("Out", StringComparison.OrdinalIgnoreCase));
        return total?.Value ?? (power.Count > 0 ? power.Max(r => r.Value) : null);
    }

    /// <summary>A rail voltage, if one of the devices reports a plausible value for it.</summary>
    public static double? Rail(IEnumerable<SensorDevice> devices, Regex name, double low, double high) =>
        devices.SelectMany(d => d.Sensors)
            .Where(r => r.Type == "Voltage" && name.IsMatch(r.Name.Trim()) && r.Value is { } v && v >= low && v <= high)
            .Select(r => (double?)r.Value)
            .FirstOrDefault();

    [GeneratedRegex(@"^\+?\s*12(\.0)?\s*V(\s*rail)?$", RegexOptions.IgnoreCase)]
    public static partial Regex Twelve();

    [GeneratedRegex(@"^\+?\s*5(\.0)?\s*V(\s*rail)?$", RegexOptions.IgnoreCase)]
    public static partial Regex Five();

    [GeneratedRegex(@"^(\+?\s*3[.,]3\s*V(\s*rail)?|3VCC|VCC3)$", RegexOptions.IgnoreCase)]
    public static partial Regex ThreeThree();

    private void ReadForeground(Minute m)
    {
        var pid = Native.ForegroundProcessId();
        var fullscreen = pid != 0 && Native.ForegroundWindowOf(pid) is { CoversMonitor: true, HasTitleBar: false };
        if (DateTime.UtcNow - _namesCleared > TimeSpan.FromMinutes(10))
        {
            _names.Clear(); // process ids get reused
            _namesCleared = DateTime.UtcNow;
        }
        (string Name, string? Title, string? Path)? app = null;
        if (pid != 0 && !_names.TryGetValue(pid, out app))
        {
            app = Identify(pid);
            _names[pid] = app;
        }
        // The desktop and a film in a browser fill the screen too, but there is nothing to time.
        var playing = fullscreen && app is { } running && !Catalog.IsNotAGame(running.Name);
        ReadFrames(m, pid, playing);
        _frontPid = pid;
        _game = playing ? app!.Value.Name : null;

        // Away from the PC: nothing is "in use", unless something is playing fullscreen.
        if (!fullscreen && Native.IdleSeconds() is { } idle && idle > AwayAfter.TotalSeconds) return;
        if (app is not { } a) return;
        m.Apps[a.Name] = m.Apps.GetValueOrDefault(a.Name) + 1;
        if (a.Title is not null) m.Titles[a.Name] = a.Title;
        if (fullscreen)
        {
            m.FullscreenSamples++;
            if (a.Path is not null) m.Paths[a.Name] = a.Path;
        }
    }

    /// <summary>
    /// Times the frames of what plays fullscreen, the way a measured session does: nothing touches the game, so
    /// anti-cheat software isn't involved. The ETW session only runs around a game, and needs administrator rights;
    /// without them the history simply has no frame rate.
    /// </summary>
    private void ReadFrames(Minute m, int pid, bool playing)
    {
        var now = DateTime.UtcNow;
        if (playing) _lastPlayed = now;
        if (_frames is null)
        {
            if (!playing || _framesUnavailable) return;
            // One failure is enough: the rights don't change while CHC runs.
            _framesUnavailable = true;
            var source = _createFrames();
            if (!source.Start(out _))
            {
                source.Dispose();
                return;
            }
            _framesUnavailable = false;
            _frames = source;
            _framePid = 0;
            return;
        }

        var frames = _frames.Drain();
        if (now - _lastPlayed > FramesIdle)
        {
            StopFrames();
            return;
        }
        if (!playing)
        {
            _framePid = 0;
            return;
        }
        if (pid != _framePid)
        {
            _framePid = pid;
            _lastFrame = 0;
        }
        foreach (var f in frames)
        {
            if (f.ProcessId != pid) continue;
            // A long gap is a pause or a loading screen, not a frame.
            if (_lastFrame > 0 && f.TimeMs - _lastFrame is > 0 and < 1000) m.FrameTimes.Add(f.TimeMs - _lastFrame);
            _lastFrame = f.TimeMs;
        }
    }

    /// <summary>What each program uses, read every few readings: averages over a quarter of a minute are plenty per hour.</summary>
    private void ReadPrograms(DateTime now)
    {
        if (_programTicks++ % ProgramEveryTicks != 0) return;
        var finished = _programs.Sample(_frontPid, _game, now);
        if (finished.Count > 0) _store.WritePrograms(finished);
        if (now - _programsSaved >= ProgramSaveInterval) SavePrograms();
    }

    /// <summary>Once a minute is plenty: only how strong the signal is, never the network's name.</summary>
    private void ReadWifi(DateTime minute)
    {
        if (Wlan.CurrentSignal() is { } signal) _store.AddWifiSignals([(minute, signal)]);
    }

    private void SaveDeviceEvents()
    {
        var events = _devices.Drain();
        if (events.Count > 0) _store.AddDeviceEvents(events);
    }

    /// <summary>Saves the hour so far, so a crash or a shutdown costs at most these few minutes.</summary>
    private void SavePrograms()
    {
        _programsSaved = DateTime.UtcNow;
        _store.WritePrograms(_programs.Rows());
    }

    private void StopFrames()
    {
        _frames?.Dispose();
        _frames = null;
    }

    /// <summary>The exe's file name, its path and the name in its file details ("Google Chrome"); nothing from its windows.</summary>
    private static (string Name, string? Title, string? Path)? Identify(int pid)
    {
        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
        string? title = null;
        var path = Native.ProcessPath(pid);
        if (path is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                title = new[] { info.FileDescription, info.ProductName }.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
            }
            catch (FileNotFoundException) { }
        }
        return (name, title, path);
    }

    private void ReadTraffic(Minute m)
    {
        long down = 0, up = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    // Virtual switches pass the same traffic as the real adapter; counting both would double it.
                    || nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
                var stats = nic.GetIPStatistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
        }
        catch (NetworkInformationException) { return; }
        if (_bytes is { } last)
        {
            // Counters restart when an adapter reconnects; a negative step is not traffic.
            m.Down += Math.Max(0, down - last.Down);
            m.Up += Math.Max(0, up - last.Up);
        }
        _bytes = (down, up);
    }

    private static double? Min(double? a, double? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    /// <summary>Running average, lowest and highest of one value over a minute.</summary>
    private sealed class Accumulator
    {
        private double _sum;
        private int _count;
        public double? Min { get; private set; }
        public double? Max { get; private set; }
        public double? Average => _count == 0 ? null : _sum / _count;

        public void Add(double? value)
        {
            if (value is not { } v || double.IsNaN(v)) return;
            _sum += v;
            _count++;
            Min = Min is null ? v : Math.Min(Min.Value, v);
            Max = Max is null ? v : Math.Max(Max.Value, v);
        }
    }

    private sealed class Minute(DateTime start)
    {
        public DateTime Start { get; } = start;
        public int Samples { get; set; }
        public Accumulator CpuTemp { get; } = new();
        public Accumulator GpuTemp { get; } = new();
        public Accumulator CpuLoad { get; } = new();
        public Accumulator GpuLoad { get; } = new();
        public Accumulator Ram { get; } = new();
        public Accumulator CpuPower { get; } = new();
        public Accumulator GpuPower { get; } = new();
        public Accumulator SystemPower { get; } = new();
        public Accumulator Volt12 { get; } = new();
        public bool Measured { get; set; }
        public double? Volt5Min { get; set; }
        public double? Volt33Min { get; set; }
        public double? FanMax { get; set; }
        public double? DiskMax { get; set; }
        public long Down { get; set; }
        public long Up { get; set; }
        public Dictionary<string, int> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Titles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int FullscreenSamples { get; set; }
        public Dictionary<string, string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<double> FrameTimes { get; } = [];
        public bool? OnBattery { get; set; }
        public double? Battery { get; set; }

        private string? TopApp => Apps.Count == 0 ? null : Apps.MaxBy(a => a.Value).Key;
        private bool HasFrames => FrameTimes.Sum() >= MinFrameMilliseconds;

        public MinuteRecord ToRecord() => new()
        {
            Start = Start,
            Samples = Samples,
            CpuTemp = CpuTemp.Average,
            CpuTempMax = CpuTemp.Max,
            GpuTemp = GpuTemp.Average,
            GpuTempMax = GpuTemp.Max,
            CpuLoad = CpuLoad.Average,
            CpuLoadMax = CpuLoad.Max,
            GpuLoad = GpuLoad.Average,
            GpuLoadMax = GpuLoad.Max,
            RamLoad = Ram.Average,
            CpuPower = CpuPower.Average,
            CpuPowerMax = CpuPower.Max,
            GpuPower = GpuPower.Average,
            GpuPowerMax = GpuPower.Max,
            SystemPower = SystemPower.Average,
            SystemPowerMax = SystemPower.Max,
            SystemPowerMeasured = Measured,
            Volt12 = Volt12.Average,
            Volt12Min = Volt12.Min,
            Volt5Min = Volt5Min,
            Volt33Min = Volt33Min,
            FanMax = FanMax,
            DiskTempMax = DiskMax,
            NetDown = Down,
            NetUp = Up,
            // The program in front for most of the minute counts for the whole minute.
            App = TopApp,
            AppTitle = TopApp is { } app ? Titles.GetValueOrDefault(app) : null,
            AppPath = TopApp is { } exe ? Paths.GetValueOrDefault(exe) : null,
            Fullscreen = FullscreenSamples * 2 > Samples,
            Fps = HasFrames ? 1000 / FrameTimes.Average() : null,
            FpsLow = HasFrames ? 1000 / PerformanceMonitor.WorstAverage(FrameTimes, 0.01) : null,
            OnBattery = OnBattery,
            BatteryPercent = Battery,
        };
    }

    public void Dispose()
    {
        Stop();
        _programs.Dispose();
        _devices.Dispose();
    }
}
