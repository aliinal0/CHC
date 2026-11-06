using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace CHC.App.Services;

public sealed class AppSettings
{
    /// <summary>The welcome guide has been shown; it comes once, and Settings can show it again.</summary>
    public bool WelcomeDone { get; set; }

    /// <summary>The user asked to keep reading Windows' usage records by themselves, whenever CHC runs as administrator.</summary>
    public bool ReadRecordsAutomatically { get; set; }

    /// <summary>Once a day, look for newer drivers, Windows updates and programs. The only thing CHC goes online for by itself.</summary>
    public bool CheckUpdatesAutomatically { get; set; } = true;

    /// <summary>When Windows' usage records were last read.</summary>
    public DateTime? RecordsReadAt { get; set; }

    /// <summary>Language code ("en", "tr"...). Null means "follow Windows".</summary>
    public string? Language { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = ThemeService.System;

    public int RefreshSeconds { get; set; } = 2;

    /// <summary>Keep a history of temperatures, usage and power; with the window closed CHC records from the notification area.</summary>
    public bool RecordHistory { get; set; } = true;

    /// <summary>Days for which every minute is kept; older history keeps its hourly summary.</summary>
    public int HistoryDetailDays { get; set; } = 90;

    /// <summary>The hint that closing the window keeps CHC recording in the notification area has been shown.</summary>

    /// <summary>What closing the window does while the history records (<see cref="CloseActions"/>).</summary>
    public string CloseAction { get; set; } = CloseActions.Ask;

    /// <summary>The power supply as the user entered it (most supplies can't be read): wattage, 80 PLUS rating, model.</summary>
    public int? PsuWatts { get; set; }
    public string? PsuRating { get; set; }
    public string? PsuModel { get; set; }
}

/// <summary>Stores settings in %AppData%\CHC\settings.json.</summary>
/// <summary>What closing the window can mean while the history records.</summary>
public static class CloseActions
{
    public const string Ask = "Ask";
    public const string Background = "Background";
    public const string Exit = "Exit";
}

public static class SettingsService
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CHC");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>A demo copy shows the same controls, but what is changed there isn't the user's real choice.</summary>
    public static bool ReadOnly { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
