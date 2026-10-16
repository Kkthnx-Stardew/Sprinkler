using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sprinkler.Core;

internal sealed record NexusUser(string Name, bool IsPremium);

internal enum NexusError
{
    InvalidKey,
    PremiumRequired,
    RateLimited,
    NotFound,
    Network,
    Other,
}

internal sealed class NexusException(NexusError error, string message) : Exception(message)
{
    public NexusError Error { get; } = error;
}

/// <summary>
/// Talks to the official Nexus Mods API with the user's own personal key. Free accounts can read
/// file lists but only get download links when they click "Mod Manager Download" on the site,
/// which hands over a one-time key. Premium accounts get links straight from the API.
/// </summary>
internal sealed class NexusClient(Func<string?> keyProvider)
{
    public static string ModPage(string modId) => $"{AppEnvironment.NexusSiteUrl}/{AppEnvironment.NexusGame}/mods/{modId}";

    public static string FilesPage(string modId, string? fileId = null) =>
        ModPage(modId) + "?tab=files" + (fileId is null ? "" : "&file_id=" + fileId);

    public async Task<NexusUser> ValidateAsync(string key, CancellationToken ct)
    {
        var doc = await GetAsync<ValidateResponse>("/users/validate.json", key, ct);
        return new NexusUser(doc.Name ?? "your account", doc.IsPremium);
    }

    public async Task<SourceOffer> FindAsync(
        string modId, string targetVersion, string? subkey, NexusUser? user, bool nxmRegistered, CancellationToken ct)
    {
        var key = keyProvider();
        if (string.IsNullOrEmpty(key) || user is null)
        {
            return new SourceOffer
            {
                Kind = SourceKind.Nexus,
                Mode = DeliveryMode.Manual,
                Version = targetVersion,
                PageUrl = FilesPage(modId),
                NexusModId = modId,
                Note = "Nexus needs you to be logged in to download. The app opens the file page; you click Manual Download, and the app installs the file when it lands in your Downloads folder.",
            };
        }

        FilesResponse files;
        try
        {
            files = await GetAsync<FilesResponse>($"/games/{AppEnvironment.NexusGame}/mods/{modId}/files.json?category=main,update,optional", key, ct);
        }
        catch (NexusException ex) when (ex.Error is NexusError.NotFound or NexusError.Network or NexusError.RateLimited)
        {
            return new SourceOffer
            {
                Kind = SourceKind.Nexus,
                Mode = DeliveryMode.Manual,
                Version = targetVersion,
                PageUrl = FilesPage(modId),
                NexusModId = modId,
                Note = ex.Message + " The app opens the page instead so you can download by hand.",
            };
        }

        var file = Pick(files.Files ?? new(), targetVersion, subkey);
        var fileId = file?.FileId.ToString();
        var page = FilesPage(modId, fileId);
        var version = file is null ? targetVersion : (file.ModVersion ?? file.Version ?? targetVersion);

        if (user.IsPremium && file is not null)
        {
            return new SourceOffer
            {
                Kind = SourceKind.Nexus,
                Mode = DeliveryMode.Auto,
                Version = version,
                PageUrl = page,
                FileName = file.FileName,
                NexusModId = modId,
                NexusFileId = fileId,
                Note = "Your Nexus Premium account lets the app download this directly.",
            };
        }

        if (nxmRegistered && file is not null)
        {
            return new SourceOffer
            {
                Kind = SourceKind.Nexus,
                Mode = DeliveryMode.OneClick,
                Version = version,
                PageUrl = page,
                FileName = file.FileName,
                NexusModId = modId,
                NexusFileId = fileId,
                Note = "The app opens the Nexus page. Click Mod Manager Download and the app does the rest.",
            };
        }

        return new SourceOffer
        {
            Kind = SourceKind.Nexus,
            Mode = DeliveryMode.Manual,
            Version = version,
            PageUrl = page,
            FileName = file?.FileName,
            NexusModId = modId,
            NexusFileId = fileId,
            Note = "The app opens the Nexus page. Click Manual Download, then Slow Download, and the app installs the file when it lands in your Downloads folder.",
        };
    }

    public async Task<string> GetDownloadUrlAsync(string modId, string fileId, string? nxmKey, string? expires, CancellationToken ct)
    {
        var key = keyProvider() ?? throw new NexusException(NexusError.InvalidKey, "No Nexus key is saved.");
        var path = $"/games/{AppEnvironment.NexusGame}/mods/{modId}/files/{fileId}/download_link.json";
        if (!string.IsNullOrEmpty(nxmKey) && !string.IsNullOrEmpty(expires))
            path += $"?key={Uri.EscapeDataString(nxmKey)}&expires={Uri.EscapeDataString(expires)}";

        var links = await GetAsync<List<DownloadLink>>(path, key, ct);
        var url = links.FirstOrDefault(l => !string.IsNullOrEmpty(l.Uri))?.Uri;
        return url ?? throw new NexusException(NexusError.Other, "Nexus returned no download link.");
    }

    /// <summary>Changelog lines for one version, or an empty list.</summary>
    public async Task<List<string>> GetChangelogAsync(string modId, string version, CancellationToken ct)
    {
        var key = keyProvider();
        if (string.IsNullOrEmpty(key))
            return new();
        try
        {
            var all = await GetAsync<Dictionary<string, List<string>>>($"/games/{AppEnvironment.NexusGame}/mods/{modId}/changelogs.json", key, ct);
            var target = ModVersion.Parse(version);
            foreach (var (k, lines) in all)
            {
                if (k == version || (target is not null && ModVersion.Parse(k) is { } kv && kv == target))
                    return lines.Select(StripHtml).Where(l => l.Length > 0).Take(12).ToList();
            }
        }
        catch (NexusException)
        {
        }
        return new();
    }

    internal static FileEntry? Pick(List<FileEntry> files, string targetVersion, string? subkey)
    {
        var target = ModVersion.Parse(targetVersion);
        var candidates = files
            .Where(f => !string.Equals(f.CategoryName, "OLD_VERSION", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(f.CategoryName, "ARCHIVED", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(f.CategoryName, "DELETED", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return null;

        int Score(FileEntry f)
        {
            var score = 0;
            var versions = new[] { f.ModVersion, f.Version }.Select(ModVersion.Parse).OfType<ModVersion>().ToList();
            if (target is not null && versions.Any(v => v == target)) score += 100;
            else if (target is not null && versions.Any(v => v > target)) score += 40;
            if (!string.IsNullOrEmpty(subkey)
                && ((f.Name?.Contains(subkey, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (f.Description?.Contains(subkey, StringComparison.OrdinalIgnoreCase) ?? false)))
                score += 60;
            if (string.Equals(f.CategoryName, "MAIN", StringComparison.OrdinalIgnoreCase)) score += 20;
            if (f.IsPrimary) score += 10;
            return score;
        }

        return candidates
            .OrderByDescending(Score)
            .ThenByDescending(f => f.UploadedTimestamp)
            .First();
    }

    private async Task<T> GetAsync<T>(string path, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AppEnvironment.NexusApiUrl + path);
        request.Headers.TryAddWithoutValidation("apikey", key);
        request.Headers.TryAddWithoutValidation("Application-Name", AppEnvironment.AppName);
        request.Headers.TryAddWithoutValidation("Application-Version", AppEnvironment.Version);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        HttpResponseMessage response;
        try
        {
            response = await AppEnvironment.Http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;
            throw new NexusException(NexusError.Network, "Nexus could not be reached.");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new NexusException(NexusError.InvalidKey, "Nexus did not accept the key.");
                case HttpStatusCode.Forbidden:
                    throw new NexusException(NexusError.PremiumRequired, "Nexus only allows this for Premium accounts or with a one-time key from the website.");
                case HttpStatusCode.NotFound:
                    throw new NexusException(NexusError.NotFound, "Nexus has no such mod or file.");
                case HttpStatusCode.TooManyRequests:
                    throw new NexusException(NexusError.RateLimited, "Nexus is rate limiting this key for now.");
            }
            if (!response.IsSuccessStatusCode)
                throw new NexusException(NexusError.Other, $"Nexus answered {(int)response.StatusCode}.");

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(AppEnvironment.Json, timeout.Token)
                    ?? throw new NexusException(NexusError.Other, "Nexus sent an empty answer.");
            }
            catch (JsonException)
            {
                throw new NexusException(NexusError.Other, "Nexus sent an answer the app could not read.");
            }
        }
    }

    private static string StripHtml(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "").Trim();

    private sealed class ValidateResponse
    {
        public string? Name { get; set; }
        [JsonPropertyName("is_premium")] public bool IsPremium { get; set; }
    }

    private sealed class FilesResponse
    {
        public List<FileEntry>? Files { get; set; }
    }

    internal sealed class FileEntry
    {
        [JsonPropertyName("file_id")] public long FileId { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        [JsonPropertyName("mod_version")] public string? ModVersion { get; set; }
        [JsonPropertyName("category_name")] public string? CategoryName { get; set; }
        [JsonPropertyName("is_primary")] public bool IsPrimary { get; set; }
        [JsonPropertyName("file_name")] public string? FileName { get; set; }
        [JsonPropertyName("uploaded_timestamp")] public long UploadedTimestamp { get; set; }
        public string? Description { get; set; }
    }

    private sealed class DownloadLink
    {
        [JsonPropertyName("URI")] public string? Uri { get; set; }
    }
}
