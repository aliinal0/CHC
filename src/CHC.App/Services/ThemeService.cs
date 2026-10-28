using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CHC.App.Services;

public static class ThemeService
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    private static bool _watching;

    public static void Apply(string theme, Window window)
    {
        // SystemThemeWatcher only works with a loaded window (it hooks the window's message loop).
        if (!window.IsLoaded)
        {
            ApplyTheme(theme);
            window.Loaded += OnLoaded;
            return;

            void OnLoaded(object? sender, RoutedEventArgs e)
            {
                window.Loaded -= OnLoaded;
                Apply(theme, window);
            }
        }

        if (_watching)
        {
            SystemThemeWatcher.UnWatch(window);
            _watching = false;
        }
        ApplyTheme(theme);
        if (theme is not (Light or Dark))
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica);
            _watching = true;
        }
    }

    private static void ApplyTheme(string theme)
    {
        switch (theme)
        {
            case Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica);
                break;
            case Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }
}
