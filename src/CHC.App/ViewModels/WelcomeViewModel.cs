using CHC.App.Services;
using CHC.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace CHC.App.ViewModels;

/// <summary>A line on a page of the tour.</summary>
public sealed record WelcomeRow(SymbolRegular Icon, string Name, string Text);

/// <summary>A page of the tour.</summary>
public sealed record WelcomeTourPage(string Title, string Text, IReadOnlyList<WelcomeRow> Rows);

/// <summary>One of the dots that show how far along the guide is.</summary>
public sealed record WelcomeDot(bool Active);

/// <summary>
/// The guide on first start: what CHC is, a short tour anyone can skip, and the settings worth a choice, each with a note on
/// what it does and why someone would want it. Whatever is skipped keeps its default, and all of it can be changed in Settings.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private const int Intro = 0;
    private const int FirstSettings = 4;
    private const int SecondSettings = 5;
    private const int ThirdSettings = 6;
    private const int Done = 7;

    private readonly MainViewModel _main;
    private readonly IReadOnlyList<WelcomeTourPage> _tour;
    private bool _startWithWindows;
    private bool _finished;

    public WelcomeViewModel(MainViewModel main)
    {
        _main = main;
        _readNotes = main.ReadRecordsAutomatically;
        _tour =
        [
            new(Loc.T("welcome.tour1.title"), Loc.T("welcome.tour1.text"),
            [
                new(SymbolRegular.Home24, Loc.T("nav.dashboard"), Loc.T("welcome.tour1.overview")),
                new(SymbolRegular.DeveloperBoard24, Loc.T("welcome.tour1.parts.name"), Loc.T("welcome.tour1.parts")),
                new(SymbolRegular.HeartPulse24, Loc.T("nav.stability"), Loc.T("welcome.tour1.stability")),
                new(SymbolRegular.Lightbulb24, Loc.T("welcome.tour1.fix.name"), Loc.T("welcome.tour1.fix")),
            ]),
            new(Loc.T("welcome.tour2.title"), Loc.T("welcome.tour2.text"),
            [
                new(SymbolRegular.DataTrending24, Loc.T("nav.history"), Loc.T("welcome.tour2.history")),
                new(SymbolRegular.Timeline24, Loc.T("nav.timeline"), Loc.T("welcome.tour2.changes")),
                new(SymbolRegular.AppsList24, Loc.T("nav.programs"), Loc.T("welcome.tour2.programs")),
                new(SymbolRegular.History24, Loc.T("nav.lookback"), Loc.T("welcome.tour2.lookback")),
            ]),
            new(Loc.T("welcome.tour3.title"), Loc.T("welcome.tour3.text"),
            [
                new(SymbolRegular.TopSpeed24, Loc.T("nav.performance"), Loc.T("welcome.tour3.games")),
                new(SymbolRegular.Globe24, Loc.T("nav.network"), Loc.T("welcome.tour3.network")),
                new(SymbolRegular.PlugConnected24, Loc.T("nav.devices"), Loc.T("welcome.tour3.devices")),
                new(SymbolRegular.DocumentText24, Loc.T("nav.report"), Loc.T("welcome.tour3.report")),
            ]),
        ];
        if (!main.IsDemo) _ = LoadStartupAsync();
    }

    /// <summary>The guide is through, and it is time to close.</summary>
    public event Action? Finished;

    public SettingsViewModel Settings => _main.Settings;

    public IReadOnlyList<WelcomeRow> IntroRows { get; } =
    [
        new(SymbolRegular.ChatBubblesQuestion24, Loc.T("welcome.point.plain.name"), Loc.T("welcome.point.plain")),
        new(SymbolRegular.ShieldCheckmark24, Loc.T("welcome.point.private.name"), Loc.T("welcome.point.private")),
        new(SymbolRegular.Timer24, Loc.T("welcome.point.quick.name"), Loc.T("welcome.point.quick")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIntro), nameof(IsTour), nameof(IsFirstSettings), nameof(IsSecondSettings), nameof(IsThirdSettings), nameof(IsDone), nameof(Tour), nameof(ShowBack), nameof(ShowSkip), nameof(SkipText), nameof(NextText), nameof(Dots))]
    private int _page;

    public bool IsIntro => Page == Intro;
    public bool IsTour => Page is > Intro and < FirstSettings;
    public bool IsFirstSettings => Page == FirstSettings;
    public bool IsSecondSettings => Page == SecondSettings;
    public bool IsThirdSettings => Page == ThirdSettings;
    public bool IsDone => Page == Done;
    public WelcomeTourPage? Tour => IsTour ? _tour[Page - 1] : null;
    public bool ShowBack => Page is > Intro and < Done;
    public bool ShowSkip => Page < Done;
    public string SkipText => Loc.T(Page < FirstSettings ? "welcome.skip.tour" : "welcome.skip.settings");

    public string NextText => Loc.T(Page switch
    {
        Intro => "welcome.next.start",
        FirstSettings - 1 => "welcome.next.settings",
        ThirdSettings => "welcome.next.finish",
        Done => "welcome.next.done",
        _ => "welcome.next",
    });

    public IReadOnlyList<WelcomeDot> Dots => Enumerable.Range(0, Done + 1).Select(i => new WelcomeDot(i == Page)).ToList();

    public bool IsElevated => _main.IsElevated;
    public bool CanChangeSystem => !_main.IsDemo;

    /// <summary>The sign-in task. Changing it takes effect at once: it is Windows that asks for permission, right then.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value) || _main.IsDemo) return;
            _ = ApplyStartupAsync(value);
        }
    }

    [ObservableProperty]
    private bool _restartAsAdmin;

    [ObservableProperty]
    private bool _readNotes;

    private async Task LoadStartupAsync()
    {
        var registered = await Task.Run(StartupService.IsRegistered);
        SetProperty(ref _startWithWindows, registered, nameof(StartWithWindows));
    }

    private async Task ApplyStartupAsync(bool enabled)
    {
        var elevated = _main.IsElevated;
        var registered = await Task.Run(() =>
        {
            StartupService.Set(enabled, elevated);
            return StartupService.IsRegistered();
        });

        // Declined: the switch goes back to what is really so.
        SetProperty(ref _startWithWindows, registered, nameof(StartWithWindows));
        Settings.RefreshHistory();
    }

    [RelayCommand]
    private void Next()
    {
        if (Page >= Done)
        {
            _finished = true;
            Finished?.Invoke();
        }
        else
        {
            Page++;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Page > Intro) Page--;
    }

    /// <summary>Past the tour to the settings, or past the settings to the end. Nothing skipped changes anything.</summary>
    [RelayCommand]
    private void Skip() => Page = Page < FirstSettings ? FirstSettings : Done;

    /// <summary>What was chosen for after the guide: only now, because these ask for permission or restart CHC.</summary>
    public void Apply()
    {
        if (!_finished || _main.IsDemo) return;
        if (ReadNotes != _main.ReadRecordsAutomatically) _main.ReadRecordsAutomatically = ReadNotes;

        // The new copy reads the notes by itself, and without a question: it is already administrator then.
        if (RestartAsAdmin && !_main.IsElevated && ShellService.RestartAsAdministrator()) return;
        if (ReadNotes && _main.RecordsReadAt is null) _main.LookBack.ReadFromGuide();
    }
}
