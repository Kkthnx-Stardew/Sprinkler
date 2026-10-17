using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Sprinkler.Core;

/// <summary>
/// Reads CurseForge through the same public website endpoints the Download button uses.
/// This needs no account or API key, but it is not a documented API, so every failure
/// degrades to "open the page" instead of breaking the update.
/// </summary>
internal sealed class CurseForgeClient
{
    public async Task<SourceOffer?> FindAsync(long projectId, string targetVersion, bool allowPrerelease, CancellationToken ct)
    {
        var url = $"{AppEnvironment.CurseForgeUrl}/api/v1/mods/{projectId}/files?pageIndex=0&pageSize=20&sort=dateCreated&sortDescending=true&removeAlphas=true";
        FileList? list;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await AppEnvironment.Http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return PageOnly(projectId, Loc.T("core.cf_status", (int)response.StatusCode));
            list = await response.Content.ReadFromJsonAsync<FileList>(AppEnvironment.Json, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return PageOnly(projectId, Loc.T("core.cf_unreachable"));
        }

        var files = (list?.Data ?? new())
            .Where(f => f.Id > 0 && (allowPrerelease || f.ReleaseType == 1))
            .OrderByDescending(f => f.DateCreated)
            .ToList();
        if (files.Count == 0)
            return PageOnly(projectId, Loc.T("core.cf_no_files"));

        var target = ModVersion.Parse(targetVersion);
        foreach (var file in files)
        {
            var tokens = ModVersion.FindAll(file.DisplayName).Concat(ModVersion.FindAll(file.FileName)).ToList();
            if (target is null || tokens.Any(t => t >= target))
            {
                var best = tokens.Count > 0 ? tokens.Max() : null;
                return new SourceOffer
                {
                    Kind = SourceKind.CurseForge,
                    Mode = DeliveryMode.Auto,
                    Version = best?.ToString(),
                    PageUrl = PageUrl(projectId),
                    DownloadUrl = $"{AppEnvironment.CurseForgeUrl}/api/v1/mods/{projectId}/files/{file.Id}/download",
                    FileName = file.FileName,
                    Note = Loc.T("core.cf_direct"),
                };
            }
        }

        var newest = ModVersion.FindAll(files[0].DisplayName).FirstOrDefault();
        return new SourceOffer
        {
            Kind = SourceKind.CurseForge,
            Mode = DeliveryMode.Manual,
            Version = newest?.ToString(),
            PageUrl = PageUrl(projectId),
            Usable = false,
            Note = newest is null
                ? Loc.T("core.cf_no_match", targetVersion)
                : Loc.T("core.cf_older", newest, targetVersion),
        };
    }

    public static string PageUrl(long projectId) => $"{AppEnvironment.CurseForgeUrl}/projects/{projectId}";

    private static SourceOffer PageOnly(long projectId, string note) => new()
    {
        Kind = SourceKind.CurseForge,
        Mode = DeliveryMode.Manual,
        PageUrl = PageUrl(projectId),
        Usable = false,
        Note = note,
    };

    private sealed class FileList
    {
        public List<FileEntry>? Data { get; set; }
    }

    private sealed class FileEntry
    {
        public long Id { get; set; }
        public DateTime DateCreated { get; set; }
        public string? DisplayName { get; set; }
        public string? FileName { get; set; }
        public int ReleaseType { get; set; }
        [JsonPropertyName("fileLength")] public long FileLength { get; set; }
    }
}
