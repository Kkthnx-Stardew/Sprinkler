using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sprinkler.Core;

internal sealed record NexusUser(string Name, bool IsPremium);

internal enum EndorseOutcome
{
    Endorsed,
    Abstained,
    IsOwnMod,
    TooSoonAfterDownload,
    NotDownloaded,
    Unknown,
}

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
                Note = Loc.T("core.nx_login"),
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
                Note = Loc.T("core.nx_premium"),
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
                Note = Loc.T("core.nx_oneclick"),
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
            Note = Loc.T("core.nx_manual"),
        };
    }

    public async Task<string> GetDownloadUrlAsync(string modId, string fileId, string? nxmKey, string? expires, CancellationToken ct)
    {
        var key = keyProvider() ?? throw new NexusException(NexusError.InvalidKey, Loc.T("core.nx_no_key"));
        var path = $"/games/{AppEnvironment.NexusGame}/mods/{modId}/files/{fileId}/download_link.json";
        if (!string.IsNullOrEmpty(nxmKey) && !string.IsNullOrEmpty(expires))
            path += $"?key={Uri.EscapeDataString(nxmKey)}&expires={Uri.EscapeDataString(expires)}";

        var links = await GetAsync<List<DownloadLink>>(path, key, ct);
        var url = links.FirstOrDefault(l => !string.IsNullOrEmpty(l.Uri))?.Uri;
        return url ?? throw new NexusException(NexusError.Other, Loc.T("core.nx_no_link"));
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

    /// <summary>The public details of a mod page: its picture, a short summary, who made it, and how popular it is.</summary>
    public async Task<NexusModInfo?> GetModInfoAsync(string modId, CancellationToken ct)
    {
        var key = keyProvider();
        if (string.IsNullOrEmpty(key))
            return null;
        var mod = await GetAsync<ModResponse>($"/games/{AppEnvironment.NexusGame}/mods/{modId}.json", key, ct);
        return new NexusModInfo(mod.PictureUrl, mod.Summary, mod.Author, mod.Downloads, mod.Endorsements);
    }

    /// <summary>The Stardew Valley mods the person has endorsed on Nexus.</summary>
    public async Task<HashSet<long>> GetEndorsedAsync(CancellationToken ct)
    {
        var key = keyProvider();
        var endorsed = new HashSet<long>();
        if (string.IsNullOrEmpty(key))
            return endorsed;
        var (status, body) = await SendRawAsync(HttpMethod.Get, "/user/endorsements.json", key, null, ct);
        if (status != HttpStatusCode.OK)
            throw new NexusException(status == HttpStatusCode.TooManyRequests ? NexusError.RateLimited : NexusError.Other, Loc.T("core.nx_status", (int)status));
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var domain = e.TryGetProperty("domain_name", out var d) ? d.GetString() : null;
                var state = e.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (string.Equals(domain, AppEnvironment.NexusGame, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(state, "Endorsed", StringComparison.OrdinalIgnoreCase)
                    && e.TryGetProperty("mod_id", out var id) && id.TryGetInt64(out var modId))
                    endorsed.Add(modId);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new NexusException(NexusError.Other, Loc.T("core.nx_unreadable"));
        }
        return endorsed;
    }

    /// <summary>Endorses a mod, or takes the endorsement back. Nexus decides whether the person is allowed to.</summary>
    public async Task<EndorseOutcome> SetEndorsementAsync(long modId, bool endorse, string version, CancellationToken ct)
    {
        var key = keyProvider() ?? throw new NexusException(NexusError.InvalidKey, Loc.T("core.nx_no_key"));
        var path = $"/games/{AppEnvironment.NexusGame}/mods/{modId}/{(endorse ? "endorse" : "abstain")}.json";
        var payload = JsonSerializer.Serialize(new { Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version });
        var (status, body) = await SendRawAsync(HttpMethod.Post, path, key, payload, ct);
        if (status == HttpStatusCode.TooManyRequests)
            throw new NexusException(NexusError.RateLimited, Loc.T("core.nx_rate"));
        if (status == HttpStatusCode.Unauthorized)
            throw new NexusException(NexusError.InvalidKey, Loc.T("core.nx_bad_key"));

        string? state = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            state = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
        }

        if (string.Equals(state, "ENDORSED", StringComparison.OrdinalIgnoreCase)) return EndorseOutcome.Endorsed;
        if (string.Equals(state, "ABSTAINED", StringComparison.OrdinalIgnoreCase)) return EndorseOutcome.Abstained;
        return (message ?? "").ToUpperInvariant() switch
        {
            "IS_OWN_MOD" => EndorseOutcome.IsOwnMod,
            "TOO_SOON_AFTER_DOWNLOAD" => EndorseOutcome.TooSoonAfterDownload,
            "NOT_DOWNLOADED_MOD" => EndorseOutcome.NotDownloaded,
            _ => EndorseOutcome.Unknown,
        };
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendRawAsync(HttpMethod method, string path, string key, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, AppEnvironment.NexusApiUrl + path);
        request.Headers.TryAddWithoutValidation("apikey", key);
        request.Headers.TryAddWithoutValidation("Application-Name", AppEnvironment.AppName);
        request.Headers.TryAddWithoutValidation("Application-Version", AppEnvironment.Version);
        if (json is not null)
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var response = await AppEnvironment.Http.SendAsync(request, timeout.Token);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;
            throw new NexusException(NexusError.Network, Loc.T("core.nx_unreachable"));
        }
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
            throw new NexusException(NexusError.Network, Loc.T("core.nx_unreachable"));
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new NexusException(NexusError.InvalidKey, Loc.T("core.nx_bad_key"));
                case HttpStatusCode.Forbidden:
                    throw new NexusException(NexusError.PremiumRequired, Loc.T("core.nx_premium_only"));
                case HttpStatusCode.NotFound:
                    throw new NexusException(NexusError.NotFound, Loc.T("core.nx_not_found"));
                case HttpStatusCode.TooManyRequests:
                    throw new NexusException(NexusError.RateLimited, Loc.T("core.nx_rate"));
            }
            if (!response.IsSuccessStatusCode)
                throw new NexusException(NexusError.Other, Loc.T("core.nx_status", (int)response.StatusCode));

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(AppEnvironment.Json, timeout.Token)
                    ?? throw new NexusException(NexusError.Other, Loc.T("core.nx_empty"));
            }
            catch (JsonException)
            {
                throw new NexusException(NexusError.Other, Loc.T("core.nx_unreadable"));
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

    private sealed class ModResponse
    {
        [JsonPropertyName("picture_url")] public string? PictureUrl { get; set; }
        public string? Summary { get; set; }
        public string? Author { get; set; }
        [JsonPropertyName("mod_downloads")] public long Downloads { get; set; }
        [JsonPropertyName("endorsement_count")] public long Endorsements { get; set; }
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
