using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using CHC.Core.Models;

namespace CHC.Core.Updates;

/// <summary>What Windows Update itself offers. It is Windows that asks Microsoft; nothing gets installed.</summary>
public static class WindowsUpdates
{
    /// <summary>Microsoft Defender's definitions come several times a day and install by themselves.</summary>
    private const string DefinitionUpdates = "e0789628-ce08-4437-be74-2495b842f43b";

    public static List<AvailableUpdate> Search()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update isn't available");
        dynamic session = Activator.CreateInstance(type)!;
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");

        var list = new List<AvailableUpdate>();
        foreach (dynamic update in result.Updates)
        {
            var definitions = false;
            foreach (dynamic category in update.Categories)
                definitions |= string.Equals((string)category.CategoryID, DefinitionUpdates, StringComparison.OrdinalIgnoreCase);
            if (definitions) continue;

            var driver = (int)update.Type == 2;
            // A graphics driver from here is usually older than the maker's, which is looked up on its own.
            if (driver && string.Equals((string?)update.DriverClass, "Display", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new AvailableUpdate
            {
                Kind = driver ? UpdateKind.WindowsDriver : UpdateKind.Windows,
                Name = (string)update.Title,
                Url = driver ? MakerPages.OptionalUpdates : MakerPages.WindowsUpdate,
            });
        }
        return list;
    }
}

/// <summary>Programs with a newer version, from the list of Windows' own package manager. Nothing gets installed.</summary>
public static partial class WingetUpdates
{
    private const int TimeoutMs = 120_000;

    /// <summary>Null if winget isn't on this PC; that is no failure, only less to say.</summary>
    public static async Task<List<AvailableUpdate>?> SearchAsync()
    {
        var start = new ProcessStartInfo("winget", "upgrade --source winget --disable-interactivity")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception) { return null; }
        if (process is null) return null;

        using (process)
        using (var cancel = new CancellationTokenSource(TimeoutMs))
        {
            var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var errors = process.StandardError.ReadToEndAsync(cancel.Token);
            try
            {
                await process.WaitForExitAsync(cancel.Token);
                await errors;
                return Parse(await output);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                throw new TimeoutException("winget took too long");
            }
        }
    }

    /// <summary>
    /// The table after its line of dashes: name, id, version, available and sometimes source. Its headers are translated and
    /// its widths vary, so a row is read from the right: only the name can have spaces in it.
    /// </summary>
    public static List<AvailableUpdate> Parse(string output)
    {
        var list = new List<AvailableUpdate>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var dashes = Array.FindIndex(lines, l => Dashes().IsMatch(l));
        if (dashes < 1) return list;
        var hasSource = lines[dashes - 1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 5;

        foreach (var line in lines.Skip(dashes + 1))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (hasSource && tokens.Count > 0) tokens.RemoveAt(tokens.Count - 1);
            if (tokens.Count < 4) break;

            var available = tokens[^1];
            var installed = tokens[^2];
            var idAt = tokens.Count - 3;
            // "< 1.2.3": winget only knows the version is older than this.
            if (tokens[idAt] is "<" or ">") idAt--;
            if (idAt < 1 || !PackageId().IsMatch(tokens[idAt])) break;

            list.Add(new AvailableUpdate
            {
                Kind = Runtime().IsMatch(tokens[idAt]) ? UpdateKind.Runtime : UpdateKind.Program,
                Name = string.Join(' ', tokens.Take(idAt)),
                Installed = installed,
                Latest = available,
            });
        }
        return list;
    }

    [GeneratedRegex(@"^\s*-{10,}\s*$")]
    private static partial Regex Dashes();

    [GeneratedRegex(@"^[\p{L}\d][\p{L}\d\-_+]*(\.[\p{L}\d\-_+…]+)+$")]
    private static partial Regex PackageId();

    /// <summary>Software other programs need in order to run.</summary>
    [GeneratedRegex(@"^(Microsoft\.(DotNet|VCRedist|VCLibs|EdgeWebView2Runtime|DirectX|XNARedist|OpenJDK|PowerShell|WSL)\b|Oracle\.(JavaRuntimeEnvironment|JDK)|EclipseAdoptium\.|Azul\.|Amazon\.Corretto|Python\.Python\.|OpenJS\.NodeJS)")]
    private static partial Regex Runtime();
}

/// <summary>Looks for newer drivers, Windows updates and programs. Each source can fail on its own; what it said last time then stays.</summary>
public static class UpdateChecker
{
    private static readonly TimeSpan WindowsTimeout = TimeSpan.FromMinutes(3);

    public static async Task<UpdateInfo> CheckAsync(SystemSnapshot s, UpdateInfo? before)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CHC/1.0 (Computer Health Check)");

        var info = new UpdateInfo { NvidiaCards = before?.NvidiaCards ?? [] };
        var drivers = TryAsync(() => DriversAsync(http, s, info));
        var windows = TryAsync(async () => (List<AvailableUpdate>?)await Task.Run(WindowsUpdates.Search).WaitAsync(WindowsTimeout));
        var programs = TryAsync(async () => await WingetUpdates.SearchAsync() ?? []);
        await Task.WhenAll(drivers, windows, programs);

        // No source at all: most likely no internet. Try again next time, with the old list until then.
        if (drivers.Result is null && windows.Result is null && programs.Result is null)
        {
            var kept = before ?? new UpdateInfo();
            kept.Incomplete = true;
            return kept;
        }

        info.CheckedAt = DateTime.Now;
        info.Incomplete = drivers.Result is null || windows.Result is null || programs.Result is null;
        Add(info, drivers.Result, before, UpdateKind.GraphicsDriver, UpdateKind.ChipsetDriver);
        Add(info, windows.Result, before, UpdateKind.WindowsDriver, UpdateKind.Windows);
        Add(info, programs.Result, before, UpdateKind.Runtime, UpdateKind.Program);
        if (drivers.Result is null) info.KnownGraphics = before?.KnownGraphics ?? [];
        info.Items = info.Items.OrderBy(i => i.Kind).ToList();
        KeepCurrent(info, s);
        return info;
    }

    /// <summary>Something installed since the last look is no longer an update. Only what can be told without going online.</summary>
    public static void KeepCurrent(UpdateInfo info, SystemSnapshot s)
    {
        info.Items.RemoveAll(item => item.Kind switch
        {
            UpdateKind.GraphicsDriver => InstalledGraphics(item.Name, s) is { } now && !UpdateVersions.IsNewer(item.Latest, now),
            UpdateKind.ChipsetDriver => AmdDrivers.InstalledChipset() is { } now && !UpdateVersions.IsNewer(item.Latest, now),
            _ => false,
        });
        foreach (var item in info.Items.Where(i => i.Kind == UpdateKind.GraphicsDriver))
            if (InstalledGraphics(item.Name, s) is { } now) item.Installed = now;
    }

    private static void Add(UpdateInfo info, List<AvailableUpdate>? found, UpdateInfo? before, params UpdateKind[] kinds)
    {
        if (found is not null) info.Items.AddRange(found);
        else if (before is not null) info.Items.AddRange(before.Items.Where(i => kinds.Contains(i.Kind)));
    }

    private static async Task<List<AvailableUpdate>?> TryAsync(Func<Task<List<AvailableUpdate>?>> source)
    {
        try { return await source(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException or XmlException or COMException or InvalidOperationException or IOException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return null;
        }
    }

    private static async Task<List<AvailableUpdate>?> DriversAsync(HttpClient http, SystemSnapshot s, UpdateInfo info)
    {
        var list = new List<AvailableUpdate>();
        var anyAnswer = false;
        var anyFailure = false;

        foreach (var gpu in s.Gpus.Where(g => !g.IsVirtual && !g.UsesBasicDriver))
        {
            try
            {
                if (await GraphicsAsync(http, gpu, info) is not { } answer) continue;
                anyAnswer = true;
                if (!info.KnownGraphics.Contains(gpu.Name)) info.KnownGraphics.Add(gpu.Name);
                if (answer.Update is not null) list.Add(answer.Update);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or XmlException)
            {
                anyFailure = true;
            }
        }

        try
        {
            if (await ChipsetAsync(http, s) is { } chipset)
            {
                anyAnswer = true;
                if (chipset.Update is not null) list.Add(chipset.Update);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            anyFailure = true;
        }

        // Nothing here could be looked up at all, like an Intel-only PC: that is no failure.
        return anyFailure && !anyAnswer ? null : list;
    }

    private sealed record Answer(AvailableUpdate? Update);

    private static async Task<Answer?> GraphicsAsync(HttpClient http, GpuInfo gpu, UpdateInfo info)
    {
        if (NvidiaDrivers.IsNvidia(gpu.Name))
        {
            if (await NvidiaDrivers.LatestAsync(http, gpu.Name, info.NvidiaCards) is not { } driver) return null;
            var now = NvidiaDrivers.PublicVersion(gpu.DriverVersion);
            return new Answer(now is not null && UpdateVersions.IsNewer(driver.Version, now)
                ? new AvailableUpdate { Kind = UpdateKind.GraphicsDriver, Name = gpu.Name, Installed = now, Latest = driver.Version, Released = driver.Released, Url = driver.Url }
                : null);
        }

        if (AmdDrivers.GraphicsPage(gpu.Name) is { } page && AmdDrivers.InstalledSoftware() is { } installed)
        {
            if (AmdDrivers.ParseRecommended(await http.GetStringAsync(page)) is not { } latest) return null;
            return new Answer(UpdateVersions.IsNewer(latest, installed)
                ? new AvailableUpdate { Kind = UpdateKind.GraphicsDriver, Name = gpu.Name, Installed = installed, Latest = latest, Url = page }
                : null);
        }
        return null;
    }

    private static async Task<Answer?> ChipsetAsync(HttpClient http, SystemSnapshot s)
    {
        if (s.Board.IsLaptop || !s.Cpu.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || AmdDrivers.ChipsetPage(s.Board.Product) is not { } page) return null;
        if (AmdDrivers.ParseChipset(await http.GetStringAsync(page)) is not { } latest) return null;
        var now = AmdDrivers.InstalledChipset();
        return new Answer(now is null || UpdateVersions.IsNewer(latest, now)
            ? new AvailableUpdate { Kind = UpdateKind.ChipsetDriver, Name = "AMD Chipset Software", Installed = now ?? "", Latest = latest, Url = page }
            : null);
    }

    private static string? InstalledGraphics(string card, SystemSnapshot s)
    {
        var gpu = s.Gpus.FirstOrDefault(g => g.Name == card);
        if (gpu is null) return null;
        return NvidiaDrivers.IsNvidia(card) ? NvidiaDrivers.PublicVersion(gpu.DriverVersion) : AmdDrivers.InstalledSoftware();
    }
}
