using System.Text.RegularExpressions;

namespace StardewModUpdater.Core;

/// <summary>A tolerant semantic version: 1 to 4 numeric parts plus an optional prerelease tag.</summary>
internal sealed partial class ModVersion : IComparable<ModVersion>, IEquatable<ModVersion>
{
    private readonly long[] _numbers;
    private readonly string[] _pre;

    public string Raw { get; }

    public bool IsPrerelease => _pre.Length > 0;

    private ModVersion(string raw, long[] numbers, string[] pre)
    {
        Raw = raw;
        _numbers = numbers;
        _pre = pre;
    }

    public static ModVersion? Parse(string? text) => TryParse(text, out var v) ? v : null;

    public static bool TryParse(string? text, out ModVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1]))
            s = s[1..];

        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];

        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..] : "";

        var parts = core.Split('.');
        if (parts.Length is < 1 or > 6)
            return false;

        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                return false;
        }

        var preParts = pre.Length == 0
            ? Array.Empty<string>()
            : pre.Split(new[] { '.', '-' }, StringSplitOptions.RemoveEmptyEntries);

        version = new ModVersion(text.Trim(), numbers, preParts);
        return true;
    }

    /// <summary>Every version-looking token in free text such as a file or release name.</summary>
    public static List<ModVersion> FindAll(string? text)
    {
        var found = new List<ModVersion>();
        if (string.IsNullOrEmpty(text))
            return found;

        foreach (Match m in TokenRegex().Matches(text))
        {
            if (TryParse(m.Value, out var v))
                found.Add(v);
        }
        return found;
    }

    public int CompareTo(ModVersion? other)
    {
        if (other is null)
            return 1;

        var len = Math.Max(_numbers.Length, other._numbers.Length);
        for (var i = 0; i < len; i++)
        {
            var a = i < _numbers.Length ? _numbers[i] : 0;
            var b = i < other._numbers.Length ? other._numbers[i] : 0;
            if (a != b)
                return a.CompareTo(b);
        }

        if (_pre.Length == 0 && other._pre.Length == 0) return 0;
        if (_pre.Length == 0) return 1;
        if (other._pre.Length == 0) return -1;

        var plen = Math.Min(_pre.Length, other._pre.Length);
        for (var i = 0; i < plen; i++)
        {
            var aNum = long.TryParse(_pre[i], out var an);
            var bNum = long.TryParse(other._pre[i], out var bn);
            int c;
            if (aNum && bNum) c = an.CompareTo(bn);
            else if (aNum) c = -1;
            else if (bNum) c = 1;
            else c = string.Compare(_pre[i], other._pre[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0)
                return c;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(ModVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ModVersion v && Equals(v);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        var last = _numbers.Length;
        while (last > 1 && _numbers[last - 1] == 0)
            last--;
        for (var i = 0; i < last; i++)
            hash.Add(_numbers[i]);
        foreach (var p in _pre)
            hash.Add(p, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }

    public override string ToString() => Raw;

    public static bool operator ==(ModVersion? a, ModVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(ModVersion? a, ModVersion? b) => !(a == b);
    public static bool operator >(ModVersion a, ModVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(ModVersion a, ModVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(ModVersion a, ModVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(ModVersion a, ModVersion b) => a.CompareTo(b) <= 0;

    [GeneratedRegex(@"(?<![\w.])[vV]?\d+(?:\.\d+){1,4}(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?")]
    private static partial Regex TokenRegex();
}
