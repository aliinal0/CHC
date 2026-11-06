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
    /// many small warnings never scores below a part with one serious problem.
    /// </summary>
    public int Score
    {
        get
        {
            var warnings = Findings.Count(f => f.Scored && f.Severity == Severity.Warning);
            var criticals = Findings.Count(f => f.Scored && f.Severity == Severity.Critical);
            var score = 100 - warnings * 15 - criticals * 40;
            if (criticals > 0) return Math.Clamp(score, 0, 40);
            if (warnings > 0) return Math.Clamp(score, 45, 85);
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

    /// <summary>
    /// 90+ (excellent) only without warnings; warnings alone end at 50 (fair) at worst; below 50 (poor,
    /// "serious problems") needs a critical problem. Summaries of other findings are not counted again.
    /// </summary>
    public int Score
    {
        get
        {
            var warnings = AllFindings.Count(f => f.Scored && f.Severity == Severity.Warning);
            var criticals = AllFindings.Count(f => f.Scored && f.Severity == Severity.Critical);
            var score = 100 - warnings * 8 - criticals * 30;
            if (criticals > 0) return Math.Clamp(score, 0, 49);
            if (warnings > 0) return Math.Clamp(score, 50, 89);
            return 100;
        }
    }

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
