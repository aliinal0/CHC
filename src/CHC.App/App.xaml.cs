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

        var settings = SettingsService.Load();
        Loc.Instance.SetLanguage(settings.Language ?? CultureInfo.CurrentUICulture.Name);

        _main = new MainViewModel(settings);
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
