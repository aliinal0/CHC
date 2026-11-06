using System.Windows;
using CHC.Core.Demo;
using CHC.Core.Localization;
using CHC.Core.Network;
using CHC.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CHC.App.ViewModels;

/// <summary>The network page: start/stop a ping test, live values, and the analysis.</summary>
public sealed partial class NetworkViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 120;

    private readonly MainViewModel _main;
    private readonly NetworkMonitor _monitor;
    private readonly bool _demo;
    private readonly List<double> _internet = [];
    private readonly List<double> _gateway = [];

    public NetworkViewModel(MainViewModel main, bool demo)
    {
        _main = main;
        _demo = demo;
        _monitor = new NetworkMonitor(id => demo ? new DemoNetProbe() : new RealNetProbe(id));
        _monitor.Updated += (_, state) => Application.Current?.Dispatcher.BeginInvoke(() => Apply(state));
        _status = Loc.T("net.status.idle");
    }

    public MainViewModel Main => _main;

    /// <summary>Traffic per program needs an ETW session, which Windows only allows for administrators.</summary>
    public bool ShowAdminHint => !_demo && !_main.IsElevated;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isStopping;

    [ObservableProperty]
    private bool _hasLive;

    [ObservableProperty]
    private string _status;

    [ObservableProperty] private string _pingText = Fmt.Dash;
    [ObservableProperty] private string _jitterText = Fmt.Dash;
    [ObservableProperty] private string _lossText = Fmt.Dash;
    [ObservableProperty] private string _spikesText = Fmt.Dash;
    [ObservableProperty] private string _routerText = Fmt.Dash;
    [ObservableProperty] private string _dnsText = Fmt.Dash;
    [ObservableProperty] private string _downText = Fmt.Dash;
    [ObservableProperty] private string _upText = Fmt.Dash;

    [ObservableProperty] private IReadOnlyList<double>? _internetHistory;
    [ObservableProperty] private double _internetChartMax = 50;
    [ObservableProperty] private IReadOnlyList<double>? _gatewayHistory;
    [ObservableProperty] private double _gatewayChartMax = 10;

    [ObservableProperty]
    private ComponentViewModel? _result;

    private bool CanStart() => !IsRunning && !IsStopping && _main.Snapshot is { Network.Physical: not null };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (_main.Snapshot is not { } snapshot) return;
        _internet.Clear();
        _gateway.Clear();
        InternetHistory = null;
        GatewayHistory = null;
        PingText = JitterText = LossText = SpikesText = RouterText = DnsText = DownText = UpText = Fmt.Dash;

        _monitor.Start(snapshot.Network);
        IsRunning = true;
        HasLive = true;
        Status = Loc.T("net.status.starting");
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        IsRunning = false;
        IsStopping = true;
        Status = Loc.T("net.status.stopping");
        // Waits for the last pings to come back, so keep the window responsive.
        var session = await Task.Run(_monitor.Stop);
        IsStopping = false;
        if (session is not null && _main.Snapshot is { } snapshot)
        {
            snapshot.NetworkTest = session;
            Status = Loc.T("net.status.done", Fmt.Elapsed(session.Duration));
        }
        else
        {
            Status = Loc.T("net.status.idle");
        }
        Refresh();
        _main.Report.Refresh();
    }

    /// <summary>Rebuilds the analysis (after a test, a rescan or a language change).</summary>
    public void Refresh()
    {
        if (_main.Snapshot is not { } snapshot) return;
        Result = new ComponentViewModel(NetworkSection.Build(snapshot, snapshot.NetworkTest), _main.LiveSensors);
        StartCommand.NotifyCanExecuteChanged();
        if (!IsRunning && !IsStopping && snapshot.NetworkTest is null) Status = Loc.T("net.status.idle");
    }

    private void Apply(NetLiveState s)
    {
        if (!IsRunning) return;
        Status = Loc.T("net.status.measuring", Fmt.Elapsed(s.Elapsed));
        PingText = Fmt.Ms(s.InternetMs);
        JitterText = Fmt.Ms(s.JitterMs);
        LossText = Fmt.Percent(s.LossPercent, 1);
        SpikesText = s.Spikes.ToString();
        RouterText = Fmt.Ms(s.GatewayMs);
        DnsText = Fmt.Ms(s.DnsMs);
        DownText = Mbps(s.Sample.DownMbps);
        UpText = Mbps(s.Sample.UpMbps);

        if (s.Sample.InternetMs is { } ms)
        {
            Push(_internet, ms);
            InternetHistory = _internet.ToArray();
            InternetChartMax = Math.Max(30, _internet.Max() * 1.15);
        }
        if (s.Sample.GatewayMs is { } gw)
        {
            Push(_gateway, gw);
            GatewayHistory = _gateway.ToArray();
            GatewayChartMax = Math.Max(5, _gateway.Max() * 1.15);
        }
    }

    private static string Mbps(double value) => value.ToString(value < 10 ? "0.0" : "0", Loc.Instance.Culture) + " Mbps";

    private static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    public void Dispose() => _monitor.Dispose();
}
