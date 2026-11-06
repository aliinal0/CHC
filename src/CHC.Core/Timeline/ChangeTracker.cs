using System.Globalization;
using CHC.Core.Collection;
using CHC.Core.History;
using CHC.Core.Models;
using CHC.Core.Presentation;

namespace CHC.Core.Timeline;

/// <summary>
/// Keeps the change timeline in the history file. It adds what Windows logged (updates, drivers, program installs)
/// and compares the PC with how CHC last saw it, which finds what Windows doesn't log: a BIOS update, a new or
/// removed part, a new Windows version, XMP or Memory Integrity switched, a program updated or removed. The first
/// time there is nothing to compare with; that look becomes the starting point.
/// </summary>
public static class ChangeTracker
{
    /// <summary>Inventory keys a scan knows about (hardware, firmware, Windows, settings).</summary>
    private static readonly string[] ScanPrefixes = ["bios", "windows", "cpu", "gpu:", "ram", "disk:", "setting:"];
    private static readonly string[] ProgramPrefixes = ["app:"];

    /// <param name="snapshot">A full scan, or null between scans (then only programs are compared).</param>
    /// <param name="logged">What Windows logged (<see cref="ChangeCollector.Collect"/>).</param>
    public static void Track(HistoryStore store, SystemSnapshot? snapshot, IEnumerable<SystemChange> logged, DateTime? now = null)
    {
        var time = now ?? DateTime.Now;
        var found = logged.ToList();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, version) in ChangeCollector.InstalledPrograms()) current["app:" + name] = version;
        if (snapshot is not null) Describe(snapshot, current);

        var prefixes = snapshot is null ? ProgramPrefixes : [.. ScanPrefixes, .. ProgramPrefixes];
        var previous = store.Inventory();
        // Nothing to compare with for these keys yet: this look is the starting point, not a pile of "changes".
        bool Known(string prefix) => previous.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var compare = prefixes.Where(p => p == "app:" ? Known("app:") : Known("bios") || Known("cpu")).ToList();
        if (compare.Count > 0) found.AddRange(Compare(previous, current, compare, found, time));

        store.AddChanges(found);
        store.SaveInventory(current, prefixes);
    }

    /// <summary>The parts of a scan whose changes are worth a line on the timeline.</summary>
    private static void Describe(SystemSnapshot s, Dictionary<string, string> into)
    {
        if (s.Board.BiosVersion.Length > 0) into["bios"] = (s.Board.BiosVendor + " " + s.Board.BiosVersion).Trim();
        if (s.Os.Name.Length > 0) into["windows"] = $"{s.Os.Name} {s.Os.DisplayVersion} ({s.Os.Build})".Replace("  ", " ");
        if (s.Cpu.Name.Length > 0) into["cpu"] = s.Cpu.Name;
        foreach (var g in s.Gpus.Where(g => !g.IsVirtual && g.Name.Length > 0))
            into["gpu:" + g.Name] = g.DriverVersion;
        if (s.Memory.TotalBytes > 0) into["ram"] = Fmt.Gigabytes((ulong)Math.Round(s.Memory.TotalBytes / 1073741824.0));
        if (s.Memory.Modules.Count > 0 && s.Memory.Modules.Max(m => m.ConfiguredSpeedMTs) is > 0 and var speed)
            into["setting:memoryspeed"] = speed.ToString(CultureInfo.InvariantCulture);
        foreach (var d in s.Disks.Where(d => d.Model.Length > 0))
            into["disk:" + d.Model] = Fmt.DiskSize(d.SizeBytes);
        if (s.Gaming.MemoryIntegrity is { } hvci) into["setting:memoryintegrity"] = hvci ? "on" : "off";
        if (s.Gpus.FirstOrDefault(g => !g.IsIntegrated && !g.IsVirtual)?.ResizableBar is { } rebar) into["setting:rebar"] = rebar ? "on" : "off";
        if (s.Power.PlanName.Length > 0) into["setting:powerplan"] = s.Power.PlanName;
    }

    private static IEnumerable<SystemChange> Compare(Dictionary<string, string> before, Dictionary<string, string> after,
        IReadOnlyList<string> prefixes, IReadOnlyList<SystemChange> logged, DateTime time)
    {
        bool InScope(string key) => prefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        foreach (var (key, value) in after.Where(a => InScope(a.Key)))
        {
            var had = before.TryGetValue(key, out var old);
            if (had && string.Equals(old, value, StringComparison.Ordinal)) continue;
            if (Describe(key, had ? old : null, value, logged, time) is { } change) yield return change;
        }
        foreach (var (key, old) in before.Where(b => InScope(b.Key) && !after.ContainsKey(b.Key)))
            if (Describe(key, old, null, logged, time) is { } change) yield return change;
    }

    /// <summary>A timeline entry for one inventory difference, or null when Windows already logged the same change.</summary>
    private static SystemChange? Describe(string key, string? before, string? after, IReadOnlyList<SystemChange> logged, DateTime time)
    {
        var name = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
        var change = new SystemChange
        {
            Time = time,
            Exact = false,
            Before = before,
            After = after,
            Key = $"inv:{key}:{before}>{after}:{time:yyyyMMddHHmm}".ToLowerInvariant(),
        };
        switch (key)
        {
            case "bios":
                change.Kind = ChangeKind.Bios;
                change.Title = "BIOS";
                change.CanAffectStability = true;
                return change;
            case "windows":
                change.Kind = ChangeKind.Windows;
                change.Title = "Windows";
                change.CanAffectStability = true;
                return change;
            case "cpu":
            case "ram":
                change.Kind = ChangeKind.HardwareChanged;
                change.Title = key;
                change.CanAffectStability = true;
                return change;
        }
        if (key.StartsWith("gpu:", StringComparison.OrdinalIgnoreCase))
        {
            change.Title = name;
            change.CanAffectStability = true;
            if (before is not null && after is not null)
            {
                // A new graphics driver; skip it if the driver log or Windows Update already has this version.
                if (logged.Any(l => l.Kind == ChangeKind.Driver && string.Equals(l.After, after, StringComparison.OrdinalIgnoreCase))) return null;
                change.Kind = ChangeKind.Driver;
                return change;
            }
            change.Kind = after is null ? ChangeKind.HardwareRemoved : ChangeKind.HardwareAdded;
            return change;
        }
        if (key.StartsWith("disk:", StringComparison.OrdinalIgnoreCase))
        {
            if (before is not null && after is not null) return null; // same model, other size reading: not a change
            change.Kind = after is null ? ChangeKind.HardwareRemoved : ChangeKind.HardwareAdded;
            change.Title = name;
            return change;
        }
        if (key.StartsWith("setting:", StringComparison.OrdinalIgnoreCase))
        {
            if (before is null || after is null) return null; // a setting that appears or disappears says little
            change.Kind = ChangeKind.Setting;
            change.Setting = name;
            change.Title = name;
            // Memory running at another speed: almost always XMP/EXPO switched (or reset by a BIOS update).
            change.CanAffectStability = name is "memoryspeed" or "memoryintegrity";
            return change;
        }
        if (key.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            change.Title = name;
            change.Kind = before is null ? ChangeKind.AppInstalled : after is null ? ChangeKind.AppRemoved : ChangeKind.AppUpdated;
            // Installs Windows logged with their date are on the timeline already.
            if (change.Kind == ChangeKind.AppInstalled && logged.Any(l => l.Kind == ChangeKind.AppInstalled && l.Title.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;
            return change;
        }
        return null;
    }
}
