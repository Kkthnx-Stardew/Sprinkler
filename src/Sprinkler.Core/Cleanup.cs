namespace Sprinkler.Core;

/// <summary>Deletes leftovers that are not worth failing over: a locked or already removed file just stays until next time.</summary>
internal static class Cleanup
{
    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes everything in a scratch folder that has not been touched for <paramref name="age"/>. Files copied or unpacked for a moment
    /// (a dropped archive that was never installed, a download nobody asked for) would otherwise stay for good.
    /// </summary>
    public static int PurgeOlderThan(string folder, TimeSpan age)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(folder))
                return 0;
            var cutoff = DateTime.UtcNow - age;
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
            {
                var info = new FileInfo(entry);
                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                // a copied file keeps the time of the original, so the time it arrived here counts too
                var touched = new[] { info.CreationTimeUtc, info.LastWriteTimeUtc }.Max();
                if (touched > cutoff)
                    continue;
                if (isDirectory)
                    TryDeleteDirectory(entry);
                else
                    TryDeleteFile(entry);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return removed;
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
