namespace Sprinkler.Core;

/// <summary>Which web addresses the app will open for the user. Anything else is refused, so a page or a mod description cannot send the user somewhere unexpected.</summary>
internal static class UrlPolicy
{
    private static readonly string[] OpenableHosts =
    {
        "nexusmods.com", "curseforge.com", "github.com", "moddrop.com", "smapi.io", "stardewvalleywiki.com", "wiki.stardewvalley.net",
    };

    internal static bool TryOpenable(string? raw, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = u.Host.ToLowerInvariant();
        if (!OpenableHosts.Any(h => host == h || host.EndsWith("." + h)))
            return false;
        uri = u;
        return true;
    }

    /// <summary>Opens an allowed address in the default browser. Returns false when the address is not allowed.</summary>
    public static bool Open(string? url)
    {
        if (TryOpenable(url, out var uri) || (Environment.GetEnvironmentVariable("SPRINKLER_ALLOW_LOCAL") == "1" && Uri.TryCreate(url, UriKind.Absolute, out uri!)))
        {
            Platform.OpenUrl(uri.ToString());
            return true;
        }
        return false;
    }
}
