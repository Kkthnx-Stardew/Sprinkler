namespace Sprinkler.Core;

/// <summary>Finds installed mods the way SMAPI does: any folder with a manifest is a mod, other folders are searched deeper.</summary>
internal static class ModScanner
{
    public static List<ModInfo> Scan(string modsPath)
    {
        var mods = new List<ModInfo>();
        if (Directory.Exists(modsPath))
            ScanFolder(modsPath, mods, disabled: false, isRoot: true);

        return mods
            .OrderBy(m => m.Bundled)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Folder names SMAPI never treats as mods (its ModScanner ignore list): Vortex and macOS leftovers.</summary>
    internal static bool IsIgnoredFolder(string name) =>
        name.Equals("__folder_managed_by_vortex", StringComparison.OrdinalIgnoreCase)
        || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mcs", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("._", StringComparison.Ordinal);

    private static void ScanFolder(string folder, List<ModInfo> mods, bool disabled, bool isRoot)
    {
        var name = Path.GetFileName(folder);
        if (!isRoot && IsIgnoredFolder(name))
            return;

        var isDisabled = disabled || (!isRoot && name.StartsWith('.'));

        var manifest = isRoot ? null : ManifestReader.FindManifest(folder);
        if (manifest is not null)
        {
            var info = ManifestReader.ReadFile(manifest, folder, isDisabled);
            if (info is not null)
                mods.Add(info);
            return;
        }

        IEnumerable<string> subfolders;
        try
        {
            subfolders = Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var sub in subfolders)
            ScanFolder(sub, mods, isDisabled, isRoot: false);
    }
}
