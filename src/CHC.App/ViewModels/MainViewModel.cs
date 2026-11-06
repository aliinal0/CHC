using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CHC.App.Services;
using CHC.Core.Collection;
using CHC.Core.Demo;
using CHC.Core.Devices;
using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;
using CHC.Core.Sensors;
using CHC.Core.Timeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace CHC.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly SensorHub _hub = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _changeTimer;
    private readonly Dictionary<ComponentKind, ComponentViewModel> _components = [];
    private readonly DemoData? _demo;
    private readonly HistoryStore? _history;
    private readonly HistoryRecorder? _recorder;
    private bool _liveBusy;
    private bool _windowVisible;

    private static readonly string[] DemoSteps =
        ["scan.step.sensors", "scan.step.system", "scan.step.cpu", "scan.step.memory", "scan.step.storage", "scan.step.gpu", "scan.step.stability"];

    /// <param name="demo">Show the built-in sample PC instead of scanning this one (CHC.exe --demo).</param>
    /// <param name="laptop">Make the sample PC a laptop with a battery (CHC.exe --demo-laptop).</param>
    public MainViewModel(AppSettings settings, bool demo = false, bool laptop = false)
    {
        _settings = settings;
        _demo = demo ? new DemoData { Laptop = laptop } : null;
        IsDemo = demo;
        IsElevated = demo || SnapshotCollector.IsElevated;
        Dashboard = new DashboardViewModel(this);
        Sensors = new SensorsViewModel();
        Report = new ReportViewModel(this);
        Settings = new SettingsViewModel(this, settings);
        Performance = new PerformanceViewModel(this, () => _demo?.NextSensors() ?? _hub.Refresh(), _demo);
        Network = new NetworkViewModel(this, demo);
        HistoryPage = new HistoryViewModel(this);
        Timeline = new TimelineViewModel(this);
        PowerPage = new PowerViewModel(this, settings);
        Programs = new ProgramsViewModel(this);
        Devices = new DevicesViewModel(this);
        LookBack = new LookBackViewModel(this);

        // Between scans (CHC in the tray for days), Windows' own change logs are read every few hours.
        _changeTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _changeTimer.Tick += (_, _) =>
        {
            if (!_windowVisible) TrackChanges(null);
        };
        if (!demo) _changeTimer.Start();

        // The history lives in its own file; if it can't be opened (disk full, no rights) CHC works without it.
        _recordHistory = settings.RecordHistory;
        try
        {
            if (_demo is not null)
            {
                _history = new HistoryStore();
                var minutes = DemoHistory.Create(laptop);
                _history.WriteAll(minutes);
                _history.WritePrograms(DemoHistory.Programs(minutes, laptop));
                _history.AddDeviceEvents(DemoData.DeviceEvents());
                _history.AddWifiSignals(DemoData.WifiSignals());
                _history.AddPastBlocks(DemoData.PastBlocks());
            }
            else
            {
                _history = new HistoryStore(HistoryStore.DefaultPath) { DetailDays = Math.Max(30, settings.HistoryDetailDays) };
                _recorder = new HistoryRecorder(_history, _hub.Refresh);
                // A new minute on the history page: show it, if the page is open on the current period.
                _recorder.MinuteWritten += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!_windowVisible) return;
                    if (CurrentPage == HistoryPage) HistoryPage.Refresh();
                    else if (CurrentPage == Dashboard) Dashboard.LoadLately(_history, Timeline);
                });
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            HistoryError = ex.Message;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(settings.RefreshSeconds, 1, 60)) };
        _timer.Tick += async (_, _) => await LiveTickAsync();
        Loc.Instance.LanguageChanged += (_, _) => Rebuild();

        BuildNavigation();
        Navigate("dashboard");
    }

    public DashboardViewModel Dashboard { get; }
    public SensorsViewModel Sensors { get; }
    public ReportViewModel Report { get; }
    public SettingsViewModel Settings { get; }
    public PerformanceViewModel Performance { get; }
    public NetworkViewModel Network { get; }
    public HistoryViewModel HistoryPage { get; }
    public TimelineViewModel Timeline { get; }
    public ProgramsViewModel Programs { get; }
    public DevicesViewModel Devices { get; }
    public LookBackViewModel LookBack { get; }

    public DateTime? RecordsReadAt => _settings.RecordsReadAt;

    public bool ReadRecordsAutomatically
    {
        get => _settings.ReadRecordsAutomatically;
        set
        {
            _settings.ReadRecordsAutomatically = value;
            SettingsService.Save(_settings);
        }
    }

    public void RecordsWereRead()
    {
        _settings.RecordsReadAt = DateTime.Now;
        SettingsService.Save(_settings);
        if (Snapshot is { } snapshot)
        {
            // The freezes and crashes can now say what was on at the time.
            if (_history is not null)
            {
                try { Core.Performance.ProgramAdvisor.AddMoments(_history, snapshot); }
                catch (Microsoft.Data.Sqlite.SqliteException) { /* they do without */ }
            }
            ReanalyzeDevices();
        }
    }

    private Core.Updates.UpdateInfo? _updates;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesNowCommand))]
    private bool _isCheckingUpdates;

    partial void OnIsCheckingUpdatesChanged(bool value) => Dashboard.RefreshUpdateStatus();

    public bool CheckUpdatesAutomatically
    {
        get => _settings.CheckUpdatesAutomatically;
        set
        {
            _settings.CheckUpdatesAutomatically = value;
            SettingsService.Save(_settings);
        }
    }

    public void CheckUpdatesIfDue()
    {
        if (_demo is not null || !_settings.CheckUpdatesAutomatically || Snapshot is null) return;
        if (_updates?.CheckedAt is { } last && DateTime.Now - last < TimeSpan.FromHours(20)) return;
        _ = CheckUpdatesAsync();
    }

    private bool CanCheckUpdates => !IsCheckingUpdates;

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private Task CheckUpdatesNow() => CheckUpdatesAsync();

    /// <summary>Goes online and asks; never installs anything. What it finds joins the findings, and stays for the next start.</summary>
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdates || Snapshot is not { } snapshot) return;
        IsCheckingUpdates = true;
        try
        {
            if (_demo is not null)
            {
                await Task.Delay(1200);
                snapshot.Updates.CheckedAt = DateTime.Now;
            }
            else
            {
                var found = await Task.Run(() => Core.Updates.UpdateChecker.CheckAsync(snapshot, _updates));
                _updates = found;
                Core.Updates.UpdateStore.Save(found);
            }
        }
        finally
        {
            IsCheckingUpdates = false;
        }

        // A scan may have replaced the snapshot in the meantime.
        if (Snapshot is { } now)
        {
            if (_updates is not null) now.Updates = _updates;
            ReanalyzeDevices();
        }
    }

    /// <summary>Fills in what Windows noted while CHC wasn't looking, if the user asked for that and it can be done without a question.</summary>
    private async void ReadRecordsIfDue()
    {
        if (_demo is not null || !IsElevated || _history is not { } history || !_settings.ReadRecordsAutomatically) return;
        if (_settings.RecordsReadAt is { } last && DateTime.Now - last < TimeSpan.FromHours(20)) return;
        var outcome = await RecordsService.ReadAsync(history, elevated: true);
        if (outcome.Result != RecordsResult.Done) return;
        RecordsWereRead();
        if (CurrentPage == LookBack) LookBack.Refresh();
        else if (CurrentPage == Performance) Performance.LoadGames();
    }
    public PowerViewModel PowerPage { get; }

    public ObservableCollection<NavItem> MainNav { get; } = [];
    public ObservableCollection<NavItem> FooterNav { get; } = [];
    public IReadOnlyList<ComponentSection> Sections { get; private set; } = [];

    public string AppVersion { get; } = SnapshotCollector.AppVersion;
    public bool IsDemo { get; }

    /// <summary>The "sample data" badge; hidden with --screenshot so README images stay clean.</summary>
    public bool ShowDemoBadge { get; init; }

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstScan))]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanStep = "";

    [ObservableProperty]
    private bool _isElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstScan), nameof(HasSnapshot))]
    private SystemSnapshot? _snapshot;

    [ObservableProperty]
    private HealthReport _health = HealthReport.Empty;

    public bool IsFirstScan => IsScanning && Snapshot is null;

    /// <summary>The temperature and usage history, or null if its file couldn't be opened.</summary>
    public HistoryStore? History => _history;
    public string? HistoryError { get; }

    /// <summary>The user's choice to keep a history (Settings); with it on, closing the window keeps CHC in the tray.</summary>
    [ObservableProperty]
    private bool _recordHistory;

    /// <summary>Paused from the tray until resumed (or the next start).</summary>
    [ObservableProperty]
    private bool _recordingPaused;

    public bool IsRecording => _recorder?.IsRunning == true;

    /// <summary>The recorder's last write problem (disk full, file locked), if any.</summary>
    public string? RecorderError => _recorder?.LastError;

    partial void OnRecordHistoryChanged(bool value)
    {
        _settings.RecordHistory = value;
        SettingsService.Save(_settings);
        if (value) StartRecording();
        else StopRecording();
    }

    partial void OnRecordingPausedChanged(bool value)
    {
        if (value) StopRecording();
        else StartRecording();
    }

    private Core.Power.PowerSupplyInfo PowerSupplyFor(SystemSnapshot snapshot)
    {
        var (watts, rating, model) = PowerPage.Configured();
        return Core.Power.PowerSupplyMath.Build(snapshot.Sensors, watts, rating, model, _history);
    }

    /// <summary>The user described their power supply: its findings change, so does the score.</summary>
    /// <summary>A device test finished: what it found belongs on every page and in the report.</summary>
    public void ReanalyzeDevices()
    {
        if (Snapshot is not { } snapshot) return;
        Health = HealthAnalyzer.Analyze(snapshot);
        Rebuild();
    }

    public void ReanalyzePowerSupply()
    {
        if (Snapshot is not { } snapshot) return;
        snapshot.PowerSupply = PowerSupplyFor(snapshot);
        Health = HealthAnalyzer.Analyze(snapshot);
        Rebuild();
    }

    /// <summary>
    /// Adds what changed to the timeline: after a scan with everything the scan saw, and every few hours in the
    /// background with what Windows logged. The demo only shows its made-up changes.
    /// </summary>
    private void TrackChanges(SystemSnapshot? snapshot)
    {
        if (_history is not { } store) return;
        if (_demo is not null)
        {
            if (snapshot is not null) store.AddChanges(snapshot.Changes);
            return;
        }
        Task.Run(() =>
        {
            try { ChangeTracker.Track(store, snapshot, snapshot?.Changes ?? ChangeCollector.Collect(TimeSpan.FromDays(14))); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* the timeline waits for the next try */ }
        }).ContinueWith(_ =>
        {
            if (CurrentPage == Timeline) Timeline.Refresh();
            else if (CurrentPage == Dashboard) Dashboard.LoadLately(_history, Timeline);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void StartRecording()
    {
        if (_recorder is not { IsRunning: false } recorder || !RecordHistory || RecordingPaused) return;
        // Opening the sensors takes a few seconds; not on the window's thread.
        Task.Run(() =>
        {
            _hub.Open();
            // Turned off or paused meanwhile: stay off.
            if (RecordHistory && !RecordingPaused) recorder.Start();
        }).ContinueWith(_ => OnPropertyChanged(nameof(IsRecording)), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void StopRecording()
    {
        if (_recorder is not { IsRunning: true }) return;
        _recorder.Stop();
        OnPropertyChanged(nameof(IsRecording));
    }

    /// <summary>
    /// The window was shown or hidden. Hidden, only the recorder reads sensors, and disks only every five minutes
    /// so a sleeping hard drive stays asleep. The first time it is shown, CHC scans.
    /// </summary>
    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        _hub.StorageInterval = visible ? TimeSpan.Zero : TimeSpan.FromMinutes(5);
        if (!visible)
        {
            _timer.Stop();
            return;
        }
        if (Snapshot is null)
        {
            if (!IsScanning) _ = StartAsync();
        }
        else
        {
            _timer.Start();
        }
    }

    /// <summary>
    /// The newest sensor values for the live tiles. The snapshot keeps the values of the scan itself, so the
    /// findings and every number in a report describe the same moment.
    /// </summary>
    public SensorSnapshot LiveSensors { get; private set; } = SensorSnapshot.Empty;
    public bool HasSnapshot => Snapshot is not null;

    public async Task StartAsync()
    {
        await ScanAsync();
        if (_windowVisible) _timer.Start();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var firstScan = Snapshot is null;
            SystemSnapshot snapshot;
            if (_demo is not null)
            {
                foreach (var step in DemoSteps)
                {
                    ScanStep = Loc.T(step);
                    await Task.Delay(150);
                }
                snapshot = _demo.CreateSnapshot();
            }
            else
            {
                var progress = new Progress<string>(key => ScanStep = Loc.T(key));
                snapshot = await Task.Run(() => SnapshotCollector.Collect(_hub, progress));
            }
            // Keep the last measured game session and network test across rescans.
            snapshot.Performance = Snapshot?.Performance;
            snapshot.NetworkTest = Snapshot?.NetworkTest;
            snapshot.PowerSupply = PowerSupplyFor(snapshot);
            if (_history is not null)
            {
                try
                {
                    Core.Performance.ProgramAdvisor.AddMoments(_history, snapshot);
                    DeviceAdvisor.AddDrops(_history, snapshot);
                }
                catch (Microsoft.Data.Sqlite.SqliteException) { /* the findings do without what the history saw */ }
            }
            // What the last look for updates found, minus what has been installed since.
            if (_demo is null)
            {
                _updates ??= Core.Updates.UpdateStore.Load();
                if (_updates is not null)
                {
                    Core.Updates.UpdateChecker.KeepCurrent(_updates, snapshot);
                    snapshot.Updates = _updates;
                }
            }
            Health = HealthAnalyzer.Analyze(snapshot);
            TrackChanges(snapshot);
            if (_recorder is not null)
            {
                _recorder.Laptop = snapshot.Board.IsLaptop;
                _recorder.DiscreteGpu = snapshot.Gpus.Any(g => !g.IsIntegrated && !g.IsVirtual && !g.UsesBasicDriver);
            }
            LiveSensors = snapshot.Sensors;
            Snapshot = snapshot;
            Rebuild();
            Devices.Refresh();
            ReadRecordsIfDue();
            CheckUpdatesIfDue();

            // Give the demo charts a history, as if CHC had been open for a while.
            if (_demo is not null && firstScan)
                for (var i = 0; i < 59; i++) ApplySensors(_demo.NextSensors());
        }
        finally
        {
            IsScanning = false;
            ScanStep = "";
        }
    }

    [RelayCommand]
    private void RestartAsAdmin() => ShellService.RestartAsAdministrator();

    [RelayCommand]
    public void Navigate(string? key)
    {
        var all = MainNav.Concat(FooterNav).ToList();
        var item = all.FirstOrDefault(n => n.Key == key) ?? all.First();
        foreach (var n in all) n.SetSelectedSilently(n == item);
        CurrentPage = ResolvePage(item.Key);
        if (CurrentPage == Dashboard) Dashboard.LoadLately(_history, Timeline);
        else if (CurrentPage == Settings) Settings.RefreshHistory();
        else if (CurrentPage == HistoryPage) HistoryPage.Refresh();
        else if (CurrentPage == Timeline) Timeline.Refresh();
        else if (CurrentPage == PowerPage) PowerPage.Refresh();
        else if (CurrentPage == Performance) Performance.LoadGames();
        else if (CurrentPage == Programs) Programs.Refresh();
        else if (CurrentPage == Devices) Devices.Refresh();
        else if (CurrentPage == LookBack) LookBack.Refresh();
        if (Snapshot is not null)
        {
            if (CurrentPage is ComponentViewModel c) c.UpdateLive(LiveSensors);
            if (CurrentPage == Sensors) Sensors.UpdateLive(LiveSensors);
        }
    }

    public void SetRefreshInterval(int seconds)
    {
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _settings.RefreshSeconds = seconds;
        SettingsService.Save(_settings);
    }

    /// <summary>Recreates every page model from the current snapshot (after a scan or a language change).</summary>
    private void Rebuild()
    {
        var selected = MainNav.Concat(FooterNav).FirstOrDefault(n => n.IsSelected)?.Key ?? "dashboard";
        if (Snapshot is { } snapshot)
        {
            Sections = SectionBuilder.Build(snapshot, Health);
            _components.Clear();
            foreach (var section in Sections)
                _components[section.Kind] = new ComponentViewModel(section, LiveSensors);
            Dashboard.Load(snapshot, Health, _components.Values.ToList(), LiveSensors);
            Sensors.Load(LiveSensors);
        }
        Report.Refresh();
        Performance.Refresh();
        Network.Refresh();
        BuildNavigation();
        Navigate(selected);
    }

    private void BuildNavigation()
    {
        void OnSelected(NavItem item) => Navigate(item.Key);
        static string Group(string key) => Loc.T("nav.group." + key);

        MainNav.Clear();
        // Check-up: the scan and every part it looked at.
        MainNav.Add(new NavItem("dashboard", SymbolRegular.Home24, OnSelected) { Severity = Snapshot is null ? null : Health.RatingSeverity, Header = Group("checkup") });
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            if (kind is ComponentKind.Performance or ComponentKind.Network || (kind == ComponentKind.Battery && Snapshot?.Battery is null)) continue;
            MainNav.Add(new NavItem(Icons.Key(kind), Icons.For(kind), OnSelected) { Severity = Health.For(kind)?.Severity });
        }
        // Tests the user starts; what the history kept; what the sensors say right now.
        MainNav.Add(new NavItem("performance", SymbolRegular.TopSpeed24, OnSelected) { Header = Group("tests") });
        MainNav.Add(new NavItem("network", SymbolRegular.Globe24, OnSelected));
        MainNav.Add(new NavItem("devices", SymbolRegular.PlugConnected24, OnSelected));
        MainNav.Add(new NavItem("history", SymbolRegular.DataTrending24, OnSelected) { Header = Group("history") });
        MainNav.Add(new NavItem("timeline", SymbolRegular.Timeline24, OnSelected));
        MainNav.Add(new NavItem("programs", SymbolRegular.AppsList24, OnSelected));
        MainNav.Add(new NavItem("lookback", SymbolRegular.History24, OnSelected));
        MainNav.Add(new NavItem("sensors", SymbolRegular.Temperature24, OnSelected) { Header = Group("live") });
        MainNav.Add(new NavItem("power", SymbolRegular.Flash24, OnSelected));

        FooterNav.Clear();
        FooterNav.Add(new NavItem("report", SymbolRegular.DocumentText24, OnSelected));
        FooterNav.Add(new NavItem("settings", SymbolRegular.Settings24, OnSelected));
    }

    private object ResolvePage(string key) => key switch
    {
        "sensors" => Sensors,
        "performance" => Performance,
        "network" => Network,
        "history" => HistoryPage,
        "timeline" => Timeline,
        "programs" => Programs,
        "devices" => Devices,
        "lookback" => LookBack,
        "power" => PowerPage,
        "report" => Report,
        "settings" => Settings,
        _ when Enum.TryParse<ComponentKind>(key, true, out var kind) && _components.TryGetValue(kind, out var vm) => vm,
        _ => Dashboard,
    };

    private async Task LiveTickAsync()
    {
        if (_liveBusy || IsScanning || Snapshot is null) return;
        _liveBusy = true;
        try
        {
            var sensors = _demo?.NextSensors() ?? await Task.Run(_hub.Refresh);
            if (sensors.Devices.Count > 0) ApplySensors(sensors);
        }
        finally
        {
            _liveBusy = false;
        }
    }

    private void ApplySensors(SensorSnapshot sensors)
    {
        if (Snapshot is null) return;
        LiveSensors = sensors;
        Dashboard.UpdateLive(sensors);
        if (CurrentPage is ComponentViewModel component) component.UpdateLive(sensors);
        else if (CurrentPage == Sensors) Sensors.UpdateLive(sensors);
    }

    public void Dispose()
    {
        _timer.Stop();
        _changeTimer.Stop();
        Performance.Dispose();
        Network.Dispose();
        _recorder?.Dispose();
        _history?.Dispose();
        _hub.Dispose();
    }
}
