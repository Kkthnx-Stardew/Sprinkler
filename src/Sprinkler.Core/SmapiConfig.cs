using System.Text.Json;

namespace Sprinkler.Core;

/// <summary>
/// The parts of SMAPI's own settings that change what an update check should show. SMAPI reads
/// smapi-internal/config.json, then smapi-internal/config.user.json, then Mods/SMAPI-config.json, each one applied over the
/// last. Its JSON reader adds the entries of a later list to the earlier one instead of replacing it, so the lists are joined here too.
/// </summary>
internal static class SmapiConfig
{
    private const long MaxBytes = 1_000_000;

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The mod IDs SMAPI is told not to check for updates. Missing or unreadable files add nothing.</summary>
    public static HashSet<string> SuppressedUpdateChecks(string gamePath, string modsPath)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[]
        {
            Path.Combine(gamePath, "smapi-internal", "config.json"),
            Path.Combine(gamePath, "smapi-internal", "config.user.json"),
            Path.Combine(modsPath, "SMAPI-config.json"),
        })
        {
            AddFrom(file, ids);
        }
        return ids;
    }

    private static void AddFrom(string file, HashSet<string> ids)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxBytes)
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // SMAPI's reader matches setting names without regard to case
                if (!prop.Name.Equals("SuppressUpdateChecks", StringComparison.OrdinalIgnoreCase) || prop.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString()?.Trim() is { Length: > 0 } id)
                        ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write($"Could not read SMAPI's settings in {file}: {ex.Message}");
        }
    }
}
