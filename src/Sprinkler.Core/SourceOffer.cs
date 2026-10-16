namespace Sprinkler.Core;

internal enum DeliveryMode
{
    /// <summary>The app downloads the file itself.</summary>
    Auto,

    /// <summary>The user clicks "Mod Manager Download" on Nexus and the app finishes the job.</summary>
    OneClick,

    /// <summary>The user downloads the file in their browser and the app picks it up.</summary>
    Manual,
}

/// <summary>One way to get a specific update, with an honest description of what it needs from the user.</summary>
internal sealed class SourceOffer
{
    public SourceKind Kind { get; init; }
    public DeliveryMode Mode { get; init; }
    public string? Version { get; init; }
    public string PageUrl { get; init; } = "";
    public string? DownloadUrl { get; init; }
    public string? FileName { get; init; }
    public string? NexusModId { get; init; }
    public string? NexusFileId { get; init; }

    /// <summary>False when the site was checked but cannot supply this version.</summary>
    public bool Usable { get; init; } = true;

    /// <summary>Plain-language explanation shown next to the offer.</summary>
    public string Note { get; init; } = "";

    public string Label => Kind switch
    {
        SourceKind.Nexus => "Nexus Mods",
        SourceKind.CurseForge => "CurseForge",
        SourceKind.GitHub => "GitHub",
        SourceKind.ModDrop => "ModDrop",
        _ => Kind.ToString(),
    };
}
