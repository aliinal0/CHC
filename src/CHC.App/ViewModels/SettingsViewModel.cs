using System.Collections.ObjectModel;
using System.Windows;
using CHC.App.Services;
using CHC.Core.Localization;
using CHC.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CHC.App.ViewModels;

public sealed record Option<T>(T Value, string Title)
{
    public override string ToString() => Title;
}

public sealed record ThirdParty(string Name, string License, string Url);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _settings;
    private bool _loading;

    public SettingsViewModel(MainViewModel main, AppSettings settings)
    {
        _main = main;
        _settings = settings;
        Languages = Loc.Instance.Languages;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == Loc.Instance.CurrentLanguage);
        BuildOptions();
        Loc.Instance.LanguageChanged += (_, _) => BuildOptions();
    }

    public IReadOnlyList<LanguageInfo> Languages { get; }

    /// <summary>CHC ships in English only for now; the choice appears once a language file is added.</summary>
    public bool HasLanguageChoice => Languages.Count > 1;
    public ObservableCollection<Option<string>> Themes { get; } = [];
    public ObservableCollection<Option<int>> RefreshIntervals { get; } = [];
    public ObservableCollection<Option<int>> DetailPeriods { get; } = [];
    public ObservableCollection<Option<string>> CloseChoices { get; } = [];

    public string Version => _main.AppVersion;
    public bool IsElevated => _main.IsElevated;
    public MainViewModel Main => _main;

    public IReadOnlyList<ThirdParty> Credits { get; } =
    [
        new("LibreHardwareMonitor", "MPL-2.0", "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"),
        new("DiskInfoToolkit", "MPL-2.0", "https://github.com/Blacktempel/DiskInfoToolkit"),
        new("RAMSPDToolkit", "MPL-2.0", "https://github.com/Blacktempel/RAMSPDToolkit"),
        new("BlackSharp", "MPL-2.0", "https://github.com/Blacktempel/BlackSharp"),
        new("HidSharp", "Apache-2.0", "https://software.seekye.com/hidsharp"),
        new("WPF UI", "MIT", "https://github.com/lepoco/wpfui"),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Microsoft.Data.Sqlite", "MIT", "https://github.com/dotnet/efcore"),
        new("SQLitePCLRaw", "Apache-2.0", "https://github.com/ericsink/SQLitePCL.raw"),
        new("SQLite", "Public domain", "https://sqlite.org"),
        new("Inter", "OFL-1.1", "https://github.com/rsms/inter"),
    ];

    [ObservableProperty]
    private LanguageInfo? _selectedLanguage;

    [ObservableProperty]
    private Option<string>? _selectedTheme;

    [ObservableProperty]
    private Option<int>? _selectedRefresh;

    partial void OnSelectedLanguageChanged(LanguageInfo? value)
    {
        if (_loading || value is null || value.Code == Loc.Instance.CurrentLanguage) return;
        _settings.Language = value.Code;
        SettingsService.Save(_settings);
        Loc.Instance.SetLanguage(value.Code);
    }

    partial void OnSelectedThemeChanged(Option<string>? value)
    {
        if (_loading || value is null || value.Value == _settings.Theme) return;
        _settings.Theme = value.Value;
        SettingsService.Save(_settings);
        if (Application.Current.MainWindow is { } window) ThemeService.Apply(value.Value, window);
    }

    partial void OnSelectedRefreshChanged(Option<int>? value)
    {
        if (_loading || value is null || value.Value == _settings.RefreshSeconds) return;
        _main.SetRefreshInterval(value.Value);
    }

    [ObservableProperty]
    private Option<int>? _selectedDetail;

    [ObservableProperty]
    private Option<string>? _selectedCloseChoice;

    /// <summary>The sign-in task exists: CHC starts in the notification area when someone signs in.</summary>
    [ObservableProperty]
    private bool _startsWithWindows;

    [ObservableProperty]
    private string _historyInfo = "";

    [ObservableProperty]
    private string? _historyProblem;

    public bool CheckUpdatesAutomatically
    {
        get => _main.CheckUpdatesAutomatically;
        set
        {
            if (_main.CheckUpdatesAutomatically == value) return;
            _main.CheckUpdatesAutomatically = value;
            OnPropertyChanged();
            _main.CheckUpdatesIfDue();
        }
    }

    public bool RecordHistory
    {
        get => _main.RecordHistory;
        set
        {
            if (_main.RecordHistory == value) return;
            _main.RecordHistory = value;
            OnPropertyChanged();
            RefreshHistory();
        }
    }

    partial void OnSelectedCloseChoiceChanged(Option<string>? value)
    {
        if (_loading || value is null || value.Value == _settings.CloseAction) return;
        _settings.CloseAction = value.Value;
        SettingsService.Save(_settings);
    }

    /// <summary>The close question was answered with "don't ask again": the setting shows the answer.</summary>
    public void RefreshCloseAction()
    {
        var loading = _loading;
        _loading = true;
        try { SelectedCloseChoice = CloseChoices.FirstOrDefault(c => c.Value == _settings.CloseAction) ?? CloseChoices[0]; }
        finally { _loading = loading; }
    }

    partial void OnSelectedDetailChanged(Option<int>? value)
    {
        if (_loading || value is null || value.Value == _settings.HistoryDetailDays) return;
        _settings.HistoryDetailDays = value.Value;
        SettingsService.Save(_settings);
        if (_main.History is { } history) history.DetailDays = value.Value;
    }

    /// <summary>Size, start and problems of the history; checks the sign-in task without holding up the page.</summary>
    public void RefreshHistory()
    {
        if (_main.History is { } history)
        {
            var since = history.FirstRecord();
            HistoryInfo = since is null
                ? Loc.T("settings.history.empty")
                : Loc.T("settings.history.size", Fmt.Bytes((ulong)history.SizeBytes), Fmt.Date(since));
        }
        HistoryProblem = _main.HistoryError ?? _main.RecorderError;
        Task.Run(StartupService.IsRegistered).ContinueWith(t => StartsWithWindows = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private void SetUpStartup() => SetStartup(true);

    [RelayCommand]
    private void RemoveStartup() => SetStartup(false);

    private void SetStartup(bool enabled)
    {
        // One UAC prompt (the task starts CHC with the user's highest rights); declined, nothing changes.
        StartupService.Set(enabled, IsElevated);
        RefreshHistory();
    }

    [RelayCommand]
    private static void ShowWelcome() => (Application.Current as App)?.ShowWelcome();

    [RelayCommand]
    private void OpenHistoryFolder() => ShellService.Open(System.IO.Path.GetDirectoryName(Core.History.HistoryStore.DefaultPath)!);

    [RelayCommand]
    private void ClearHistory()
    {
        if (_main.History is not { } history) return;
        var answer = System.Windows.MessageBox.Show(Loc.T("settings.history.clear.confirm"), "CHC",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        history.Clear();
        RefreshHistory();
    }

    [RelayCommand]
    private static void OpenUrl(string url) => ShellService.Open(url);

    [RelayCommand]
    private static void OpenLanguagesFolder()
    {
        var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Languages");
        System.IO.Directory.CreateDirectory(folder);
        ShellService.Open(folder);
    }

    private void BuildOptions()
    {
        _loading = true;
        try
        {
            Themes.Clear();
            Themes.Add(new(ThemeService.System, Loc.T("settings.theme.system")));
            Themes.Add(new(ThemeService.Light, Loc.T("settings.theme.light")));
            Themes.Add(new(ThemeService.Dark, Loc.T("settings.theme.dark")));
            SelectedTheme = Themes.FirstOrDefault(t => t.Value == _settings.Theme) ?? Themes[0];

            RefreshIntervals.Clear();
            foreach (var seconds in new[] { 1, 2, 5, 10 })
                RefreshIntervals.Add(new(seconds, Loc.T("settings.refresh.seconds", seconds)));
            SelectedRefresh = RefreshIntervals.FirstOrDefault(r => r.Value == _settings.RefreshSeconds) ?? RefreshIntervals[1];

            DetailPeriods.Clear();
            foreach (var days in new[] { 30, 90, 180, 365 })
                DetailPeriods.Add(new(days, Loc.T("settings.history.detail.days", days)));
            SelectedDetail = DetailPeriods.FirstOrDefault(d => d.Value == _settings.HistoryDetailDays) ?? DetailPeriods[1];

            CloseChoices.Clear();
            CloseChoices.Add(new(CloseActions.Ask, Loc.T("settings.close.ask")));
            CloseChoices.Add(new(CloseActions.Background, Loc.T("settings.close.background")));
            CloseChoices.Add(new(CloseActions.Exit, Loc.T("settings.close.exit")));
            SelectedCloseChoice = CloseChoices.FirstOrDefault(c => c.Value == _settings.CloseAction) ?? CloseChoices[0];

            SelectedLanguage = Languages.FirstOrDefault(l => l.Code == Loc.Instance.CurrentLanguage);
        }
        finally
        {
            _loading = false;
        }
    }
}
