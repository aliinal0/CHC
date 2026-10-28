using System.Collections.ObjectModel;
using System.Windows.Threading;
using CHC.App.Services;
using CHC.Core.Collection;
using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;
using CHC.Core.Sensors;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace CHC.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly SensorHub _hub = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ComponentKind, ComponentViewModel> _components = [];
    private bool _liveBusy;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        IsElevated = SnapshotCollector.IsElevated;
        Dashboard = new DashboardViewModel(this);
        Sensors = new SensorsViewModel();
        Report = new ReportViewModel(this);
        Settings = new SettingsViewModel(this, settings);

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

    public ObservableCollection<NavItem> MainNav { get; } = [];
    public ObservableCollection<NavItem> FooterNav { get; } = [];
    public IReadOnlyList<ComponentSection> Sections { get; private set; } = [];

    public string AppVersion { get; } = SnapshotCollector.AppVersion;

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
    public bool HasSnapshot => Snapshot is not null;

    public async Task StartAsync()
    {
        await ScanAsync();
        _timer.Start();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var progress = new Progress<string>(key => ScanStep = Loc.T(key));
            var snapshot = await Task.Run(() => SnapshotCollector.Collect(_hub, progress));
            Health = HealthAnalyzer.Analyze(snapshot);
            Snapshot = snapshot;
            Rebuild();
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
        if (Snapshot is not null)
        {
            if (CurrentPage is ComponentViewModel c) c.UpdateLive(Snapshot.Sensors);
            if (CurrentPage == Sensors) Sensors.UpdateLive(Snapshot.Sensors);
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
                _components[section.Kind] = new ComponentViewModel(section, snapshot.Sensors);
            Dashboard.Load(snapshot, Health, _components.Values.ToList());
            Sensors.Load(snapshot.Sensors);
        }
        Report.Refresh();
        BuildNavigation();
        Navigate(selected);
    }

    private void BuildNavigation()
    {
        void OnSelected(NavItem item) => Navigate(item.Key);

        MainNav.Clear();
        MainNav.Add(new NavItem("dashboard", SymbolRegular.Home24, OnSelected) { Severity = Snapshot is null ? null : Health.Severity });
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            if (kind == ComponentKind.Battery && Snapshot?.Battery is null) continue;
            MainNav.Add(new NavItem(Icons.Key(kind), Icons.For(kind), OnSelected) { Severity = Health.For(kind)?.Severity });
        }
        MainNav.Add(new NavItem("sensors", SymbolRegular.Temperature24, OnSelected));

        FooterNav.Clear();
        FooterNav.Add(new NavItem("report", SymbolRegular.DocumentText24, OnSelected));
        FooterNav.Add(new NavItem("settings", SymbolRegular.Settings24, OnSelected));
    }

    private object ResolvePage(string key) => key switch
    {
        "sensors" => Sensors,
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
            var sensors = await Task.Run(_hub.Refresh);
            if (sensors.Devices.Count == 0) return;
            SnapshotCollector.ApplyLiveSensors(Snapshot, sensors);
            Dashboard.UpdateLive(sensors);
            if (CurrentPage is ComponentViewModel component) component.UpdateLive(sensors);
            else if (CurrentPage == Sensors) Sensors.UpdateLive(sensors);
        }
        finally
        {
            _liveBusy = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _hub.Dispose();
    }
}
