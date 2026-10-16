using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewModUpdater.Core;

internal sealed record SmapiQuery(string Id, string[] UpdateKeys, string? InstalledVersion);

/// <summary>What SMAPI's public update service knows about one mod.</summary>
internal sealed record SmapiResult(
    string Id,
    string? Name,
    string? SuggestedVersion,
    string? SuggestedUrl,
    long? NexusId,
    long? CurseForgeId,
    long? ModDropId,
    string? GitHubRepo,
    string? MainVersion,
    string? OptionalVersion,
    string? UnofficialVersion,
    string? UnofficialUrl,
    string? CompatibilityStatus,
    string? CompatibilitySummary,
    string[] Errors);

/// <summary>Asks smapi.io, the same service SMAPI itself uses, which mods have updates. No account or key needed.</summary>
internal sealed class SmapiClient
{
    private const int BatchSize = 80;

    public async Task<Dictionary<string, SmapiResult>> CheckAsync(
        IReadOnlyList<SmapiQuery> queries, string? smapiVersion, string? gameVersion, CancellationToken ct)
    {
        var results = new Dictionary<string, SmapiResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in queries.Chunk(BatchSize))
        {
            var body = new
            {
                mods = batch.Select(q => new
                {
                    id = q.Id,
                    updateKeys = q.UpdateKeys,
                    installedVersion = q.InstalledVersion,
                }),
                apiVersion = smapiVersion ?? "4.0.0",
                gameVersion = gameVersion ?? "1.6.0",
                platform = "Windows",
                includeExtendedMetadata = true,
            };

            var items = await PostWithRetryAsync(body, ct);
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Id))
                    continue;
                var meta = item.Metadata;
                results[item.Id] = new SmapiResult(
                    item.Id,
                    meta?.Name,
                    item.SuggestedUpdate?.Version,
                    item.SuggestedUpdate?.Url,
                    meta?.NexusId,
                    meta?.CurseForgeId,
                    meta?.ModDropId,
                    meta?.GitHubRepo,
                    meta?.Main?.Version,
                    meta?.Optional?.Version,
                    meta?.Unofficial?.Version,
                    meta?.Unofficial?.Url,
                    meta?.CompatibilityStatus,
                    meta?.CompatibilitySummary,
                    item.Errors ?? Array.Empty<string>());
            }
        }
        return results;
    }

    private static async Task<List<ApiItem>> PostWithRetryAsync(object body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var response = await AppEnvironment.Http.PostAsJsonAsync(AppEnvironment.SmapiApiUrl, body, AppEnvironment.Json, timeout.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<ApiItem>>(AppEnvironment.Json, timeout.Token) ?? new();
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && (ex is HttpRequestException or OperationCanceledException))
            {
                await Task.Delay(1200, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException("the request timed out");
            }
        }
    }

    private sealed class ApiItem
    {
        public string? Id { get; set; }
        public VersionInfo? SuggestedUpdate { get; set; }
        public Meta? Metadata { get; set; }
        public string[]? Errors { get; set; }
    }

    private sealed class Meta
    {
        public string? Name { get; set; }
        [JsonPropertyName("nexusID")] public long? NexusId { get; set; }
        [JsonPropertyName("curseForgeID")] public long? CurseForgeId { get; set; }
        [JsonPropertyName("modDropID")] public long? ModDropId { get; set; }
        public string? GitHubRepo { get; set; }
        public VersionInfo? Main { get; set; }
        public VersionInfo? Optional { get; set; }
        public VersionInfo? Unofficial { get; set; }
        public string? CompatibilityStatus { get; set; }
        public string? CompatibilitySummary { get; set; }
    }

    private sealed class VersionInfo
    {
        public string? Version { get; set; }
        public string? Url { get; set; }
    }
}
