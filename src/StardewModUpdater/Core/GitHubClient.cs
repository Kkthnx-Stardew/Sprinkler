using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace StardewModUpdater.Core;

/// <summary>Finds a release zip on GitHub through its public API (60 requests per hour without an account).</summary>
internal sealed class GitHubClient
{
    private static readonly string[] OtherPlatforms = { "linux", "macos", "mac-", "android", "-src", "source" };

    public async Task<SourceOffer?> FindAsync(string repo, string targetVersion, string modName, string uniqueId, bool allowPrerelease, CancellationToken ct)
    {
        var page = $"https://github.com/{repo}/releases";
        List<Release>? releases;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AppEnvironment.GitHubApiUrl}/repos/{repo}/releases?per_page=12");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await AppEnvironment.Http.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Unusable(page, "GitHub's hourly limit for anonymous requests is used up. Try again later.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Unusable(page, "The GitHub repository was not found.");
            if (!response.IsSuccessStatusCode)
                return Unusable(page, $"GitHub answered {(int)response.StatusCode}.");
            releases = await response.Content.ReadFromJsonAsync<List<Release>>(AppEnvironment.Json, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return Unusable(page, "GitHub could not be reached.");
        }

        var target = ModVersion.Parse(targetVersion);
        foreach (var rel in (releases ?? new()).Where(r => !r.Draft && (allowPrerelease || !r.Prerelease)))
        {
            var tokens = ModVersion.FindAll(rel.TagName).Concat(ModVersion.FindAll(rel.Name)).ToList();
            if (target is not null && !tokens.Any(t => t >= target))
                continue;

            var asset = PickAsset(rel.Assets ?? new(), modName, uniqueId);
            if (asset is null)
                return Unusable(rel.HtmlUrl ?? page, "The release on GitHub has no zip, 7z or rar file attached.");

            return new SourceOffer
            {
                Kind = SourceKind.GitHub,
                Mode = DeliveryMode.Auto,
                Version = (tokens.Count > 0 ? tokens.Max() : null)?.ToString(),
                PageUrl = rel.HtmlUrl ?? page,
                DownloadUrl = asset.BrowserDownloadUrl,
                FileName = asset.Name,
                Note = "GitHub has this version and the app can download it without any clicks.",
            };
        }

        return Unusable(page, "GitHub has no release with this version yet.");
    }

    private static Asset? PickAsset(List<Asset> assets, string modName, string uniqueId)
    {
        var archives = assets
            .Where(a => a.BrowserDownloadUrl is not null && IsArchive(a.Name))
            .ToList();
        if (archives.Count <= 1)
            return archives.FirstOrDefault();

        var clean = archives.Where(a => !OtherPlatforms.Any(p => a.Name!.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (clean.Count == 0)
            clean = archives;

        var compactName = Compact(modName);
        var compactId = Compact(uniqueId.Split('.').Last());
        return clean.FirstOrDefault(a => Compact(a.Name!).Contains(compactName) && compactName.Length > 2)
            ?? clean.FirstOrDefault(a => Compact(a.Name!).Contains(compactId) && compactId.Length > 2)
            ?? clean[0];
    }

    private static bool IsArchive(string? name) =>
        name is not null && (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase));

    private static string Compact(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static SourceOffer Unusable(string page, string note) => new()
    {
        Kind = SourceKind.GitHub,
        Mode = DeliveryMode.Manual,
        PageUrl = page,
        Usable = false,
        Note = note,
    };

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        public string? Name { get; set; }
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        public List<Asset>? Assets { get; set; }
    }

    private sealed class Asset
    {
        public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
