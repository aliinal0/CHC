using CHC.Core.Localization;

namespace CHC.Core.Health;

/// <summary>Ordered from best to worst so that Max() gives the overall state.</summary>
public enum Severity { Good = 0, Info = 1, Warning = 2, Critical = 3 }

public enum ComponentKind { Cpu, Memory, Storage, Gpu, Battery, Motherboard, Stability }

/// <summary>
/// One observation about the computer. Texts are resolved from the language files on demand
/// ("&lt;key&gt;.title", "&lt;key&gt;.detail", "&lt;key&gt;.fix"), so switching language needs no rescan.
/// </summary>
public sealed class Finding(ComponentKind component, Severity severity, string key, params object?[] args)
{
    public ComponentKind Component { get; } = component;
    public Severity Severity { get; } = severity;
    public string Key { get; } = key;
    public object?[] Args { get; } = args;

    public string Title => Loc.T(Key + ".title", Args);
    public string Detail => Loc.Instance.Has(Key + ".detail") ? Loc.T(Key + ".detail", Args) : "";
    public string Recommendation => Loc.Instance.Has(Key + ".fix") ? Loc.T(Key + ".fix", Args) : "";
    public bool HasRecommendation => Loc.Instance.Has(Key + ".fix");
}

public sealed class ComponentHealth(ComponentKind kind, IReadOnlyList<Finding> findings, bool hasData)
{
    public ComponentKind Kind { get; } = kind;
    public IReadOnlyList<Finding> Findings { get; } = findings;
    public bool HasData { get; } = hasData;

    public Severity Severity => Findings.Count == 0 ? Severity.Good : Findings.Max(f => f.Severity);

    public int Score
    {
        get
        {
            var warnings = Findings.Count(f => f.Severity == Severity.Warning);
            var criticals = Findings.Count(f => f.Severity == Severity.Critical);
            var score = 100 - warnings * 15 - criticals * 40;
            if (criticals > 0) score = Math.Min(score, 40);
            else if (warnings > 0) score = Math.Min(score, 85);
            return Math.Max(score, 0);
        }
    }
}

public sealed class HealthReport(IReadOnlyList<ComponentHealth> components)
{
    public IReadOnlyList<ComponentHealth> Components { get; } = components;

    public IEnumerable<Finding> AllFindings => Components.SelectMany(c => c.Findings);

    /// <summary>Problems and tips, worst first. "Good" confirmations are left out.</summary>
    public IReadOnlyList<Finding> Issues =>
        AllFindings.Where(f => f.Severity != Severity.Good).OrderByDescending(f => f.Severity).ThenBy(f => f.Component).ToList();

    public int WarningCount => AllFindings.Count(f => f.Severity == Severity.Warning);
    public int CriticalCount => AllFindings.Count(f => f.Severity == Severity.Critical);
    public int InfoCount => AllFindings.Count(f => f.Severity == Severity.Info);

    public Severity Severity => Components.Count == 0 ? Severity.Good : Components.Max(c => c.Severity);

    public int Score
    {
        get
        {
            var score = 100 - WarningCount * 8 - CriticalCount * 30;
            if (CriticalCount > 0) score = Math.Min(score, 49);
            else if (WarningCount > 0) score = Math.Min(score, 89);
            return Math.Clamp(score, 0, 100);
        }
    }

    public string RatingKey => Score switch
    {
        >= 90 => "rating.excellent",
        >= 75 => "rating.good",
        >= 50 => "rating.fair",
        _ => "rating.poor",
    };

    public ComponentHealth? For(ComponentKind kind) => Components.FirstOrDefault(c => c.Kind == kind);

    public static HealthReport Empty { get; } = new([]);
}
