using System.Collections.ObjectModel;

namespace Sprinkler.ViewModels;

/// <summary>Plain-language formatting shared by the screens.</summary>
internal static class Fmt
{
    public static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    public static string Bytes(long n) =>
        n > 1_000_000_000 ? $"{n / 1e9:0.0} GB"
        : n > 1_000_000 ? $"{n / 1e6:0} MB"
        : $"{Math.Max(1, (long)Math.Round(n / 1e3))} KB";

    public static string Time(DateTime when) => when.ToLocalTime().ToString("MMM d, yyyy h:mm tt");

    public static string Ago(DateTime when)
    {
        var minutes = (int)Math.Round((DateTime.UtcNow - when.ToUniversalTime()).TotalMinutes);
        return minutes < 1 ? "just now"
            : minutes < 60 ? $"{minutes} min ago"
            : minutes < 1440 ? $"{(int)Math.Round(minutes / 60.0)} h ago"
            : Time(when);
    }

    /// <summary>Makes <paramref name="target"/> equal to <paramref name="source"/> while touching it as little as possible, so lists on screen do not flicker.</summary>
    public static void Sync(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        if (target.SequenceEqual(source))
            return;
        target.Clear();
        foreach (var s in source)
            target.Add(s);
    }
}
