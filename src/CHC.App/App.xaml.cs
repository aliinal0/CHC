using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CHC.App.Services;
using CHC.App.ViewModels;
using CHC.App.Views;
using CHC.Core.Collection;
using CHC.Core.Localization;

namespace CHC.App;

public partial class App : Application
{
    private MainViewModel? _main;
    private MainWindow? _window;
    private TrayService? _tray;
    private AppSettings? _settings;
    private bool _exiting;

    private bool _forceWelcome;
    private bool _welcomeOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A page's scroll area takes the keyboard focus when the button in use goes away (Start turns into Stop): no outline around the whole page for that.
        FrameworkElement.FocusVisualStyleProperty.OverrideMetadata(typeof(System.Windows.Controls.ScrollViewer), new FrameworkPropertyMetadata(null));
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            Start(e);
        }
        catch (Exception ex)
        {
            // Without a window or a tray icon nobody could ever close it: a start that fails has to end.
            Console.Error.WriteLine(ex);
            MessageBox.Show(ex.Message, "CHC", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void Start(StartupEventArgs e)
    {
        bool Has(string option) => e.Args.Contains(option, StringComparer.OrdinalIgnoreCase);

        // A short-lived copy, started with administrator rights to read Windows' usage records and leave them in a file.
        if (Has(RecordsService.Option))
        {
            var at = Array.FindIndex(e.Args, a => a.Equals(RecordsService.Option, StringComparison.OrdinalIgnoreCase));
            Shutdown(at + 1 < e.Args.Length && RecordsService.RunChild(e.Args[at + 1]) ? 0 : 1);
            return;
        }

        // The installer sets up (and its uninstaller removes) the sign-in task this way, then CHC quits.
        if (Has("--register-startup") || Has("--unregister-startup"))
        {
            Shutdown(StartupService.Set(Has("--register-startup"), SnapshotCollector.IsElevated) ? 0 : 1);
            return;
        }

        // Command line: --demo shows a sample PC (--demo-laptop one with a battery), --lang <code> picks a language
        // for this run only (--theme light|dark the theme), --screenshot hides the demo badge for README images,
        // --background starts in the notification area (the sign-in task) and only records until the window is opened,
        // --welcome shows the guide even if it was seen before, also in demo mode.
        var laptop = Has("--demo-laptop");
        var demo = laptop || Has("--demo");
        SettingsService.ReadOnly = demo;
        _forceWelcome = Has("--welcome");
        var screenshot = Has("--screenshot");
        var background = Has("--background") && !demo;
        var langAt = Array.FindIndex(e.Args, a => a.Equals("--lang", StringComparison.OrdinalIgnoreCase));
        var language = langAt >= 0 && langAt + 1 < e.Args.Length ? e.Args[langAt + 1] : null;
        var themeAt = Array.FindIndex(e.Args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        var theme = themeAt >= 0 && themeAt + 1 < e.Args.Length ? e.Args[themeAt + 1] : null;

        // One CHC per session: a second start shows the running one (unless it's the sign-in task starting twice).
        // Demo copies are independent; they never touch the real history.
        if (!demo && !SingleInstance.TryAcquire(signalOther: !background))
        {
            Shutdown();
            return;
        }

        _settings = SettingsService.Load();
        if (background && !_settings.RecordHistory)
        {
            Shutdown();
            return;
        }
        // A language chosen in the app wins over the one picked in the installer, which wins over Windows'.
        Loc.Instance.SetLanguage(language ?? _settings.Language ?? CultureInfo.CurrentUICulture.Name);

        // Signing out, shutting down, or the installer closing CHC for an update: quit for real, not into the tray.
        SessionEnding += (_, _) => ExitApp();

        _main = new MainViewModel(_settings, demo, laptop) { ShowDemoBadge = demo && !screenshot };
        _main.PropertyChanged += OnMainPropertyChanged;
        _window = new MainWindow { DataContext = _main };
        _window.Closing += OnWindowClosing;
        ThemeService.Apply(theme switch
        {
            _ when theme?.Equals(ThemeService.Light, StringComparison.OrdinalIgnoreCase) == true => ThemeService.Light,
            _ when theme?.Equals(ThemeService.Dark, StringComparison.OrdinalIgnoreCase) == true => ThemeService.Dark,
            _ => _settings.Theme,
        }, _window);
        MainWindow = _window;

        if (!demo)
        {
            SingleInstance.OnShowRequested(() => Dispatcher.BeginInvoke(ShowWindow));
            UpdateTray();
            _main.StartRecording();
        }
        if (!background) ShowWindow();
    }

    /// <summary>Shows the window (first start, the tray icon, or a second start from the Start menu).</summary>
    private void ShowWindow()
    {
        if (_window is null || _main is null || _exiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _main.SetWindowVisible(true);
        ShowWelcomeIfDue();
    }

    /// <summary>The guide comes once, when the window is first seen: a start from the sign-in task waits until someone opens it.</summary>
    private void ShowWelcomeIfDue()
    {
        if (_settings is null || _main is null) return;
        if (!_forceWelcome && (_main.IsDemo || _settings.WelcomeDone)) return;
        Dispatcher.BeginInvoke(new Action(ShowWelcome), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Shows the welcome guide over the window. It counts as seen however it is left.</summary>
    public void ShowWelcome()
    {
        if (_welcomeOpen || _window is null || _main is null || _settings is null || _exiting) return;
        _welcomeOpen = true;
        _forceWelcome = false;
        var guide = new WelcomeViewModel(_main);
        new WelcomeWindow(guide) { Owner = _window }.ShowDialog();
        _welcomeOpen = false;
        _settings.WelcomeDone = true;
        SettingsService.Save(_settings);
        guide.Apply();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _main is null || _window is null || _settings is null) return;
        if (_tray is null)
        {
            ExitApp();
            return;
        }

        // Recording: the window can go while CHC goes on in the notification area, so the choice is the user's to make.
        var action = _settings.CloseAction;
        if (action is not (CloseActions.Background or CloseActions.Exit))
        {
            var dialog = new CloseDialog { Owner = _window };
            dialog.ShowDialog();
            if (dialog.Choice is not { } choice)
            {
                e.Cancel = true;
                return;
            }
            action = choice;
            if (dialog.Remember)
            {
                _settings.CloseAction = choice;
                SettingsService.Save(_settings);
                _main.Settings.RefreshCloseAction();
            }
        }

        if (action == CloseActions.Exit)
        {
            ExitApp();
            return;
        }
        e.Cancel = true;
        _window.Hide();
        _main.SetWindowVisible(false);
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.RecordHistory) or nameof(MainViewModel.RecordingPaused)) UpdateTray();
    }

    /// <summary>The tray icon is there while CHC records (or is paused), so the window can be closed.</summary>
    private void UpdateTray()
    {
        if (_main is null) return;
        if (_main.RecordHistory && !_main.IsDemo)
        {
            _tray ??= new TrayService(ShowWindow, paused => _main.RecordingPaused = paused, ExitApp);
            _tray.Paused = _main.RecordingPaused;
        }
        else
        {
            _tray?.Dispose();
            _tray = null;
            // With nothing to record, a hidden window would just linger.
            if (_window is { IsVisible: false }) ExitApp();
        }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _main?.Dispose();
        SingleInstance.Release();
        base.OnExit(e);
    }

    private static bool _showingError;

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive; a health checker that crashes is not very reassuring.
        // The message box pumps messages, so a layout error would fire again while it is open;
        // without this guard that recursion ends in a stack overflow.
        e.Handled = true;
        Console.Error.WriteLine(e.Exception);
        if (_showingError) return;
        _showingError = true;
        try { MessageBox.Show(e.Exception.Message, "CHC", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _showingError = false; }
    }
}
