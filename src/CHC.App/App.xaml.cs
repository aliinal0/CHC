using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CHC.App.Services;
using CHC.App.ViewModels;
using CHC.App.Views;
using CHC.Core.Localization;

namespace CHC.App;

public partial class App : Application
{
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Command line: --demo shows a sample PC, --lang <code> picks a language for this run only.
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var langAt = Array.FindIndex(e.Args, a => a.Equals("--lang", StringComparison.OrdinalIgnoreCase));
        var language = langAt >= 0 && langAt + 1 < e.Args.Length ? e.Args[langAt + 1] : null;

        var settings = SettingsService.Load();
        Loc.Instance.SetLanguage(language ?? settings.Language ?? CultureInfo.CurrentUICulture.Name);

        _main = new MainViewModel(settings, demo);
        var window = new MainWindow { DataContext = _main };
        ThemeService.Apply(settings.Theme, window);
        MainWindow = window;
        window.Show();
        _ = _main.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive; a health checker that crashes is not very reassuring.
        MessageBox.Show(e.Exception.Message, "CHC", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
