using System.Collections.ObjectModel;
using System.Windows;
using CHC.App.Services;
using CHC.Core.Localization;
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
    public ObservableCollection<Option<string>> Themes { get; } = [];
    public ObservableCollection<Option<int>> RefreshIntervals { get; } = [];

    public string Version => _main.AppVersion;
    public bool IsElevated => _main.IsElevated;
    public MainViewModel Main => _main;

    public IReadOnlyList<ThirdParty> Credits { get; } =
    [
        new("LibreHardwareMonitor", "MPL-2.0", "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"),
        new("DiskInfoToolkit", "MPL-2.0", "https://github.com/Blacktempel/DiskInfoToolkit"),
        new("RAMSPDToolkit", "MPL-2.0", "https://github.com/Blacktempel/RAMSPDToolkit"),
        new("HidSharp", "Apache-2.0", "https://software.seekye.com/hidsharp"),
        new("WPF UI", "MIT", "https://github.com/lepoco/wpfui"),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
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

            SelectedLanguage = Languages.FirstOrDefault(l => l.Code == Loc.Instance.CurrentLanguage);
        }
        finally
        {
            _loading = false;
        }
    }
}
