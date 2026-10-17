using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Sprinkler.Core;

/// <summary>A language the app can speak. <see cref="Code"/> names its file under Locales.</summary>
internal sealed record Language(string Code, string NativeName, PluralRule Plural);

/// <summary>How a language counts. Only the groups the shipped languages need are listed (CLDR plural categories).</summary>
internal enum PluralRule
{
    /// <summary>"one" for 1, "other" for everything else: English, Spanish, German, Italian, Dutch, Portuguese, Turkish, Hungarian, Bulgarian.</summary>
    OneOther,

    /// <summary>"one" for 0 and 1: French and Brazilian Portuguese.</summary>
    ZeroOneOther,

    /// <summary>"one", "few", "many": Russian and Ukrainian.</summary>
    EastSlavic,

    /// <summary>"one", "few", "many": Polish.</summary>
    Polish,

    /// <summary>Only "other": Chinese, Japanese, Korean, Thai.</summary>
    OtherOnly,
}

/// <summary>
/// The words of the app. English is the source and the fallback: a key that a language does not have yet is shown in English
/// instead of as a hole. Each language is a flat JSON file of key to text under Locales, with {0}, {1} for values and
/// _one, _few, _many, _other endings for counts.
/// </summary>
internal sealed class Loc : INotifyPropertyChanged
{
    public const string English = "en";

    public static readonly IReadOnlyList<Language> All = new[]
    {
        new Language("en", "English", PluralRule.OneOther),
        new Language("es", "Español", PluralRule.OneOther),
        new Language("fr", "Français", PluralRule.ZeroOneOther),
        new Language("de", "Deutsch", PluralRule.OneOther),
        new Language("pt-BR", "Português (Brasil)", PluralRule.ZeroOneOther),
        new Language("it", "Italiano", PluralRule.OneOther),
        new Language("nl", "Nederlands", PluralRule.OneOther),
        new Language("ru", "Русский", PluralRule.EastSlavic),
        new Language("uk", "Українська", PluralRule.EastSlavic),
        new Language("pl", "Polski", PluralRule.Polish),
        new Language("tr", "Türkçe", PluralRule.OneOther),
        new Language("hu", "Magyar", PluralRule.OneOther),
        new Language("bg", "Български", PluralRule.OneOther),
        new Language("zh-Hans", "简体中文", PluralRule.OtherOnly),
        new Language("ja", "日本語", PluralRule.OtherOnly),
        new Language("ko", "한국어", PluralRule.OtherOnly),
        new Language("th", "ไทย", PluralRule.OtherOnly),
    };

    public static Loc Instance { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private Language _current = All[0];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changes, so screens can redraw the text they built in code.</summary>
    public event Action? LanguageChanged;

    public Language Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>Chooses a language by code, or "auto" for the one the system uses. Unknown codes mean English.</summary>
    public void SetLanguage(string? codeOrAuto)
    {
        var language = Resolve(codeOrAuto);
        lock (_gate)
        {
            if (_current.Code == language.Code)
                return;
            _current = language;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        LanguageChanged?.Invoke();
    }

    public static Language Resolve(string? codeOrAuto)
    {
        if (string.IsNullOrWhiteSpace(codeOrAuto) || codeOrAuto.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return Match(OsLanguage.Detect()) ?? All[0];
        return All.FirstOrDefault(l => l.Code.Equals(codeOrAuto.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }

    /// <summary>
    /// Finds the shipped language for a system locale name such as "pt_PT.UTF-8", "zh-CN", "de-AT" or "en_US". Returns null when none fits,
    /// so the caller can fall back to English. Traditional Chinese is not matched to Simplified.
    /// </summary>
    public static Language? Match(IEnumerable<string> localeNames)
    {
        foreach (var raw in localeNames)
        {
            var name = raw.Split('.', '@')[0].Replace('_', '-').Trim();
            if (name.Length == 0 || name.Equals("C", StringComparison.OrdinalIgnoreCase) || name.Equals("POSIX", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = name.Split('-');
            var lang = parts[0].ToLowerInvariant();

            if (lang == "zh")
            {
                var traditional = parts.Skip(1).Any(p => p.Equals("TW", StringComparison.OrdinalIgnoreCase) || p.Equals("HK", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("MO", StringComparison.OrdinalIgnoreCase) || p.Equals("Hant", StringComparison.OrdinalIgnoreCase));
                if (!traditional)
                    return All.First(l => l.Code == "zh-Hans");
                continue;
            }
            if (lang == "pt")
                return All.First(l => l.Code == "pt-BR");
            if (lang == "nb" || lang == "nn")
                continue;
            var found = All.FirstOrDefault(l => l.Code.Equals(lang, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                return found;
        }
        return null;
    }

    // ---------------------------------------------------------------- looking words up

    /// <summary>Indexer for screens: the text for a key in the current language, or English, or the key itself.</summary>
    public string this[string key] => Lookup(key) ?? key;

    public static string T(string key) => Instance[key];

    public static string T(string key, params object[] args)
    {
        var format = Instance[key];
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            return format; // a translation with a broken placeholder must never crash the screen
        }
    }

    /// <summary>A counted phrase such as "3 mods": picks the form the current language uses for that number. Keys end in _one, _few, _many or _other.</summary>
    public static string Count(string key, long n) => Instance.Plural(key, n);

    public string Plural(string key, long n)
    {
        var language = Current;
        var category = Category(language.Plural, n);
        var text = Lookup(key + "_" + category) ?? Lookup(key + "_other") ?? key;
        try
        {
            return string.Format(CultureInfo.InvariantCulture, text, n);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    public static string Category(PluralRule rule, long n)
    {
        var abs = Math.Abs(n);
        switch (rule)
        {
            case PluralRule.OneOther:
                return abs == 1 ? "one" : "other";
            case PluralRule.ZeroOneOther:
                return abs is 0 or 1 ? "one" : "other";
            case PluralRule.EastSlavic:
                if (abs % 10 == 1 && abs % 100 != 11) return "one";
                if (abs % 10 is >= 2 and <= 4 && abs % 100 is not (>= 12 and <= 14)) return "few";
                return "many";
            case PluralRule.Polish:
                if (abs == 1) return "one";
                if (abs % 10 is >= 2 and <= 4 && abs % 100 is not (>= 12 and <= 14)) return "few";
                return "many";
            default:
                return "other";
        }
    }

    private string? Lookup(string key)
    {
        var language = Current;
        if (Load(language.Code).TryGetValue(key, out var text) && text.Length > 0)
            return text;
        if (language.Code != English && Load(English).TryGetValue(key, out var fallback))
            return fallback;
        return null;
    }

    /// <summary>The strings of one language, read once from the file inside the program.</summary>
    internal Dictionary<string, string> Load(string code)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(code, out var cached))
                return cached;
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var assembly = typeof(Loc).Assembly;
                using var stream = assembly.GetManifestResourceStream($"Locales.{code}.json");
                if (stream is not null)
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new())
                        strings[k] = v;
            }
            catch (JsonException ex)
            {
                Log.Write($"Language file {code} is not valid: {ex.Message}");
            }
            _cache[code] = strings;
            return strings;
        }
    }

    /// <summary>For tests and tools: every key a language defines.</summary>
    internal IReadOnlyDictionary<string, string> Strings(string code) => Load(code);

    /// <summary>For tests: read the files again.</summary>
    internal void ClearCache()
    {
        lock (_gate)
            _cache.Clear();
    }
}

/// <summary>Finds the language the system is set to, on every system, without needing the globalization data the app leaves out to stay small.</summary>
internal static class OsLanguage
{
    public static IEnumerable<string> Detect()
    {
        var names = new List<string>();
        foreach (var variable in new[] { "LC_ALL", "LC_MESSAGES", "LANGUAGE", "LANG" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                names.AddRange(value.Split(':', StringSplitOptions.RemoveEmptyEntries));
        }
        if (OperatingSystem.IsWindows())
        {
            var windows = WindowsLocale();
            if (windows is not null)
                names.Insert(0, windows);
        }
        try
        {
            var culture = CultureInfo.CurrentUICulture.Name;
            if (culture.Length > 0)
                names.Add(culture);
        }
        catch (CultureNotFoundException)
        {
        }
        return names;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? WindowsLocale()
    {
        try
        {
            var buffer = new System.Text.StringBuilder(85);
            return NativeMethods.GetUserDefaultLocaleName(buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int GetUserDefaultLocaleName(System.Text.StringBuilder name, int capacity);
    }
}
