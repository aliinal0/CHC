using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Timeline;

namespace CHC.Core.Presentation;

/// <summary>How a change reads on the timeline and in findings, in the current language.</summary>
public static class ChangeTexts
{
    public static string Title(SystemChange c) => c.Kind switch
    {
        ChangeKind.Driver => c.After is null ? Loc.T("change.driver.removed", c.Title) : Loc.T("change.driver", c.Title, c.After),
        ChangeKind.WindowsUpdate => c.After is null && c.Before is not null ? Loc.T("change.update.removed", c.Title) : c.Title,
        ChangeKind.AppInstalled => Loc.T("change.app.installed", c.Title),
        ChangeKind.AppUpdated => Loc.T("change.app.updated", c.Title),
        ChangeKind.AppRemoved => Loc.T("change.app.removed", c.Title),
        ChangeKind.Bios => Loc.T("change.bios"),
        ChangeKind.Windows => Loc.T("change.windows"),
        ChangeKind.HardwareAdded => Loc.T("change.hardware.added", c.Title),
        ChangeKind.HardwareRemoved => Loc.T("change.hardware.removed", c.Title),
        ChangeKind.HardwareChanged => Loc.T("change.hardware.changed." + c.Title),
        ChangeKind.Setting => Loc.Instance.Has("change.setting." + c.Setting)
            ? Loc.T("change.setting." + c.Setting, Value(c.After))
            : Loc.T("change.setting", c.Title),
        _ => c.Title,
    };

    /// <summary>"566.14 → 566.36", or empty when there is no before and after to show.</summary>
    public static string Detail(SystemChange c)
    {
        if (c.Kind == ChangeKind.Setting && c.Setting == "memoryspeed")
            return Loc.T("change.speed", c.Before, c.After);
        if (c.Kind is ChangeKind.Setting && c.Setting is "memoryintegrity" or "rebar") return "";
        if (c.Before is { Length: > 0 } before && c.After is { Length: > 0 } after && c.Kind != ChangeKind.WindowsUpdate)
            return Loc.T("change.fromto", Value(before), Value(after));
        if (c.Kind == ChangeKind.Driver && c.After is null && c.Before is not null) return c.Before;
        return c.Kind is ChangeKind.AppInstalled && c.After is { Length: > 0 } version ? version : "";
    }

    private static string Value(string? value) => value switch
    {
        "on" => Loc.T("common.on"),
        "off" => Loc.T("common.off"),
        _ => value ?? "",
    };
}

/// <summary>A change as a finding's argument: it reads in whatever language the app shows when the text is formatted.</summary>
public sealed record ChangeArgument(SystemChange Change)
{
    public override string ToString() => ChangeTexts.Title(Change);
}

/// <summary>Finds the change that problems most likely started with.</summary>
public static class ChangeSuspects
{
    /// <summary>
    /// A driver, update, BIOS or memory setting change after which at least two freezes or crashes came within ten
    /// days, when the two weeks before it were calm. The most incidents win; on a tie, the newest change.
    /// </summary>
    public static (SystemChange Change, int After)? Find(IReadOnlyList<SystemChange> changes, IReadOnlyList<Incident> incidents)
    {
        (SystemChange Change, int After)? best = null;
        foreach (var c in changes.Where(c => c.CanAffectStability))
        {
            var after = incidents.Count(i => i.Time >= c.Time && i.Time < c.Time.AddDays(10));
            var before = incidents.Count(i => i.Time < c.Time && i.Time >= c.Time.AddDays(-14));
            if (after < 2 || before > 0) continue;
            if (best is null || after > best.Value.After || after == best.Value.After && c.Time > best.Value.Change.Time)
                best = (c, after);
        }
        return best;
    }
}
