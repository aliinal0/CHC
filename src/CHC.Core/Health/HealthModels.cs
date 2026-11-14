using CHC.Core.Localization;

namespace CHC.Core.Health;

/// <summary>
/// Ordered from best to worst so that Max() gives the overall state. <see cref="Update"/> is something out of date or
/// waiting to be installed: more to do than a tip, but like a tip it doesn't change the score.
/// </summary>
public enum Severity { Good = 0, Info = 1, Update = 2, Warning = 3, Critical = 4 }

/// <summary>Health components plus <see cref="Performance"/> and <see cref="Network"/>, which are analysed separately and do not change the health score.</summary>
public enum ComponentKind { Cpu, Memory, Storage, Gpu, Battery, Motherboard, Stability, Performance, Network }

/// <summary>
/// One observation about the computer. Texts are resolved from the language files on demand
/// ("&lt;key&gt;.title", "&lt;key&gt;.detail", "&lt;key&gt;.fix"), so switching language needs no rescan.
/// </summary>
public sealed class Finding(ComponentKind component, Severity severity, string key, params object?[] args)
{
    private readonly bool _isUpdate;

    public ComponentKind Component { get; } = component;
    public Severity Severity { get; } = severity;

    /// <summary>Shown with the updates, apart from problems and tips. Something urgent keeps its severity and so still counts in the score.</summary>
    public bool IsUpdate
    {
        get => Severity == Severity.Update || _isUpdate;
        init => _isUpdate = value;
    }

    public string Key { get; } = key;
    public object?[] Args { get; } = args;

    /// <summary>Where to act on it: a maker's page or a page of Windows Settings.</summary>
    public string? ActionUrl { get; init; }

    public string ActionKey { get; init; } = "update.action.page";

    public string ActionText => Loc.T(ActionKey);

    /// <summary>False for a summary of problems that already have their own findings, so they are not counted twice in the score.</summary>
    public bool Scored { get; init; } = true;

    /// <summary>Points this takes off the overall score. Null: what its severity usually costs.</summary>
    public double? Weight { get; init; }

    /// <summary>What it costs: nothing when it doesn't count, else its own weight or its severity's usual one.</summary>
    public double Penalty => !Scored ? 0 : Weight ?? Severity switch
    {
        Severity.Warning => HealthReport.WarningPoints,
        Severity.Critical => HealthReport.CriticalPoints,
        _ => 0,
    };

    /// <summary>Makes the PC slow in everyday use. These add up to the speed verdict.</summary>
    public bool Slows { get; init; }

    /// <summary>
    /// Set when the user may say this wasn't the PC's doing: a power cut, a program they were testing
    /// ("silent", "appcrash:name"...). Null when that isn't an option.
    /// </summary>
    public string? ExcuseKey { get; init; }

    /// <summary>The user said so: it is still shown, but doesn't count.</summary>
    public bool Excused { get; init; }

    private string ExcuseKind => ExcuseKey?.Split(':')[0] ?? "";
    public string ExcuseLabel => ExcuseKey is null ? "" : Loc.T("excuse." + ExcuseKind + ".label");
    public string ExcuseHint => ExcuseKey is null ? "" : Loc.T("excuse." + ExcuseKind + ".hint");

    public string Title => Loc.T(Key + ".title", Args);
    public string Detail => Loc.Instance.Has(Key + ".detail") ? Loc.T(Key + ".detail", Args) : "";
    public string Recommendation => Loc.Instance.Has(Key + ".fix") ? Loc.T(Key + ".fix", Args) : "";
    public bool HasRecommendation => Loc.Instance.Has(Key + ".fix");

    /// <summary>What to do, one step per line ("&lt;key&gt;.steps"); empty when the recommendation says it all.</summary>
    public IReadOnlyList<string> Steps => Loc.Instance.Has(Key + ".steps")
        ? Loc.T(Key + ".steps", Args).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];

    /// <summary>Why it helps, and what to watch out for ("&lt;key&gt;.why"); shown when the user opens the details.</summary>
    public string Why => Loc.Instance.Has(Key + ".why") ? Loc.T(Key + ".why", Args) : "";
}

public sealed class ComponentHealth(ComponentKind kind, IReadOnlyList<Finding> findings, bool hasData)
{
    public ComponentKind Kind { get; } = kind;
    public IReadOnlyList<Finding> Findings { get; } = findings;
    public bool HasData { get; } = hasData;

    public Severity Severity => Findings.Count == 0 ? Severity.Good : Findings.Max(f => f.Severity);

    /// <summary>
    /// 100 without warnings. Warnings only: 85 down to 45. Any critical problem: 40 down to 0, so a part with
    /// many small warnings never scores below a part with one serious problem. A part shows its problems larger
    /// than the whole PC does: a usual warning's 8 points are 15 here, a critical problem's 30 are 40.
    /// </summary>
    public int Score
    {
        get
        {
            var scored = Findings.Where(f => f.Scored && f.Severity >= Severity.Warning).ToList();
            var criticals = scored.Any(f => f.Severity == Severity.Critical);
            var score = (int)Math.Round(100 - scored.Sum(f => f.Penalty * (f.Severity == Severity.Critical ? 40.0 / HealthReport.CriticalPoints : 15.0 / HealthReport.WarningPoints)));
            if (criticals) return Math.Clamp(score, 0, 40);
            if (scored.Count > 0) return Math.Clamp(score, 45, 85);
            return 100;
        }
    }
}

public sealed class HealthReport(IReadOnlyList<ComponentHealth> components)
{
    public IReadOnlyList<ComponentHealth> Components { get; } = components;

    public IEnumerable<Finding> AllFindings => Components.SelectMany(c => c.Findings);

    /// <summary>Everything worth mentioning, worst first. "Good" confirmations are left out.</summary>
    public IReadOnlyList<Finding> Noteworthy =>
        AllFindings.Where(f => f.Severity != Severity.Good).OrderByDescending(f => f.Severity).ThenBy(f => f.Component).ToList();

    /// <summary>Problems and tips, worst first. Updates have their own list.</summary>
    public IReadOnlyList<Finding> Issues => Noteworthy.Where(f => !f.IsUpdate).ToList();

    /// <summary>What is out of date or waiting to be installed, most urgent first.</summary>
    public IReadOnlyList<Finding> Updates => Noteworthy.Where(f => f.IsUpdate).ToList();

    // Each finding counts once, under the list it is shown in.
    public int WarningCount => Issues.Count(f => f.Severity == Severity.Warning);
    public int CriticalCount => Issues.Count(f => f.Severity == Severity.Critical);
    public int InfoCount => Issues.Count(f => f.Severity == Severity.Info);
    public int UpdateCount => Updates.Count;

    public Severity Severity => Components.Count == 0 ? Severity.Good : Components.Max(c => c.Severity);

    /// <summary>What a warning and a critical problem take off the score, unless the finding says otherwise.</summary>
    public const double WarningPoints = 8;
    public const double CriticalPoints = 30;

    /// <summary>
    /// Every finding takes its own weight off 100: a hard disk under Windows costs more than a program that hung.
    /// 90+ (excellent) only without warnings. Warnings alone end at 50 (fair) at worst, so many small ones never look
    /// worse than one serious problem. Below 50 (poor) takes a critical problem, or a PC that is very slow: held back
    /// by its disk, its memory and its processor at once it is in a poor state, even though nothing in it is broken.
    /// Summaries of other findings and what the user excused aren't counted.
    /// </summary>
    public int Score
    {
        get
        {
            var scored = AllFindings.Where(f => f.Scored && f.Severity >= Severity.Warning).ToList();
            var score = (int)Math.Round(100 - scored.Sum(f => f.Penalty));
            if (scored.Any(f => f.Severity == Severity.Critical)) return Math.Clamp(score, 0, 49);
            if (SpeedKey == "speed.veryslow") return Math.Clamp(score, 30, 49);
            if (scored.Count > 0) return Math.Clamp(score, 50, 89);
            return 100;
        }
    }

    /// <summary>What makes this PC slow in everyday use, the heaviest first.</summary>
    public IReadOnlyList<Finding> Slowing =>
        AllFindings.Where(f => f.Slows && f.Scored && f.Severity >= Severity.Warning).OrderByDescending(f => f.Penalty).ToList();

    /// <summary>How fast the PC is for everyday use, from what slows it down: "fine", "littleslow", "slow" or "veryslow".</summary>
    public string SpeedKey => "speed." + (Slowing.Sum(f => f.Penalty) switch
    {
        >= 40 => "veryslow",
        >= 20 => "slow",
        >= 6 => "littleslow",
        _ => "fine",
    });

    public Severity SpeedSeverity => SpeedKey switch
    {
        "speed.veryslow" => Severity.Critical,
        "speed.slow" => Severity.Warning,
        "speed.littleslow" => Severity.Info,
        _ => Severity.Good,
    };

    public string RatingKey => Score switch
    {
        >= 90 => "rating.excellent",
        >= 75 => "rating.good",
        >= 50 => "rating.fair",
        _ => "rating.poor",
    };

    /// <summary>The colour that goes with the rating: "Good" is green even with a warning among the findings.</summary>
    public Severity RatingSeverity => Score switch
    {
        >= 75 => Severity.Good,
        >= 50 => Severity.Warning,
        _ => Severity.Critical,
    };

    public ComponentHealth? For(ComponentKind kind) => Components.FirstOrDefault(c => c.Kind == kind);

    public static HealthReport Empty { get; } = new([]);
}
