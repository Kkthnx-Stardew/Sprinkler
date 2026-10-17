using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sprinkler.Core;

internal sealed record NexusModInfo(string? PictureUrl, string? Summary, string? Author, long Downloads, long Endorsements);

/// <summary>What is known about one mod's picture. <see cref="File"/> is null when the site has none.</summary>
internal sealed class ArtEntry
{
    public string? File { get; set; }
    public string? Summary { get; set; }
    public string? Author { get; set; }
    public long Downloads { get; set; }
    public long Endorsements { get; set; }
    public DateTime FetchedUtc { get; set; }
}

/// <summary>
/// Mod pictures, like the icons on CurseForge and Nexus. Each picture is fetched once and kept on disk for two weeks,
/// so a normal start makes no requests at all. Nexus pictures come through the person's own key (one small request per
/// mod); GitHub mods use the author's avatar. Mods without either simply get a coloured initial in the window.
/// Nothing here is needed for updating, so every failure is quiet.
/// </summary>
internal sealed class ModArtStore
{
    private static readonly TimeSpan HitLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(3);
    private const int MaxImageBytes = 2_000_000;

    private readonly string _dir;
    private readonly NexusClient _nexus;
    private readonly object _gate = new();
    private readonly Dictionary<string, ArtEntry> _entries;
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _network = new(2, 2);
    private DateTime _pausedUntil = DateTime.MinValue;

    public ModArtStore(NexusClient nexus, string? dir = null)
    {
        _nexus = nexus;
        _dir = dir ?? Path.Combine(AppEnvironment.DataDir, "art");
        _entries = Load();
    }

    /// <summary>Raised when a picture arrives, from a background thread.</summary>
    public event Action? Changed;

    /// <summary>The key used to look up a mod's picture, such as "nexus:1915" or "github:Pathoschild". Null when it has no usable update key.</summary>
    public static string? KeyFor(ModInfo mod)
    {
        var keys = mod.ParsedKeys.ToList();
        if (keys.FirstOrDefault(k => k.Site == SourceKind.Nexus && k.Id.All(char.IsDigit)) is { } nexus)
            return "nexus:" + nexus.Id;
        if (keys.FirstOrDefault(k => k.Site == SourceKind.GitHub && k.Id.Contains('/')) is { } github)
            return "github:" + github.Id.Split('/')[0];
        return null;
    }

    public static string? KeyFor(long? nexusId, string? gitHubRepo)
    {
        if (nexusId is { } n)
            return "nexus:" + n;
        if (!string.IsNullOrEmpty(gitHubRepo) && gitHubRepo.Contains('/'))
            return "github:" + gitHubRepo.Split('/')[0];
        return null;
    }

    public ArtEntry? Peek(string? key)
    {
        if (key is null)
            return null;
        lock (_gate)
            return _entries.GetValueOrDefault(key);
    }

    /// <summary>The picture file for a key, or null.</summary>
    public string? PathFor(string? key)
    {
        var entry = Peek(key);
        if (entry?.File is null)
            return null;
        var path = Path.Combine(_dir, entry.File);
        return System.IO.File.Exists(path) ? path : null;
    }

    private bool IsFresh(ArtEntry e) =>
        DateTime.UtcNow - e.FetchedUtc < (e.File is null ? MissLifetime : HitLifetime) && (e.File is null || System.IO.File.Exists(Path.Combine(_dir, e.File)));

    /// <summary>Starts fetching a picture in the background unless one is fresh or already on its way.</summary>
    public void Request(string? key)
    {
        if (key is null || DateTime.UtcNow < _pausedUntil)
            return;
        lock (_gate)
        {
            if ((_entries.TryGetValue(key, out var existing) && IsFresh(existing)) || !_busy.Add(key))
                return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await FetchAsync(key);
            }
            catch (Exception ex)
            {
                Log.Write($"Mod picture {key}: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                    _busy.Remove(key);
            }
        });
    }

    private async Task FetchAsync(string key)
    {
        await _network.WaitAsync();
        try
        {
            var colon = key.IndexOf(':');
            var kind = key[..colon];
            var id = key[(colon + 1)..];

            ArtEntry? entry = null;
            if (kind == "nexus")
                entry = await FetchNexusAsync(id);
            else if (kind == "github")
                entry = await FetchGitHubAsync(id);

            if (entry is null)
                return; // nothing was learned (no key yet, or a network problem): try again next time

            lock (_gate)
                _entries[key] = entry;
            Save();
            Changed?.Invoke();
        }
        finally
        {
            _network.Release();
        }
    }

    private async Task<ArtEntry?> FetchNexusAsync(string modId)
    {
        NexusModInfo? info;
        try
        {
            info = await _nexus.GetModInfoAsync(modId, CancellationToken.None);
        }
        catch (NexusException ex) when (ex.Error == NexusError.NotFound)
        {
            return new ArtEntry { FetchedUtc = DateTime.UtcNow };
        }
        catch (NexusException ex) when (ex.Error is NexusError.RateLimited)
        {
            _pausedUntil = DateTime.UtcNow.AddMinutes(30);
            return null;
        }
        catch (NexusException)
        {
            return null;
        }
        if (info is null)
            return null; // no key saved yet

        string? file = null;
        if (IsAllowedImageUrl(info.PictureUrl, "nexusmods.com"))
            file = await DownloadAsync(info.PictureUrl!, "nexus-" + modId);

        return new ArtEntry
        {
            File = file,
            Summary = Trim(info.Summary, 400),
            Author = info.Author,
            Downloads = info.Downloads,
            Endorsements = info.Endorsements,
            FetchedUtc = DateTime.UtcNow,
        };
    }

    private async Task<ArtEntry?> FetchGitHubAsync(string owner)
    {
        if (owner.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            return new ArtEntry { FetchedUtc = DateTime.UtcNow };
        var url = $"{AppEnvironment.GitHubSiteUrl}/{owner}.png?size=96";
        var file = await DownloadAsync(url, "github-" + owner, allowedHosts: new[] { "github.com", "githubusercontent.com" });
        return file is null ? null : new ArtEntry { File = file, FetchedUtc = DateTime.UtcNow };
    }

    /// <summary>Downloads one picture, checks it really is an image and is small, and stores it. Returns the file name, or null.</summary>
    private async Task<string?> DownloadAsync(string url, string name, string[]? allowedHosts = null)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await AppEnvironment.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            // a redirect may have moved us; the final address must still be one we allow
            var finalHost = response.RequestMessage?.RequestUri?.Host ?? "";
            if (allowedHosts is not null && !allowedHosts.Any(h => finalHost == h || finalHost.EndsWith("." + h)) && !LocalAllowed(finalHost))
                return null;
            if (response.Content.Headers.ContentLength > MaxImageBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxImageBytes)
                    return null;
            }
            var bytes = buffer.ToArray();
            if (!LooksLikeImage(bytes))
                return null;

            Directory.CreateDirectory(_dir);
            var file = SafeName(name) + "." + Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant() + ".img";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(_dir, file), bytes);
            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool LocalAllowed(string host) =>
        Environment.GetEnvironmentVariable("SPRINKLER_ALLOW_LOCAL") == "1" && host is "127.0.0.1" or "localhost" or "[::1]";

    internal static bool IsAllowedImageUrl(string? url, string host)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return false;
        if (LocalAllowed(u.Host))
            return u.Scheme is "http" or "https";
        return u.Scheme == Uri.UriSchemeHttps && (u.Host == host || u.Host.EndsWith("." + host));
    }

    /// <summary>PNG, JPEG, GIF and WebP start with fixed bytes. Anything else (an HTML error page, say) is not stored.</summary>
    internal static bool LooksLikeImage(byte[] b) =>
        b.Length > 12 && (
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            || (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            || (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)
            || (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50));

    private static string SafeName(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "...";
    }

    private Dictionary<string, ArtEntry> Load()
    {
        try
        {
            var file = Path.Combine(_dir, "index.json");
            if (System.IO.File.Exists(file))
                return JsonSerializer.Deserialize<Dictionary<string, ArtEntry>>(System.IO.File.ReadAllText(file), AppEnvironment.Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("Could not read the picture index: " + ex.Message);
        }
        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string json;
            lock (_gate)
                json = JsonSerializer.Serialize(_entries, AppEnvironment.JsonPretty);
            var file = Path.Combine(_dir, "index.json");
            var temp = file + ".tmp";
            System.IO.File.WriteAllText(temp, json, new UTF8Encoding(false));
            System.IO.File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not save the picture index: " + ex.Message);
        }
    }
}
