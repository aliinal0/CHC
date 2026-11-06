using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CHC.App.Services;

/// <summary>
/// Light and dark: CHC's own palette (Themes/Palette.*.xaml) on a solid background, with its own accent and font,
/// so the app looks the same on every Windows version and, later, on other systems.
/// </summary>
public static class ThemeService
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>CHC's own accent (the blue of its logo) instead of the Windows accent colour, in both themes.</summary>
    public static readonly global::System.Windows.Media.Color Accent = global::System.Windows.Media.Color.FromRgb(0x2B, 0x8F, 0xE0);

    /// <summary>Inter, bundled; Segoe UI and Microsoft YaHei UI only fill in characters it lacks (Chinese).</summary>
    public static readonly FontFamily Font = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Inter, Segoe UI, Microsoft YaHei UI");

    private static bool _watching;
    private static bool _hooked;

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
            SystemThemeWatcher.Watch(window, WindowBackdropType.None, updateAccents: false);
            _watching = true;
        }
    }

    private static void ApplyTheme(string theme)
    {
        var resources = Application.Current.Resources;
        // WPF-UI's own styles look this one up; the window passes Font on to everything else.
        resources["ContentControlThemeFontFamily"] = Font;
        resources["AppFont"] = Font;

        var target = theme switch
        {
            Light => ApplicationTheme.Light,
            Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() is SystemTheme.Light or SystemTheme.Sunrise or SystemTheme.HCWhite
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark,
        };
        // The palette first: WPF-UI reads the background while it applies its theme.
        ApplyPalette(target);
        ApplicationThemeManager.Apply(target, WindowBackdropType.None, updateAccent: false);
        ApplyAccent(target);

        // Windows switching between light and dark (with "Use Windows setting") keeps the palette and accent too.
        if (!_hooked)
        {
            ApplicationThemeManager.Changed += (current, _) =>
            {
                ApplyPalette(current);
                ApplyAccent(current);
            };
            _hooked = true;
        }
    }

    /// <summary>
    /// The logo's blue as a solid fill with white text in both themes (Windows tints it pale with dark text in the
    /// dark theme); links and accent text get a lighter blue on dark backgrounds so they stay readable.
    /// </summary>
    private static void ApplyAccent(ApplicationTheme theme)
    {
        static global::System.Windows.Media.Color Rgb(uint rgb) =>
            global::System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (theme == ApplicationTheme.Light)
            ApplicationAccentColorManager.Apply(Accent, Rgb(0x2380D0), Accent, Rgb(0x1F74BE));
        else
            ApplicationAccentColorManager.Apply(Accent, Rgb(0x4DA3EB), Accent, Rgb(0x6DB6F0));

        // WPF-UI picks dark text when the accent is light enough; CHC's blue always takes white.
        var resources = Application.Current.Resources;
        foreach (var (key, alpha) in new[] { ("TextOnAccentFillColorPrimary", (byte)0xFF), ("TextOnAccentFillColorSecondary", (byte)0xB3), ("TextOnAccentFillColorDisabled", (byte)0x87) })
        {
            var white = global::System.Windows.Media.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);
            resources[key] = white;
            resources[key + "Brush"] = new SolidColorBrush(white);
        }
    }

    /// <summary>Puts the palette last, so it wins over WPF-UI's theme, and points the windows back at it.</summary>
    private static void ApplyPalette(ApplicationTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var name = theme == ApplicationTheme.Light ? "Light" : "Dark";
        var current = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Palette.", StringComparison.Ordinal) == true);
        if (current?.Source?.OriginalString.EndsWith($"Palette.{name}.xaml", StringComparison.Ordinal) == true && dictionaries[^1] == current) return;
        if (current is not null) dictionaries.Remove(current);
        dictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/Palette.{name}.xaml") });
        foreach (Window window in Application.Current.Windows)
            window.SetResourceReference(Window.BackgroundProperty, "PageBrush");
    }
}
