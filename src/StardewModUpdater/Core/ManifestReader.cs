using System.Text;
using System.Text.Json;

namespace StardewModUpdater.Core;

/// <summary>Reads SMAPI manifests. SMAPI accepts comments and trailing commas, so this does too.</summary>
internal static class ManifestReader
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The manifest file of a mod folder. SMAPI takes "manifest.json" exactly, and also matches other capitalizations
    /// when its UseCaseInsensitivePaths setting is on, which is the default on Linux. Windows and macOS file systems
    /// already ignore case, so this only changes the result on Linux.
    /// </summary>
    public static string? FindManifest(string folder)
    {
        var exact = Path.Combine(folder, "manifest.json");
        if (File.Exists(exact))
            return exact;
        try
        {
            return Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ModInfo? ReadFile(string manifestPath, string folderPath, bool disabled = false)
    {
        try
        {
            return Parse(File.ReadAllBytes(manifestPath), folderPath, disabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ModInfo? Parse(byte[] bytes, string folderPath, bool disabled = false)
    {
        var json = DecodeText(bytes);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, Lenient);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var root = doc.RootElement;
            var id = GetString(root, "UniqueID");
            if (string.IsNullOrWhiteSpace(id))
                return null;

            string? packFor = null, packMin = null;
            if (TryGet(root, "ContentPackFor", out var cp) && cp.ValueKind == JsonValueKind.Object)
            {
                packFor = GetString(cp, "UniqueID");
                packMin = GetString(cp, "MinimumVersion");
            }

            var deps = new List<ModDependency>();
            if (TryGet(root, "Dependencies", out var depArr) && depArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in depArr.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object)
                        continue;
                    var did = GetString(d, "UniqueID");
                    if (string.IsNullOrWhiteSpace(did))
                        continue;
                    var required = true;
                    if (TryGet(d, "IsRequired", out var req) && req.ValueKind is JsonValueKind.False)
                        required = false;
                    deps.Add(new ModDependency(did.Trim(), GetString(d, "MinimumVersion"), required));
                }
            }

            var keys = new List<string>();
            if (TryGet(root, "UpdateKeys", out var keyArr) && keyArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in keyArr.EnumerateArray())
                {
                    if (k.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(k.GetString()))
                        keys.Add(k.GetString()!.Trim());
                }
            }

            var folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
            return new ModInfo
            {
                UniqueId = id.Trim(),
                Name = GetString(root, "Name")?.Trim() is { Length: > 0 } n ? n : folderName,
                Author = GetString(root, "Author")?.Trim() ?? "",
                Version = GetString(root, "Version")?.Trim() ?? "0.0.0",
                FolderPath = folderPath,
                FolderName = folderName,
                UpdateKeys = keys.ToArray(),
                ContentPackFor = string.IsNullOrWhiteSpace(packFor) ? null : packFor.Trim(),
                ContentPackForMinVersion = packMin,
                Dependencies = deps.ToArray(),
                MinimumApiVersion = GetString(root, "MinimumApiVersion"),
                Disabled = disabled,
                Bundled = id.StartsWith("SMAPI.", StringComparison.OrdinalIgnoreCase),
            };
        }
    }

    public static ManifestSummary? Summarize(byte[] bytes)
    {
        var info = Parse(bytes, "");
        return info is null ? null : new ManifestSummary(info.UniqueId, info.Name, info.Version);
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string name) =>
        TryGet(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
