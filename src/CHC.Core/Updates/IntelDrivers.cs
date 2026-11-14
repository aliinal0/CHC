using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CHC.Core.Updates;

/// <summary>A device as Windows knows it: its hardware id and the driver version in use.</summary>
public sealed record DeviceDriver(string Name, string HardwareId, string Version);

/// <summary>A newer Intel driver for one device.</summary>
public sealed record IntelDriver(DeviceDriver Device, string Category, string Version, DateTime? Released, string Url);

/// <summary>
/// Intel's own list of its current drivers: graphics, Wi-Fi, Bluetooth and network adapters, each with the hardware
/// ids it is for. It is the list Intel's Driver &amp; Support Assistant works from. CHC downloads the whole list and
/// does the matching here, so Intel learns nothing about this PC. Only the list is read: the programs that come
/// in the same archive are never unpacked or run.
/// </summary>
public static class IntelDrivers
{
    private const string Feed = "https://dsadata.intel.com/data/en";
    private const string ListName = "software-configurations.json";
    private const long LargestList = 8 * 1024 * 1024;

    private static readonly string[] DriverCategories = ["Graphics", "Wireless", "Bluetooth", "LAN", "OtherDrivers"];

    public static bool IsIntel(string hardwareId) =>
        hardwareId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || hardwareId.Contains("VID_8087", StringComparison.OrdinalIgnoreCase)
        || hardwareId.Contains("VID_8086", StringComparison.OrdinalIgnoreCase);

    /// <summary>The list itself, as text. Null if the archive doesn't hold one.</summary>
    public static async Task<string?> DownloadAsync(HttpClient http)
    {
        var bytes = await http.GetByteArrayAsync(Feed);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = archive.GetEntry(ListName);
        if (entry is null || entry.Length > LargestList) return null;
        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// For each device, the newest driver in Intel's list that is for it and for this Windows, and whether Intel
    /// lists the device at all. <paramref name="windows11"/> picks which Windows the drivers must be for.
    /// </summary>
    public static (List<IntelDriver> Newer, HashSet<string> Listed) Match(string json, IReadOnlyList<DeviceDriver> devices, bool windows11)
    {
        var newer = new Dictionary<string, IntelDriver>(StringComparer.OrdinalIgnoreCase);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var os = windows11 ? "windows-11-" : "windows-10-";
        bool ForThisWindows(JsonElement names) =>
            names.ValueKind == JsonValueKind.Array && names.EnumerateArray().Any(n => n.GetString() is { } name && name.StartsWith(os, StringComparison.Ordinal) && name.EndsWith("-64", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return ([], listed);
        foreach (var package in document.RootElement.EnumerateArray())
        {
            if (Bool(package, "IsBeta") || Bool(package, "IntelSystemOnly")) continue;
            if (!package.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array
                || !files.EnumerateArray().Any(f => f.TryGetProperty("OperatingSystems", out var names) && ForThisWindows(names))) continue;
            if (!package.TryGetProperty("Components", out var components) || components.ValueKind != JsonValueKind.Array) continue;

            foreach (var component in components.EnumerateArray())
            {
                var category = Text(component, "Category");
                if (!DriverCategories.Contains(category) || Text(component, "DetectionType") != "Default") continue;
                // A component can be for fewer Windows versions than its package.
                if (component.TryGetProperty("OperatingSystemOverrides", out var only) && only.ValueKind == JsonValueKind.Array && only.GetArrayLength() > 0 && !ForThisWindows(only)) continue;
                if (!component.TryGetProperty("DetectionValues", out var values) || values.ValueKind != JsonValueKind.Array) continue;
                var patterns = values.EnumerateArray().Select(v => v.GetString()).Where(v => v is { Length: >= 8 }).Select(v => Pattern(v!)).ToList();
                var version = Text(component, "Version");
                var mask = Text(component, "VersionComparisonMask");

                foreach (var device in devices)
                {
                    if (!patterns.Any(p => p.IsMatch(device.HardwareId))) continue;
                    listed.Add(device.HardwareId);
                    if (version.Length == 0 || !UpdateVersions.IsNewer(Masked(version, mask), Masked(device.Version, mask))) continue;
                    if (newer.TryGetValue(device.HardwareId, out var best) && !UpdateVersions.IsNewer(version, best.Version)) continue;
                    newer[device.HardwareId] = new IntelDriver(device, category, version, Date(package), Page(package));
                }
            }
        }
        return (newer.Values.ToList(), listed);
    }

    /// <summary>"VEN_8086&amp;DEV_4D76&amp;SUBSYS_*8086": the star stands for any hexadecimal digits.</summary>
    private static Regex Pattern(string value) =>
        new(Regex.Escape(value.ToUpperInvariant()).Replace(@"\*", "[0-9A-F]*"), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>"X.X.X.O": the parts marked O don't count when two versions are compared.</summary>
    private static string Masked(string version, string mask)
    {
        if (mask.Length == 0) return version;
        var parts = version.Split('.');
        var marks = mask.Split('.');
        for (var i = 0; i < parts.Length && i < marks.Length; i++)
            if (marks[i].Equals("O", StringComparison.OrdinalIgnoreCase)) parts[i] = "0";
        return string.Join('.', parts);
    }

    /// <summary>Opened in the browser later on, so it has to be Intel's own page.</summary>
    private static string Page(JsonElement package) =>
        Uri.TryCreate(Text(package, "Url"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "intel.com" || uri.Host.EndsWith(".intel.com", StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : MakerPages.IntelDrivers;

    private static DateTime? Date(JsonElement package) =>
        DateTime.TryParse(Text(package, "DisplayReleaseDate"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var date) && date.Year > 2000 ? date.Date : null;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Bool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
