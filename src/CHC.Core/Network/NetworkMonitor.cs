using System.Diagnostics;

namespace CHC.Core.Network;

/// <summary>What the app shows every second while the network test runs.</summary>
public sealed record NetLiveState(
    TimeSpan Elapsed,
    double? InternetMs,
    double? GatewayMs,
    double? JitterMs,
    double LossPercent,
    double? DnsMs,
    int Spikes,
    NetSample Sample,
    double? ServerMs = null);

/// <summary>
/// Runs a network test: pings the router and two public servers several times a second, times DNS
/// lookups and watches the adapter's traffic. Comparing the router with the internet tells whether
/// lag comes from inside the home (Wi-Fi, cable, router) or from the internet provider.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    public const string InternetTarget = "1.1.1.1";
    public const string SecondaryTarget = "8.8.8.8";
    private const int TickMs = 250;
    private const int TimeoutMs = 1000;

    private readonly Func<string, INetProbe> _createProbe;
    private readonly object _sync = new();
    private readonly Stopwatch _clock = new();
    private readonly List<(double Sent, double? Ms)> _gateway = [];
    private readonly List<(double Sent, double? Ms)> _internet = [];
    private readonly List<(double Sent, double? Ms)> _secondary = [];
    private readonly List<(double Sent, double? Ms)> _server = [];
    private readonly List<double?> _dns = [];
    private readonly List<Task> _pending = [];

    private INetProbe? _probe;
    private Timer? _timer;
    private NetSession? _session;
    private int _ticks;
    private double _lastSecond;
    private (ulong Down, ulong Up) _startCounters, _lastCounters;
    private string _serverAddress = "";

    /// <param name="createProbe">Creates the probe for an adapter id (the real network, or the demo).</param>
    public NetworkMonitor(Func<string, INetProbe> createProbe) => _createProbe = createProbe;

    public event EventHandler<NetLiveState>? Updated;

    public bool IsRunning { get; private set; }

    /// <summary>True when traffic per program is being measured (administrator only).</summary>
    public bool MeasuresPrograms { get; private set; }

    public void Start(NetworkInfo network)
    {
        lock (_sync)
        {
            if (IsRunning) return;
            _gateway.Clear();
            _internet.Clear();
            _secondary.Clear();
            _server.Clear();
            _serverAddress = "";
            _dns.Clear();
            _pending.Clear();
            _ticks = 0;
            _lastSecond = 0;

            var adapter = network.Physical ?? network.Primary;
            _probe = _createProbe(adapter?.Id ?? "");
            MeasuresPrograms = _probe.StartTraffic();
            _session = new NetSession
            {
                Started = DateTime.Now,
                Adapter = adapter?.Description ?? "",
                Wireless = adapter?.IsWireless == true,
                GatewayAddress = adapter?.Gateway ?? "",
                InternetTarget = InternetTarget,
                SecondaryTarget = SecondaryTarget,
                ProgramsMeasured = MeasuresPrograms,
            };
            _startCounters = _lastCounters = _probe.Counters();
            _clock.Restart();
            IsRunning = true;
            _timer = new Timer(_ => Tick(), null, 0, TickMs);
        }
    }

    /// <summary>Also pings this game server from now on (twice a second).</summary>
    public void SetServer(string address)
    {
        lock (_sync)
        {
            // Pings to a previous server (the menu, a login server) would mix two different routes.
            if (address != _serverAddress) _server.Clear();
            _serverAddress = address;
            if (_session is not null) _session.ServerAddress = address;
        }
    }

    public NetSession? Stop()
    {
        Timer? timer;
        Task[] pending;
        lock (_sync)
        {
            if (!IsRunning || _session is null) return null;
            IsRunning = false;
            timer = _timer;
            _timer = null;
            pending = _pending.ToArray();
        }
        timer?.Dispose();
        // Let the last pings come back (or time out) so they are counted.
        try { Task.WaitAll(pending, TimeoutMs + 500); } catch (AggregateException) { }

        lock (_sync)
        {
            var s = _session;
            s.Ended = DateTime.Now;
            s.GatewayPings = _gateway.OrderBy(p => p.Sent).Select(p => p.Ms).ToList();
            s.InternetPings = _internet.OrderBy(p => p.Sent).Select(p => p.Ms).ToList();
            s.SecondaryPings = _secondary.OrderBy(p => p.Sent).Select(p => p.Ms).ToList();
            s.ServerPings = _server.OrderBy(p => p.Sent).Select(p => p.Ms).ToList();
            s.DnsLookups = _dns.ToList();
            if (_probe is not null)
            {
                var (down, up) = _probe.Counters();
                s.BytesDown = down >= _startCounters.Down ? down - _startCounters.Down : 0;
                s.BytesUp = up >= _startCounters.Up ? up - _startCounters.Up : 0;
                s.Programs = _probe.Traffic().ToList();
                _probe.Dispose();
                _probe = null;
            }
            return s;
        }
    }

    private void Tick()
    {
        NetLiveState? state = null;
        lock (_sync)
        {
            if (!IsRunning || _session is null || _probe is null) return;
            var now = _clock.Elapsed.TotalSeconds;
            _ticks++;

            Send(_internet, InternetTarget, now);
            if (_session.GatewayAddress.Length > 0 && _ticks % 2 == 0) Send(_gateway, _session.GatewayAddress, now);
            if (_ticks % 4 == 1) Send(_secondary, SecondaryTarget, now);
            if (_serverAddress.Length > 0 && _ticks % 2 == 1) Send(_server, _serverAddress, now);
            if (_ticks % 20 == 1) Track(LookupAsync());
            _pending.RemoveAll(t => t.IsCompleted);

            if (now - _lastSecond >= 1)
                state = Second(now);
        }
        if (state is not null) Updated?.Invoke(this, state);
    }

    private void Send(List<(double Sent, double? Ms)> list, string target, double now)
    {
        var probe = _probe!;
        Track(Task.Run(async () =>
        {
            var ms = await probe.PingAsync(target, TimeoutMs).ConfigureAwait(false);
            lock (_sync)
            {
                // An answer from the previous game server, arriving after the switch, belongs to neither list.
                if (list != _server || target == _serverAddress) list.Add((now, ms));
            }
        }));
    }

    private async Task LookupAsync()
    {
        var ms = await _probe!.LookupAsync().ConfigureAwait(false);
        lock (_sync) _dns.Add(ms);
    }

    private void Track(Task task) => _pending.Add(task);

    /// <summary>Builds the sample for the second that just ended and the live values.</summary>
    private NetLiveState Second(double now)
    {
        var from = _lastSecond - 1;
        var elapsed = Math.Max(0.2, now - _lastSecond);
        _lastSecond = now;

        // Pings sent during the previous second have answered (or timed out) by now.
        static (double? Avg, int Lost) Window(List<(double Sent, double? Ms)> list, double from, double to)
        {
            var window = list.Where(p => p.Sent >= from && p.Sent < to).ToList();
            var ok = window.Where(p => p.Ms is not null).Select(p => p.Ms!.Value).ToList();
            return (ok.Count > 0 ? ok.Average() : null, window.Count(p => p.Ms is null));
        }
        var internet = Window(_internet, from, from + 1);
        var gateway = Window(_gateway, from, from + 1);
        var server = Window(_server, from, from + 1);

        var counters = _probe!.Counters();
        var down = counters.Down >= _lastCounters.Down ? counters.Down - _lastCounters.Down : 0;
        var up = counters.Up >= _lastCounters.Up ? counters.Up - _lastCounters.Up : 0;
        _lastCounters = counters;

        var sample = new NetSample
        {
            Second = Math.Round(from + 1),
            InternetMs = internet.Avg,
            GatewayMs = gateway.Avg,
            ServerMs = server.Avg,
            InternetLost = internet.Lost,
            GatewayLost = gateway.Lost,
            DownMbps = down * 8 / 1_000_000.0 / elapsed,
            UpMbps = up * 8 / 1_000_000.0 / elapsed,
        };
        if (from >= 0) _session!.Samples.Add(sample);

        var stats = NetStats.From(_internet.OrderBy(p => p.Sent).Select(p => p.Ms).ToList());
        var recent = _internet.OrderBy(p => p.Sent).TakeLast(40).Select(p => p.Ms).ToList();
        return new NetLiveState(
            _clock.Elapsed,
            _internet.LastOrDefault(p => p.Ms is not null).Ms,
            _gateway.LastOrDefault(p => p.Ms is not null).Ms,
            recent.Count(r => r is not null) >= 2 ? NetStats.From(recent).Jitter : null,
            stats.LossPercent,
            _dns.LastOrDefault(d => d is not null),
            stats.Spikes,
            sample,
            _server.LastOrDefault(p => p.Ms is not null).Ms);
    }

    public void Dispose()
    {
        Stop();
        _probe?.Dispose();
    }
}
