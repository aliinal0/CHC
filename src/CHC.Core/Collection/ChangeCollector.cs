using System.Globalization;
using System.Text.RegularExpressions;
using CHC.Core.Timeline;
using Microsoft.Win32;

namespace CHC.Core.Collection;

/// <summary>
/// Changes Windows itself keeps a record of: its update history, the drivers it installed (with the time) and the
/// install dates of programs. What Windows doesn't log (a BIOS update, a swapped part, XMP turned off) is found by
/// comparing scans; see <see cref="ChangeTracker"/>.
/// </summary>
public static partial class ChangeCollector
{
    public static List<SystemChange> Collect(TimeSpan period)
    {
        var since = DateTime.Now - period;
        var list = new List<SystemChange>();
        // Each source on its own: a broken update service must not hide the drivers.
        try { list.AddRange(WindowsUpdates(since)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { list.AddRange(Drivers(period)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { list.AddRange(Programs(since)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return list.OrderByDescending(c => c.Time).ToList();
    }

    // ---------- Windows Update history ----------

    /// <summary>Installed and removed updates, from the Windows Update Agent (the same list as Settings shows).</summary>
    private static List<SystemChange> WindowsUpdates(DateTime since)
    {
        var list = new List<SystemChange>();
        if (Type.GetTypeFromProgID("Microsoft.Update.Session") is not { } type) return list;
        dynamic session = Activator.CreateInstance(type)!;
        dynamic searcher = session.CreateUpdateSearcher();
        int count = searcher.GetTotalHistoryCount();
        if (count <= 0) return list;
        dynamic history = searcher.QueryHistory(0, Math.Min(count, 400));
        foreach (dynamic entry in history)
        {
            int operation = entry.Operation;   // 1 installation, 2 uninstallation
            int result = entry.ResultCode;     // 2 succeeded, 3 succeeded with errors
            if (operation is not (1 or 2) || result is not (2 or 3)) continue;
            DateTime date = entry.Date;
            var time = DateTime.SpecifyKind(date, DateTimeKind.Utc).ToLocalTime();
            if (time < since) continue;
            string title = entry.Title ?? "";
            // Defender's definitions and platform, the Windows Security app, the monthly malware scan and Store apps
            // update themselves all the time: noise on a timeline of things that change how the PC behaves.
            if (Noise.Any(kb => title.Contains(kb, StringComparison.OrdinalIgnoreCase)) || StoreProduct().IsMatch(title)) continue;
            string id = entry.UpdateIdentity.UpdateID;
            var change = new SystemChange
            {
                Time = time,
                Key = $"wu:{id}:{operation}",
                CanAffectStability = true,
            };
            if (DriverUpdate().Match(title) is { Success: true } driver)
            {
                // "NVIDIA - Display - 32.0.15.6636"
                change.Kind = ChangeKind.Driver;
                change.Title = driver.Groups[1].Value.Trim() + " " + driver.Groups[2].Value.Trim();
                change.After = driver.Groups[3].Value;
            }
            else
            {
                change.Kind = ChangeKind.WindowsUpdate;
                change.Title = title;
                change.CanAffectStability = title.Contains("Windows", StringComparison.OrdinalIgnoreCase);
            }
            if (operation == 2)
            {
                // Removed (rolled back) rather than installed.
                change.Before = change.After ?? change.Title;
                change.After = null;
            }
            list.Add(change);
        }
        return list;
    }

    private static readonly string[] Noise = ["KB2267602", "KB4052623", "KB5007651", "KB890830"];

    [GeneratedRegex(@"^[0-9A-Z]{12}-")]
    private static partial Regex StoreProduct();

    [GeneratedRegex(@"^(.+?) - (.+?) - (\d+(?:\.\d+){1,3})$")]
    private static partial Regex DriverUpdate();

    // ---------- driver installs ----------

    /// <summary>
    /// Drivers from outside Windows (graphics, chipset, network, audio...) as the Plug and Play log recorded them,
    /// with the version they replaced when the log still has it. One driver package touches several devices (a
    /// graphics driver also updates its HDMI audio); those count as one change, named after the main device.
    /// </summary>
    private static List<SystemChange> Drivers(TimeSpan period)
    {
        var events = new List<(DateTime Time, string Instance, string Version, string Provider, string ClassGuid)>();
        foreach (var r in StabilityCollector.Read("Microsoft-Windows-Kernel-PnP/Configuration", "EventID=400", period))
        {
            var d = StabilityCollector.Data(r);
            // Windows' own drivers come with Windows updates, which are on the timeline already.
            if (!d.TryGetValue("DriverInbox", out var inbox) || inbox.Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            var instance = d.GetValueOrDefault("DeviceInstanceId") ?? "";
            var version = d.GetValueOrDefault("DriverVersion") ?? "";
            if (instance.Length == 0 || version.Length == 0) continue;
            // Software and virtual devices (audio endpoints, VPN adapters) are not hardware drivers.
            if (instance.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) || instance.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) continue;
            events.Add((r.TimeCreated ?? DateTime.MinValue, instance, version, d.GetValueOrDefault("DriverProvider") ?? "", d.GetValueOrDefault("ClassGuid") ?? ""));
        }

        // The first time each device got each version; the version it had before, if the log reaches back that far.
        var installs = new List<(DateTime Time, string Instance, string Version, string? Before, string Provider, string ClassGuid)>();
        foreach (var device in events.GroupBy(e => e.Instance, StringComparer.OrdinalIgnoreCase))
        {
            string? before = null;
            foreach (var version in device.OrderBy(e => e.Time).GroupBy(e => e.Version))
            {
                var first = version.First();
                installs.Add((first.Time, first.Instance, first.Version, before, first.Provider, first.ClassGuid));
                before = first.Version;
            }
        }

        var list = new List<SystemChange>();
        var used = new HashSet<int>();
        var ordered = installs.OrderBy(i => i.Time).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!used.Add(i)) continue;
            var package = new List<int> { i };
            for (var j = i + 1; j < ordered.Count && ordered[j].Time - ordered[i].Time < TimeSpan.FromMinutes(30); j++)
                if (!used.Contains(j) && ordered[j].Provider == ordered[i].Provider && ordered[j].Version == ordered[i].Version)
                {
                    used.Add(j);
                    package.Add(j);
                }
            var main = package.Select(k => ordered[k]).OrderBy(k => ClassRank(k.ClassGuid)).First();
            list.Add(new SystemChange
            {
                Time = main.Time,
                Kind = ChangeKind.Driver,
                Title = Native.DeviceName(main.Instance) ?? main.Provider,
                Before = main.Before,
                After = main.Version,
                Key = $"drv:{main.Instance}:{main.Version}".ToLowerInvariant(),
                CanAffectStability = true,
            });
        }
        return list;
    }

    /// <summary>Which device names a driver package: the graphics card before its HDMI audio, and so on.</summary>
    private static int ClassRank(string classGuid) => classGuid.ToLowerInvariant() switch
    {
        "4d36e968-e325-11ce-bfc1-08002be10318" => 0, // display
        "4d36e97d-e325-11ce-bfc1-08002be10318" => 1, // system (chipset)
        "4d36e972-e325-11ce-bfc1-08002be10318" => 2, // network
        "4d36e97b-e325-11ce-bfc1-08002be10318" => 3, // storage controllers
        "4d36e96c-e325-11ce-bfc1-08002be10318" => 4, // sound
        _ => 9,
    };

    // ---------- programs ----------

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Installed programs and their versions, as "Apps &amp; features" lists them (no updates, no Windows parts).</summary>
    public static Dictionary<string, string> InstalledPrograms()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version, _) in Uninstall())
            result.TryAdd(name, version);
        return result;
    }

    /// <summary>Programs whose install date falls in the period; Windows keeps only the day, not the time.</summary>
    private static List<SystemChange> Programs(DateTime since)
    {
        var list = new List<SystemChange>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version, installed) in Uninstall())
        {
            if (installed is not { } date || date < since.Date || !seen.Add(name)) continue;
            list.Add(new SystemChange
            {
                Time = date,
                Kind = ChangeKind.AppInstalled,
                Title = name,
                After = version.Length > 0 ? version : null,
                Exact = false,
                Key = $"app:{name}:{version}".ToLowerInvariant(),
            });
        }
        return list;
    }

    private static IEnumerable<(string Name, string Version, DateTime? Installed)> Uninstall()
    {
        var views = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };
        foreach (var (hive, view) in views)
        {
            RegistryKey? root = null;
            try { root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(UninstallKey); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            if (root is null) continue;
            using (root)
            {
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(sub);
                    if (key?.GetValue("DisplayName") is not string { Length: > 0 } name) continue;
                    if (key.GetValue("SystemComponent") is int and 1 || key.GetValue("ParentKeyName") is not null) continue;
                    if (key.GetValue("ReleaseType") is string release && release.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = key.GetValue("DisplayVersion") as string ?? "";
                    DateTime? installed = key.GetValue("InstallDate") is string raw
                        && DateTime.TryParseExact(raw.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    yield return (name.Trim(), version.Trim(), installed);
                }
            }
        }
    }
}
