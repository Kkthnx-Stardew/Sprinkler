namespace Sprinkler.Core;

internal enum SourceKind
{
    Nexus,
    CurseForge,
    GitHub,
    ModDrop,
}

/// <summary>One entry of a manifest's UpdateKeys, such as "Nexus:1915@main".</summary>
internal sealed record UpdateKey(SourceKind Site, string Id, string? Subkey)
{
    public static UpdateKey? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var colon = raw.IndexOf(':');
        if (colon <= 0)
            return null;

        var site = raw[..colon].Trim();
        var rest = raw[(colon + 1)..].Trim();
        string? sub = null;
        var at = rest.IndexOf('@');
        if (at >= 0)
        {
            sub = rest[(at + 1)..].Trim();
            rest = rest[..at].Trim();
            if (sub.Length == 0)
                sub = null;
        }
        if (rest.Length == 0)
            return null;

        SourceKind kind;
        if (site.Equals("Nexus", StringComparison.OrdinalIgnoreCase)) kind = SourceKind.Nexus;
        else if (site.Equals("CurseForge", StringComparison.OrdinalIgnoreCase)) kind = SourceKind.CurseForge;
        else if (site.Equals("GitHub", StringComparison.OrdinalIgnoreCase)) kind = SourceKind.GitHub;
        else if (site.Equals("ModDrop", StringComparison.OrdinalIgnoreCase)) kind = SourceKind.ModDrop;
        else return null;

        return new UpdateKey(kind, rest, sub);
    }
}

internal sealed record ModDependency(string UniqueId, string? MinimumVersion, bool Required);

/// <summary>A mod folder found in the Mods directory.</summary>
internal sealed class ModInfo
{
    public string UniqueId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string[] UpdateKeys { get; init; } = Array.Empty<string>();
    public string? ContentPackFor { get; init; }
    public string? ContentPackForMinVersion { get; init; }
    public ModDependency[] Dependencies { get; init; } = Array.Empty<ModDependency>();
    public string? MinimumApiVersion { get; init; }
    public bool Disabled { get; init; }
    public bool Bundled { get; init; }

    public IEnumerable<UpdateKey> ParsedKeys => UpdateKeys.Select(UpdateKey.Parse).OfType<UpdateKey>();
}

/// <summary>The parts of a manifest that matter for matching an archive to an installed mod.</summary>
internal sealed record ManifestSummary(string UniqueId, string Name, string Version, string[]? UpdateKeys = null)
{
    /// <summary>True when the manifest points at this Nexus mod page.</summary>
    public bool HasNexusKey(long modId) =>
        (UpdateKeys ?? Array.Empty<string>()).Select(UpdateKey.Parse)
            .Any(k => k is { Site: SourceKind.Nexus } && k.Id == modId.ToString());
}

internal sealed record GameInstall(string GamePath, string ModsPath, string Store, string? GameVersion, string? SmapiVersion)
{
    public bool HasSmapi => SmapiVersion is not null;
}
