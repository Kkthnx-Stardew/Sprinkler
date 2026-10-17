using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sprinkler.Core;

/// <summary>One mod in a Nexus collection: which mod, which file the curator tested, and whether it is optional.</summary>
internal sealed record CollectionMod(long ModId, long FileId, string? Version, string Name, string? Author, bool Optional);

/// <summary>A collection as Nexus describes it. Name and curator come from the page, the mods from the revision.</summary>
internal sealed record CollectionRevision(string Slug, int Revision, string Name, string? Curator, string? Summary, List<CollectionMod> Mods);

internal sealed class CollectionException(string message) : Exception(message);

/// <summary>
/// Reads Nexus Collections through Nexus's public GraphQL API. Reading a collection needs no account, so looking one up
/// never uses the person's key. Collections are lists, not downloads: every mod still comes through the normal update flow.
/// </summary>
internal static partial class NexusCollections
{
    /// <summary>SMAPI itself is listed in many collections. It has its own installer, so it is skipped here.</summary>
    private const long SmapiModId = 2400;

    [GeneratedRegex(@"nexusmods\.com/(?:games/)?(?<domain>[a-z0-9]+)/collections/(?<slug>[a-z0-9]+)(?:/revisions/(?<rev>[0-9]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^[a-z0-9]{4,16}$", RegexOptions.IgnoreCase)]
    private static partial Regex SlugPattern();

    /// <summary>Accepts a collection address or a bare code such as "htknoa". Returns null for anything else.</summary>
    public static (string Slug, int? Revision)? Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 300)
            return null;
        var m = UrlPattern().Match(t);
        if (m.Success)
        {
            if (!m.Groups["domain"].Value.Equals(AppEnvironment.NexusGame, StringComparison.OrdinalIgnoreCase)
                && !m.Groups["domain"].Value.Equals("games", StringComparison.OrdinalIgnoreCase))
                throw new CollectionException(Loc.T("core.col_other_game"));
            return (m.Groups["slug"].Value.ToLowerInvariant(), m.Groups["rev"].Success ? int.Parse(m.Groups["rev"].Value) : null);
        }
        return SlugPattern().IsMatch(t) ? (t.ToLowerInvariant(), null) : null;
    }

    private const string Query = """
        query Sprinkler($slug: String, $domainName: String, $revision: Int) {
          collectionRevision(slug: $slug, domainName: $domainName, revision: $revision, viewAdultContent: true) {
            revisionNumber
            collection { name summary user { name } }
            modFiles { fileId optional file { fileId name version mod { modId name author } } }
          }
        }
        """;

    public static async Task<CollectionRevision> FetchAsync(string slug, int? revision, CancellationToken ct)
    {
        var body = new { query = Query, variables = new { slug, domainName = AppEnvironment.NexusGame, revision } };
        JsonDocument doc;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await AppEnvironment.Http.PostAsJsonAsync(AppEnvironment.NexusGraphQlUrl, body, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new CollectionException(Loc.T("core.col_status", (int)response.StatusCode));
            doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;
            throw new CollectionException(Loc.T("core.nx_unreachable"));
        }
        catch (JsonException)
        {
            throw new CollectionException(Loc.T("core.nx_unreadable"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("collectionRevision", out var rev) || rev.ValueKind != JsonValueKind.Object)
                throw new CollectionException(Loc.T("core.col_missing", slug));

            var info = rev.TryGetProperty("collection", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var name = Text(info, "name") ?? slug;
            var curator = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Text(u, "name") : null;

            var mods = new List<CollectionMod>();
            if (rev.TryGetProperty("modFiles", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in files.EnumerateArray())
                {
                    if (!f.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object
                        || !file.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object
                        || !mod.TryGetProperty("modId", out var id) || !id.TryGetInt64(out var modId) || modId == SmapiModId)
                        continue;
                    var fileId = f.TryGetProperty("fileId", out var fid) && fid.TryGetInt64(out var fi) ? fi : 0;
                    var optional = f.TryGetProperty("optional", out var o) && o.ValueKind == JsonValueKind.True;
                    mods.Add(new CollectionMod(modId, fileId, Text(file, "version"), Text(mod, "name") ?? "Mod " + modId, Text(mod, "author"), optional));
                }
            }

            var number = rev.TryGetProperty("revisionNumber", out var rn) && rn.TryGetInt32(out var r) ? r : revision ?? 1;
            return new CollectionRevision(slug, number, name.Trim(), curator, Clean(Text(info, "summary")), mods.DistinctBy(m => m.ModId).ToList());
        }
    }

    private static string? Text(JsonElement e, string property) => Plain(RawText(e, property));

    private static string? Plain(string? s) => s is null ? null : Regex.Replace(s, "<[^>]+>", "").Trim();

    private static string? RawText(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        s = Regex.Replace(s, "<[^>]+>", "").Trim();
        return s.Length <= 600 ? s : s[..600].TrimEnd() + "...";
    }
}
