using System.Globalization;
using Microsoft.Win32;

namespace CHC.Core.Performance;

public enum GameDisplayMode { Fullscreen, Borderless, Windowed }

/// <summary>A graphics setting read from the game's own settings file.</summary>
/// <param name="Setting">"shadows", "lighting", "reflections", "effects", "postprocess", "antialiasing", "shading",
/// "materials", "textures", "viewdistance", "foliage" or "details" (also the localization key "gameset.&lt;setting&gt;").</param>
/// <param name="Scale">How the game names its levels: "ue" (Low…Epic, Cinematic), "std" (Low…Very high) or "onoff".</param>
public sealed record GameQuality(string Setting, int Level, string Scale)
{
    /// <summary>High or above (or switched on): the levels worth lowering first.</summary>
    public bool IsHigh => Scale == "onoff" ? Level >= 1 : Level >= 2;

    public string LevelKey => "gameset.level." + (Scale, Level) switch
    {
        ("onoff", 0) => "off",
        ("onoff", _) => "on",
        (_, <= 0) => "low",
        (_, 1) => "medium",
        (_, 2) => "high",
        ("ue", 3) => "epic",
        ("ue", _) => "cinematic",
        _ => "veryhigh",
    };
}

/// <summary>
/// The graphics settings a game saved for itself. Only the values are kept, never the file's path:
/// some games put the player's account id in it.
/// </summary>
public sealed class GameSettings
{
    /// <summary>Where they came from: "Unreal Engine", "VALORANT", "Unity", "Counter-Strike 2" or "Minecraft".</summary>
    public string Source { get; set; } = "";
    public string File { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public GameDisplayMode? DisplayMode { get; set; }
    public bool? VSync { get; set; }

    /// <summary>The game's own frame limit; 0 = no limit.</summary>
    public int? FrameLimit { get; set; }

    /// <summary>Render resolution in percent of the output resolution (100 = native).</summary>
    public double? RenderScale { get; set; }

    /// <summary>"DLSS", "FSR", "XeSS", "TSR", or empty when off. Only meaningful when <see cref="UpscalerKnown"/>.</summary>
    public string Upscaler { get; set; } = "";
    public bool UpscalerKnown { get; set; }
    public bool? Reflex { get; set; }
    public List<GameQuality> Quality { get; set; } = [];

    /// <summary>Minecraft: chunks drawn around the player, and chunks the game keeps simulating.</summary>
    public int? RenderDistance { get; set; }
    public int? SimulationDistance { get; set; }

    /// <summary>Settings that mostly load the graphics card, and those that mostly load the processor.</summary>
    public static readonly string[] GpuHeavy = ["shadows", "lighting", "reflections", "effects", "postprocess", "antialiasing", "shading", "materials"];
    public static readonly string[] CpuHeavy = ["viewdistance", "foliage", "details"];
}

/// <summary>The game's window and the monitor's mode, sampled while the game was in front.</summary>
public sealed class GameDisplay
{
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    /// <summary>The window fills its monitor: fullscreen or borderless.</summary>
    public bool CoversMonitor { get; set; }
    public bool HasTitleBar { get; set; }

    /// <summary>The monitor's mode during the game (exclusive fullscreen can change it).</summary>
    public int ModeWidth { get; set; }
    public int ModeHeight { get; set; }
    public int RefreshHz { get; set; }

    /// <summary>The monitor's largest resolution and the highest refresh rate it offers there.</summary>
    public int NativeWidth { get; set; }
    public int NativeHeight { get; set; }
    public int MaxRefreshHz { get; set; }
}

/// <summary>
/// Finds and reads the settings file of the game that was measured. Unreal Engine games keep
/// GameUserSettings.ini in %LOCALAPPDATA%\&lt;Project&gt;\Saved\Config, VALORANT adds RiotUserSettings.ini,
/// Unity games use the registry, Counter-Strike 2 a file in Steam's user data and Minecraft options.txt.
/// </summary>
public static class GameSettingsReader
{
    public static GameSettings? Read(string exePath, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            if (name is "javaw" or "java" && windowTitle.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase)) return Minecraft();
            if (name.Equals("cs2", StringComparison.OrdinalIgnoreCase)) return CounterStrike2();
            return Unreal(exePath) ?? Unity(exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Unreal Engine

    private static readonly (string Key, string Setting)[] UnrealQuality =
    [
        ("sg.ShadowQuality", "shadows"), ("sg.GlobalIlluminationQuality", "lighting"), ("sg.ReflectionQuality", "reflections"),
        ("sg.EffectsQuality", "effects"), ("sg.PostProcessQuality", "postprocess"), ("sg.AntiAliasingQuality", "antialiasing"),
        ("sg.ShadingQuality", "shading"), ("sg.TextureQuality", "textures"), ("sg.ViewDistanceQuality", "viewdistance"),
        ("sg.FoliageQuality", "foliage"),
    ];

    /// <summary>&lt;Game&gt;\&lt;Project&gt;\Binaries\Win64\&lt;Name&gt;.exe keeps its settings under the project's or the exe's name.</summary>
    private static GameSettings? Unreal(string exePath)
    {
        var binaries = Directory.GetParent(exePath)?.Parent;
        if (binaries is null || !binaries.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase)) return null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[] { binaries.Parent?.Name, Path.GetFileNameWithoutExtension(exePath).Split('-')[0] }
            .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => Path.Combine(local, n!, "Saved", "Config"))
            .Where(Directory.Exists)
            .ToList();
        var deep = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        var file = roots.SelectMany(r => Directory.EnumerateFiles(r, "GameUserSettings.ini", deep))
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (file is null) return null;

        var ini = Ini(file.FullName);
        var s = new GameSettings { Source = "Unreal Engine", File = file.Name };
        s.Width = Int(ini, "ResolutionSizeX");
        s.Height = Int(ini, "ResolutionSizeY");
        s.DisplayMode = (Int(ini, "FullscreenMode") ?? Int(ini, "LastConfirmedFullscreenMode") ?? Int(ini, "PreferredFullscreenMode")) switch
        {
            0 => GameDisplayMode.Fullscreen,
            1 => GameDisplayMode.Borderless,
            2 => GameDisplayMode.Windowed,
            _ => null,
        };
        s.VSync = Bool(ini, "bUseVSync");
        // PUBG names its limit; elsewhere FrameRateLimit, where 0 (or a huge number) means none.
        s.FrameLimit = ini.GetValueOrDefault("InGameFrameRateLimitType") switch
        {
            "Unlimited" => 0,
            "Custom" => Int(ini, "InGameCustomFrameRateLimit"),
            _ => Double(ini, "FrameRateLimit") is { } limit ? limit is <= 0 or >= 500 ? 0 : (int)Math.Round(limit) : null,
        };
        // Unreal games write 0 until someone touches this setting: that means the default, not a render scale of nothing.
        s.RenderScale = Double(ini, "sg.ResolutionQuality") is > 0 and var quality ? quality : null;
        if (ini.TryGetValue("FortAntiAliasingMethod", out var method))
        {
            s.UpscalerKnown = true;
            s.Upscaler = method.StartsWith("DLSS", StringComparison.OrdinalIgnoreCase) ? "DLSS"
                : method.StartsWith("TSR", StringComparison.OrdinalIgnoreCase) ? "TSR"
                : method.StartsWith("FSR", StringComparison.OrdinalIgnoreCase) ? "FSR"
                : method.StartsWith("XeSS", StringComparison.OrdinalIgnoreCase) ? "XeSS" : "";
        }
        else if (Bool(ini, "bNvidiaDLSS") is { } dlss)
        {
            s.UpscalerKnown = true;
            s.Upscaler = dlss ? "DLSS" : "";
        }
        foreach (var (key, setting) in UnrealQuality)
            if (Int(ini, key) is { } level) s.Quality.Add(new GameQuality(setting, level, "ue"));

        // VALORANT saves its real graphics options in RiotUserSettings.ini; its sg.* values are left over.
        var riot = roots.SelectMany(r => Directory.EnumerateFiles(r, "RiotUserSettings.ini", deep))
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (riot is not null) Valorant(s, Ini(riot.FullName));
        return s;
    }

    private static void Valorant(GameSettings s, Dictionary<string, string> riot)
    {
        s.Source = "VALORANT";
        s.File = "RiotUserSettings.ini";
        s.Quality.Clear();
        foreach (var (key, setting) in new[] { ("MaterialQuality", "materials"), ("TextureQuality", "textures"), ("DetailQuality", "details") })
            if (Int(riot, "EAresIntSettingName::" + key) is { } level) s.Quality.Add(new GameQuality(setting, level, "std"));
        if (Bool(riot, "EAresBoolSettingName::ShadowsEnabled") is { } shadows) s.Quality.Add(new GameQuality("shadows", shadows ? 1 : 0, "onoff"));
        if (Int(riot, "EAresIntSettingName::NvidiaReflexLowLatencySetting") is { } reflex) s.Reflex = reflex > 0;
        s.RenderScale = null;
    }

    // ------------------------------------------------------------------ Unity

    /// <summary>&lt;Name&gt;_Data\app.info holds the company and product, which name the game's registry key.</summary>
    private static GameSettings? Unity(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var info = Path.Combine(dir, Path.GetFileNameWithoutExtension(exePath) + "_Data", "app.info");
        if (!System.IO.File.Exists(info)) return null;
        var lines = System.IO.File.ReadAllLines(info);
        if (lines.Length < 2) return null;
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\{lines[0].Trim()}\{lines[1].Trim()}");
        if (key is null) return null;
        int? Value(string prefix) => key.GetValueNames().FirstOrDefault(n => n.StartsWith(prefix + "_h", StringComparison.Ordinal)) is { } n && key.GetValue(n) is int v ? v : null;

        var s = new GameSettings { Source = "Unity", File = "registry" };
        s.Width = Value("Screenmanager Resolution Width");
        s.Height = Value("Screenmanager Resolution Height");
        // Unity: 0 exclusive fullscreen, 1 fullscreen window, 2 maximized window, 3 window. Older games: "Is Fullscreen mode" 1/0.
        s.DisplayMode = Value("Screenmanager Fullscreen mode") switch
        {
            0 => GameDisplayMode.Fullscreen,
            1 => GameDisplayMode.Borderless,
            2 or 3 => GameDisplayMode.Windowed,
            _ => Value("Screenmanager Is Fullscreen mode") switch { 1 => GameDisplayMode.Fullscreen, 0 => GameDisplayMode.Windowed, _ => null },
        };
        return s.Width is null && s.DisplayMode is null ? null : s;
    }

    // ------------------------------------------------------------------ Counter-Strike 2

    /// <summary>Steam\userdata\&lt;account&gt;\730\local\cfg\cs2_video.txt, lines like "setting.defaultres"  "1920".</summary>
    private static GameSettings? CounterStrike2()
    {
        using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (steamKey?.GetValue("SteamPath") is not string steam) return null;
        var userdata = Path.Combine(steam.Replace('/', '\\'), "userdata");
        if (!Directory.Exists(userdata)) return null;
        var file = Directory.EnumerateDirectories(userdata)
            .Select(d => new FileInfo(Path.Combine(d, "730", "local", "cfg", "cs2_video.txt")))
            .Where(f => f.Exists).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (file is null) return null;

        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in System.IO.File.ReadLines(file.FullName))
        {
            var parts = line.Split('"');
            if (parts.Length >= 4) kv.TryAdd(parts[1].Trim(), parts[3].Trim());
        }
        var s = new GameSettings { Source = "Counter-Strike 2", File = file.Name };
        s.Width = Int(kv, "setting.defaultres");
        s.Height = Int(kv, "setting.defaultresheight");
        s.DisplayMode = Int(kv, "setting.fullscreen") == 1 ? GameDisplayMode.Fullscreen
            : Int(kv, "setting.nowindowborder") == 1 ? GameDisplayMode.Borderless
            : Int(kv, "setting.fullscreen") == 0 ? GameDisplayMode.Windowed : null;
        s.VSync = Int(kv, "setting.mat_vsync") is { } vsync ? vsync != 0 : null;
        if (Int(kv, "setting.r_low_latency") is { } reflex) s.Reflex = reflex > 0;
        if (Int(kv, "setting.videocfg_fsr_detail") is { } fsr)
        {
            s.UpscalerKnown = true;
            s.Upscaler = fsr > 0 ? "FSR" : "";
        }
        foreach (var (key, setting) in new[] { ("setting.videocfg_shadow_quality", "shadows"), ("setting.videocfg_particle_detail", "effects"), ("setting.videocfg_ao_detail", "lighting"), ("setting.videocfg_texture_detail", "textures") })
            if (Int(kv, key) is { } level) s.Quality.Add(new GameQuality(setting, level, "std"));
        return s;
    }

    // ------------------------------------------------------------------ Minecraft (Java Edition)

    private static GameSettings? Minecraft()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "options.txt");
        if (!System.IO.File.Exists(path)) return null;
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in System.IO.File.ReadLines(path))
            if (line.IndexOf(':') is > 0 and var colon) kv.TryAdd(line[..colon], line[(colon + 1)..].Trim().Trim('"'));

        var s = new GameSettings { Source = "Minecraft", File = "options.txt" };
        s.VSync = Bool(kv, "enableVsync");
        s.DisplayMode = Bool(kv, "fullscreen") switch { true => GameDisplayMode.Fullscreen, false => GameDisplayMode.Windowed, _ => null };
        // 260 is the slider's "Unlimited".
        s.FrameLimit = Int(kv, "maxFps") is { } fps ? fps >= 260 ? 0 : fps : null;
        s.RenderDistance = Int(kv, "renderDistance");
        s.SimulationDistance = Int(kv, "simulationDistance");
        return s;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>key=value pairs of an .ini file; the first occurrence of a key wins.</summary>
    private static Dictionary<string, string> Ini(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in System.IO.File.ReadLines(path))
            if (line.IndexOf('=') is > 0 and var eq && !line.StartsWith(';')) result.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        return result;
    }

    private static double? Double(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;

    private static int? Int(Dictionary<string, string> d, string key) => Double(d, key) is { } v ? (int)Math.Round(v) : null;

    private static bool? Bool(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ? true
            : v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" ? false : null : null;
}
