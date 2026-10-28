using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CHC.Core.Localization;

/// <summary>A language that CHC can display.</summary>
public sealed record LanguageInfo(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

/// <summary>A format argument that is itself translated when the text is shown.</summary>
public sealed record LocKey(string Key);

/// <summary>
/// Simple JSON based string table. Built-in languages are embedded resources
/// (Localization/Languages/*.json); extra languages can be dropped into a
/// "Languages" folder next to the executable without recompiling.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string DefaultLanguage = "en";
    private const string ResourcePrefix = "CHC.Languages.";
    private const string NameKey = "_language";

    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _current = new();
    private Dictionary<string, string> _fallback = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = DefaultLanguage;
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(DefaultLanguage);
    public IReadOnlyList<LanguageInfo> Languages { get; private set; } = [];

    private Loc()
    {
        LoadEmbedded();
        LoadExternal(Path.Combine(AppContext.BaseDirectory, "Languages"));
        Languages = _tables
            .Select(t => new LanguageInfo(t.Key, t.Value.GetValueOrDefault(NameKey, t.Key)))
            .OrderBy(l => l.NativeName, StringComparer.CurrentCulture)
            .ToList();
        _fallback = _tables.GetValueOrDefault(DefaultLanguage) ?? new();
        _current = _fallback;
    }

    public string this[string key] => Get(key);

    public static string T(string key) => Instance.Get(key);

    public static string T(string key, params object?[] args)
    {
        var format = Instance.Get(key);
        var resolved = args.Select(a => a is LocKey k ? Instance.Get(k.Key) : a).ToArray();
        try { return string.Format(Instance.Culture, format, resolved); }
        catch (FormatException) { return format; }
    }

    public bool Has(string key) => _current.ContainsKey(key) || _fallback.ContainsKey(key);

    public string Get(string key) =>
        _current.TryGetValue(key, out var value) ? value :
        _fallback.TryGetValue(key, out value) ? value : key;

    /// <summary>Picks the best match for a culture name ("tr-TR" -> "tr"), falling back to English.</summary>
    public string Resolve(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return DefaultLanguage;
        if (_tables.ContainsKey(cultureName)) return cultureName;
        var neutral = cultureName.Split('-')[0];
        if (_tables.ContainsKey(neutral)) return neutral;
        // "pt-PT" → "pt-BR", "zh-TW" → "zh-CN": a regional sibling beats English.
        return _tables.Keys.FirstOrDefault(k => k.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase)) ?? DefaultLanguage;
    }

    public void SetLanguage(string? code)
    {
        code = Resolve(code);
        CurrentLanguage = code;
        _current = _tables[code];
        try { Culture = CultureInfo.GetCultureInfo(code); }
        catch (CultureNotFoundException) { Culture = CultureInfo.InvariantCulture; }

        // "Item[]" refreshes every indexer binding in XAML.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            var code = Path.GetFileNameWithoutExtension(name[ResourcePrefix.Length..]);
            Merge(code, stream);
        }
    }

    private void LoadExternal(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                Merge(Path.GetFileNameWithoutExtension(file), stream);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A broken community translation must never stop the app from starting.
            }
        }
    }

    private void Merge(string code, Stream stream)
    {
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        if (_tables.TryGetValue(code, out var existing))
            foreach (var (k, v) in table) existing[k] = v;
        else
            _tables[code] = new Dictionary<string, string>(table, StringComparer.Ordinal);
    }
}
