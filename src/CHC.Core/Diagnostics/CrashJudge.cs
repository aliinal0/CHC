using CHC.Core.Models;

namespace CHC.Core.Diagnostics;

/// <summary>Whose fault a program's crashes most likely are.</summary>
public enum CrashCause
{
    /// <summary>Can't be told from the records.</summary>
    Unknown,

    /// <summary>The program itself: it crashed in its own code, or gave up on an error of its own.</summary>
    Program,

    /// <summary>Its files couldn't be read from the disk.</summary>
    Disk,
}

/// <summary>
/// Looks at where a program crashed and with which error. A program that crashes in its own code has a bug: that says
/// nothing about the PC. A crash because the disk couldn't deliver the program's files does.
/// </summary>
public static class CrashJudge
{
    /// <summary>STATUS_IN_PAGE_ERROR: a page of the program couldn't be loaded from the disk.</summary>
    private const string InPageError = "c0000006";

    /// <summary>
    /// Errors a program raises itself: an unhandled .NET or C++ exception, a stack overflow, a failed internal check,
    /// its own memory bookkeeping damaged, an abort.
    /// </summary>
    private static readonly HashSet<string> OwnErrors = new(StringComparer.OrdinalIgnoreCase)
    {
        "e0434352", "e06d7363", "c00000fd", "c0000409", "c0000374", "40000015", "c000041d", "80000003",
    };

    /// <summary>The language runtimes programs bring along or run on: a crash there is the program's.</summary>
    private static readonly string[] Runtimes = ["coreclr", "clr.dll", "clrjit", "mscorwks", "mscorlib", "jvm.dll", "ucrtbase", "vcruntime", "msvcp", "msvcr", "python"];

    public static CrashCause Judge(IEnumerable<AppEvent> events)
    {
        var crashes = events.Where(e => !e.IsHang).ToList();
        if (crashes.Count == 0) return CrashCause.Unknown;
        if (crashes.Count(c => Code(c) == InPageError) * 2 >= crashes.Count) return CrashCause.Disk;
        return crashes.Count(IsOwn) * 3 >= crashes.Count * 2 ? CrashCause.Program : CrashCause.Unknown;
    }

    private static bool IsOwn(AppEvent crash) =>
        crash.ModuleIsOwn
        || crash.Module.Equals(crash.App, StringComparison.OrdinalIgnoreCase)
        || OwnErrors.Contains(Code(crash))
        || Runtimes.Any(r => crash.Module.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    private static string Code(AppEvent crash) => crash.ExceptionCode.Trim().TrimStart('0', 'x', 'X').PadLeft(8, '0').ToLowerInvariant();
}
