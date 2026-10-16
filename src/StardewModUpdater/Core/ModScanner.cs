namespace StardewModUpdater.Core;

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

    private static void ScanFolder(string folder, List<ModInfo> mods, bool disabled, bool isRoot)
    {
        var name = Path.GetFileName(folder);
        if (!isRoot && name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
            return;

        var isDisabled = disabled || (!isRoot && name.StartsWith('.'));

        var manifest = Path.Combine(folder, "manifest.json");
        if (!isRoot && File.Exists(manifest))
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
