using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CHC.Core.Updates;

/// <summary>What an update is for. The numbers are kept in a file, so a new kind goes at the end.</summary>
public enum UpdateKind { GraphicsDriver, ChipsetDriver, WindowsDriver, Windows, Runtime, Program, DeviceDriver }

/// <summary>Something with a newer version than the one on this PC.</summary>
public sealed class AvailableUpdate
{
    public UpdateKind Kind { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Empty when it isn't installed at all, or when only Windows Update knows.</summary>
    public string Installed { get; set; } = "";
    public string Latest { get; set; } = "";
    public DateTime? Released { get; set; }
    public string? Url { get; set; }

    /// <summary>For a device's driver: which hardware it is, to see later that the driver was installed.</summary>
    public string? HardwareId { get; set; }

    /// <summary>The order they are shown in: drivers first, then Windows, then the suggestions.</summary>
    [JsonIgnore]
    public int Rank => Kind switch
    {
        UpdateKind.GraphicsDriver => 0,
        UpdateKind.ChipsetDriver => 1,
        UpdateKind.DeviceDriver => 2,
        UpdateKind.WindowsDriver => 3,
        UpdateKind.Windows => 4,
        UpdateKind.Runtime => 5,
        _ => 6,
    };
}

/// <summary>Where NVIDIA's lookup keeps a card, so it needs finding only once.</summary>
public sealed class NvidiaCard
{
    public string Name { get; set; } = "";
    public string Series { get; set; } = "";
    public string Product { get; set; } = "";
}

/// <summary>What the last look for updates found. Kept in a file, so it is there without going online at every start.</summary>
public sealed class UpdateInfo
{
    public DateTime? CheckedAt { get; set; }

    /// <summary>Some source couldn't be reached: what it said last time is still in the list.</summary>
    public bool Incomplete { get; set; }

    public List<AvailableUpdate> Items { get; set; } = [];

    /// <summary>Graphics cards whose newest driver could be looked up, so their driver's age says nothing any more.</summary>
    public List<string> KnownGraphics { get; set; } = [];

    public List<NvidiaCard> NvidiaCards { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<AvailableUpdate> Runtimes => Items.Where(i => i.Kind == UpdateKind.Runtime).ToList();

    [JsonIgnore]
    public IReadOnlyList<AvailableUpdate> Programs => Items.Where(i => i.Kind == UpdateKind.Program).ToList();
}

public static class UpdateVersions
{
    /// <summary>Numbers compared part by part ("8.09.28" is 8.9.28). False if either isn't a version.</summary>
    public static bool IsNewer(string latest, string installed) =>
        Version.TryParse(Clean(latest), out var a) && Version.TryParse(Clean(installed), out var b) && a > b;

    private static string Clean(string version) => version.Contains('.') ? version.Trim() : version.Trim() + ".0";
}

/// <summary>%LocalAppData%\CHC\updates.json</summary>
public static class UpdateStore
{
    private static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHC", "updates.json");

    public static UpdateInfo? Load()
    {
        try { return JsonSerializer.Deserialize<UpdateInfo>(File.ReadAllText(File_)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Save(UpdateInfo info)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(info));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* it looks again next time */ }
    }
}
