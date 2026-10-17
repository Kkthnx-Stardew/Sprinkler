using Sprinkler.Core;

namespace Sprinkler.ViewModels;

internal sealed record QA(string Question, string Answer);

/// <summary>The words the app uses to explain itself: the tour, the FAQ and the troubleshooting list. All text comes from the language files.</summary>
internal static class Content
{
    public static string Tagline => Loc.T("brand.tagline");

    private static readonly string[] TourIcons = { "sprinkler", "package", "arrowup", "download", "layers", "shield", "key" };

    public static IReadOnlyList<TourStep> Tour =>
        TourIcons.Select((icon, i) => new TourStep(icon, Loc.T($"tour.{i + 1}.title"), Loc.T($"tour.{i + 1}.text"))).ToList();

    private static readonly string[] FaqIds =
        { "safe", "why_click", "cannot_check", "missing_mod", "backups", "settings", "profile", "collection", "removed_back", "remove_app" };

    public static IReadOnlyList<QA> Faq(string dataDir) =>
        FaqIds.Select(id => new QA(Loc.T($"faq.{id}.q"), Loc.T($"faq.{id}.a", dataDir))).ToList();

    private static readonly string[] FixIds = { "smapi", "closed_game", "key", "failed", "window" };

    public static IReadOnlyList<QA> Fixes =>
        FixIds.Select(id => new QA(Loc.T($"fix.{id}.q"), Loc.T($"fix.{id}.a"))).ToList();

    public static string FindGameHelp(string os)
    {
        var steam = Loc.T("help.find_game_steam") + " ";
        return os switch
        {
            "Linux" => steam + Loc.T("help.find_game_linux"),
            "Mac" => steam + Loc.T("help.find_game_mac"),
            _ => steam + Loc.T("help.find_game_windows"),
        };
    }

    /// <summary>An example folder shown in the empty box. These are paths, so they are not translated.</summary>
    public static string GamePathExample(string os) => os switch
    {
        "Linux" => "/home/you/.local/share/Steam/steamapps/common/Stardew Valley",
        "Mac" => "/Users/you/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS",
        _ => "D:\\SteamLibrary\\steamapps\\common\\Stardew Valley",
    };
}
