using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CHC.Core.Models;
using Microsoft.Win32;

namespace CHC.Core.Updates;

/// <summary>A driver as its maker lists it.</summary>
public sealed record MakerDriver(string Version, DateTime? Released, string Url);

/// <summary>NVIDIA's public driver pages: all they get to see is the card's model and the Windows version.</summary>
public static partial class NvidiaDrivers
{
    private const string Lookup = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx";
    private const string Driver = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";
    private const int GeForce = 1;
    private const int MostSeriesToSearch = 8;

    public static bool IsNvidia(string card) => card.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows calls a driver 32.0.15.8157, NVIDIA calls it 581.57: the last five digits.</summary>
    public static string? PublicVersion(string windowsVersion)
    {
        var parts = windowsVersion.Trim().Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        digits = digits[^5..];
        return digits[..3] + "." + digits[3..];
    }

    public static async Task<MakerDriver?> LatestAsync(HttpClient http, string card, List<NvidiaCard> known)
    {
        var place = known.FirstOrDefault(k => k.Name == card) ?? await FindAsync(http, card);
        if (place is null) return null;
        if (!known.Contains(place)) known.Add(place);

        var os = await OperatingSystemAsync(http, place.Series);
        if (os is null) return null;
        var url = $"{Driver}?func=DriverManualLookup&psid={place.Series}&pfid={place.Product}&osID={os}&languageCode=1033&beta=null&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";
        return ParseLatest(await http.GetStringAsync(url));
    }

    public static MakerDriver? ParseLatest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0) return null;
        if (!ids[0].TryGetProperty("downloadInfo", out var info)) return null;
        if (!info.TryGetProperty("Version", out var version) || version.GetString() is not { Length: > 0 } number) return null;

        DateTime? released = null;
        if (info.TryGetProperty("ReleaseDateTime", out var date)
            && DateTime.TryParseExact(date.GetString(), "ddd MMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            released = parsed;

        // Opened in the browser later on, so it has to be NVIDIA's own.
        var page = info.TryGetProperty("DetailsURL", out var details) ? details.GetString() : null;
        var own = Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && (uri.Host == "nvidia.com" || uri.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase));
        return new MakerDriver(number, released, own ? page! : MakerPages.NvidiaDrivers);
    }

    public static string NormalizeName(string name) =>
        Spaces().Replace(TradeMarks().Replace(name, " "), " ").Trim().ToLowerInvariant();

    private static async Task<NvidiaCard?> FindAsync(HttpClient http, string card)
    {
        var name = NormalizeName(card);
        var model = Model().Match(name);
        if (!model.Success) return null;

        // The likely series first: an RTX 5070 is in the 50 series, a GTX 980 in the 900 series, an MX450 in the MX400 series.
        var digits = model.Groups[2].Value;
        var hint = digits.Length == 4 ? digits[..2] : digits[..1] + "00";
        var laptop = name.Contains("laptop") || name.Contains("max-q") || name.Contains("notebook");
        var series = (await ValuesAsync(http, 2, GeForce))
            .Where(s => s.Name.Contains("Notebook", StringComparison.OrdinalIgnoreCase) == laptop || model.Groups[1].Value == "mx")
            .OrderByDescending(s => s.Name.Contains(hint, StringComparison.Ordinal))
            .Take(MostSeriesToSearch);

        foreach (var s in series)
            foreach (var product in await ValuesAsync(http, 3, s.Value))
                if (NormalizeName(product.Name) == name)
                    return new NvidiaCard { Name = card, Series = s.Value, Product = product.Value };
        return null;
    }

    private static async Task<string?> OperatingSystemAsync(HttpClient http, string series)
    {
        var wanted = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10 64-bit";
        return (await ValuesAsync(http, 4, series)).FirstOrDefault(o => o.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static async Task<List<(string Name, string Value)>> ValuesAsync(HttpClient http, int type, object parent)
    {
        var text = await http.GetStringAsync($"{Lookup}?TypeID={type}&ParentID={parent}");
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader).Descendants("LookupValue")
            .Select(v => (Name: (string?)v.Element("Name") ?? "", Value: (string?)v.Element("Value") ?? ""))
            .Where(v => v.Name.Length > 0 && v.Value.All(char.IsAsciiDigit))
            .ToList();
    }

    [GeneratedRegex(@"nvidia|\(r\)|\(tm\)|®|™", RegexOptions.IgnoreCase)]
    private static partial Regex TradeMarks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(rtx|gtx|gts|gt|mx)\s*(\d{3,4})")]
    private static partial Regex Model();
}

/// <summary>AMD's public driver pages. They list the versions in the page itself; only a version number is taken from them.</summary>
public static partial class AmdDrivers
{
    private const string Root = "https://www.amd.com/en/support/downloads/drivers.html/";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] Am5 = ["a620", "b650", "b650e", "b840", "b850", "x670", "x670e", "x870", "x870e"];
    private static readonly string[] Am4 = ["a320", "b350", "a520", "b450", "b550", "x370", "x470", "x570"];

    public static bool IsAmd(string card) => card.Contains("AMD", StringComparison.OrdinalIgnoreCase) || card.Contains("Radeon", StringComparison.OrdinalIgnoreCase);

    /// <summary>Desktop Radeon RX 5000 and newer have a page of their own; for others there is only the general one.</summary>
    public static string? GraphicsPage(string card)
    {
        var m = RadeonModel().Match(card);
        if (!m.Success) return null;
        var suffix = m.Groups[3].Success ? "-" + m.Groups[3].Value.ToLowerInvariant() : "";
        return $"{Root}graphics/radeon-rx/radeon-rx-{m.Groups[1].Value}000-series/amd-radeon-rx-{m.Groups[1].Value}{m.Groups[2].Value}{suffix}.html";
    }

    /// <summary>From the motherboard's name: "MAG B650 TOMAHAWK" is on a B650, "ROG STRIX X670E-E" on an X670E.</summary>
    public static string? ChipsetPage(string board)
    {
        var m = BoardChipset().Match(board);
        if (!m.Success) return null;
        var chipset = (m.Groups[1].Value + m.Groups[2].Value).ToLowerInvariant();
        if (Am5.Contains(chipset)) return $"{Root}chipsets/am5/{chipset}.html";
        return Am4.Contains(chipset) ? $"{Root}chipsets/am4/{chipset}.html" : null;
    }

    /// <summary>What AMD recommends, not the optional driver that is sometimes newer.</summary>
    public static string? ParseRecommended(string html)
    {
        var revisions = Revision().Matches(html).Select(m => m.Groups[1].Value).ToList();
        var chosen = revisions.FirstOrDefault(r => r.Contains("Recommended", StringComparison.OrdinalIgnoreCase)) ?? revisions.FirstOrDefault();
        var number = chosen is null ? null : AdrenalinNumber().Match(chosen);
        return number is { Success: true } ? number.Value : null;
    }

    public static string? ParseChipset(string html)
    {
        var m = ChipsetRevision().Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The Adrenalin version, which is what AMD's page lists. Windows' own driver number says something else.</summary>
    public static string? InstalledSoftware()
    {
        try
        {
            using var classes = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (classes is null) return null;
            foreach (var name in classes.GetSubKeyNames())
            {
                using var key = classes.OpenSubKey(name);
                if (key?.GetValue("RadeonSoftwareVersion") is string { Length: > 0 } version) return version;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static string? InstalledChipset()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is string shown && shown.Equals("AMD Chipset Software", StringComparison.OrdinalIgnoreCase)
                        && key.GetValue("DisplayVersion") is string { Length: > 0 } version)
                        return version;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    [GeneratedRegex(@"Radeon(?:\s*\(TM\))?\s+RX\s+([5679])(\d{3})\b(?:\s+(XTX|XT|GRE)\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonModel();

    [GeneratedRegex(@"\b([ABX]\d{3})(E?)", RegexOptions.IgnoreCase)]
    private static partial Regex BoardChipset();

    [GeneratedRegex(@"Revision Number</strong>\s*<p>([^<]*)</p>")]
    private static partial Regex Revision();

    [GeneratedRegex(@"\b\d{2}\.\d{1,2}\.\d{1,2}\b")]
    private static partial Regex AdrenalinNumber();

    [GeneratedRegex(@"Revision Number</strong>\s*<p>(\d+(?:\.\d+){2,3})</p>")]
    private static partial Regex ChipsetRevision();
}

/// <summary>Makers' own pages, for what can't be looked up automatically. All of them are fixed addresses, none comes from the internet.</summary>
public static partial class MakerPages
{
    public const string NvidiaDrivers = "https://www.nvidia.com/en-us/drivers/";
    public const string AmdDrivers = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelDrivers = "https://www.intel.com/content/www/us/en/download-center/home.html";

    public const string WindowsUpdate = "ms-settings:windowsupdate";
    public const string OptionalUpdates = "ms-settings:windowsupdate-optionalupdates";

    public static string? Graphics(string card)
    {
        if (Updates.NvidiaDrivers.IsNvidia(card)) return NvidiaDrivers;
        if (card.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return IntelDrivers;
        return Updates.AmdDrivers.IsAmd(card) ? AmdDrivers : null;
    }

    /// <summary>The name to call the maker by, and its support page.</summary>
    public static (string Maker, string Url)? Board(BoardInfo board)
    {
        var maker = board.IsLaptop ? board.SystemManufacturer : board.Manufacturer;
        if (maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || maker.Contains("MSI", StringComparison.OrdinalIgnoreCase))
        {
            var model = Hyphens().Replace(Bracketed().Replace(board.Product, " ").Trim(), "-");
            return ("MSI", !board.IsLaptop && model.Length > 0 ? $"https://www.msi.com/Motherboard/{Uri.EscapeDataString(model)}/support" : "https://www.msi.com/support");
        }
        if (maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase)) return ("ASUS", "https://www.asus.com/support/");
        if (maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase)) return ("Gigabyte", "https://www.gigabyte.com/Support");
        if (maker.Contains("ASRock", StringComparison.OrdinalIgnoreCase)) return ("ASRock", "https://www.asrock.com/support/index.asp");
        if (maker.Contains("Dell", StringComparison.OrdinalIgnoreCase)) return ("Dell", "https://www.dell.com/support/home");
        if (maker.Contains("HP", StringComparison.Ordinal) || maker.Contains("Hewlett", StringComparison.OrdinalIgnoreCase)) return ("HP", "https://support.hp.com/drivers");
        if (maker.Contains("Lenovo", StringComparison.OrdinalIgnoreCase)) return ("Lenovo", "https://pcsupport.lenovo.com/");
        if (maker.Contains("Acer", StringComparison.OrdinalIgnoreCase)) return ("Acer", "https://www.acer.com/support/drivers-and-manuals");
        return null;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Hyphens();
}
