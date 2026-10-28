using System.Management;

namespace CHC.Core.Collection;

/// <summary>Small, exception-safe helpers around System.Management.</summary>
internal static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string Wmi_ = @"root\wmi";
    public const string Tpm = @"root\cimv2\Security\MicrosoftTpm";

    public static List<ManagementBaseObject> Query(string wql, string scope = Cimv2)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(15);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    public static ManagementBaseObject? First(string wql, string scope = Cimv2) => Query(wql, scope).FirstOrDefault();

    private static object? Raw(ManagementBaseObject o, string property)
    {
        try { return o[property]; }
        catch (ManagementException) { return null; }
    }

    public static string Str(this ManagementBaseObject o, string property) =>
        Raw(o, property)?.ToString()?.Trim() ?? "";

    public static ulong U64(this ManagementBaseObject o, string property) =>
        Raw(o, property) is { } v && ulong.TryParse(v.ToString(), out var r) ? r : 0;

    public static int I32(this ManagementBaseObject o, string property) =>
        Raw(o, property) is { } v && long.TryParse(v.ToString(), out var r) ? (int)Math.Clamp(r, int.MinValue, int.MaxValue) : 0;

    /// <summary>CIM char16 values arrive as <see cref="char"/> (or as a number on some providers).</summary>
    public static char? Char(this ManagementBaseObject o, string property) => Raw(o, property) switch
    {
        char c when c != '\0' => c,
        ushort u when u != 0 => (char)u,
        int i when i != 0 => (char)i,
        string { Length: > 0 } s => s[0],
        _ => null,
    };

    public static bool? Bool(this ManagementBaseObject o, string property) =>
        Raw(o, property) is bool b ? b : null;

    public static DateTime? Date(this ManagementBaseObject o, string property)
    {
        var s = o.Str(property);
        if (s.Length == 0) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return null; }
    }

    public static IReadOnlyList<int> IntArray(this ManagementBaseObject o, string property) =>
        Raw(o, property) is Array a ? a.Cast<object>().Select(x => Convert.ToInt32(x)).ToList() : [];
}
