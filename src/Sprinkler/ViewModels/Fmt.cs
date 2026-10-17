using System.Collections.ObjectModel;
using System.Globalization;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

/// <summary>Formatting shared by the screens. Every word comes from the language files.</summary>
internal static class Fmt
{
    public static string Bytes(long n) =>
        n > 1_000_000_000 ? $"{n / 1e9:0.0} GB"
        : n > 1_000_000 ? $"{n / 1e6:0} MB"
        : $"{Math.Max(1, (long)Math.Round(n / 1e3))} KB";

    /// <summary>A date and time in the pattern of the current language. Month names are avoided on purpose: the app ships without culture data to stay small.</summary>
    public static string Time(DateTime when) => when.ToLocalTime().ToString(Loc.T("fmt.datetime"), CultureInfo.InvariantCulture);

    public static string Ago(DateTime when)
    {
        var minutes = (int)Math.Round((DateTime.UtcNow - when.ToUniversalTime()).TotalMinutes);
        return minutes < 1 ? Loc.T("time.just_now")
            : minutes < 60 ? Loc.T("time.minutes_ago", minutes)
            : minutes < 1440 ? Loc.T("time.hours_ago", (int)Math.Round(minutes / 60.0))
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
