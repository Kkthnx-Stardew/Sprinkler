using System.IO.Compression;
using SharpCompress.Archives;

namespace Sprinkler.Core;

internal sealed class ArchiveException(string message) : Exception(message);

internal sealed record ArchiveEntryInfo(string Path, long Size);

/// <summary>A zip, 7z or rar file opened read-only, with every entry path checked before anything is written to disk.</summary>
internal sealed class ArchiveFile : IDisposable
{
    public const int MaxEntries = 60_000;
    public const long MaxTotalBytes = 6L * 1024 * 1024 * 1024;

    private static readonly string[] JunkNames = { ".DS_Store", "Thumbs.db", "desktop.ini" };

    private readonly ZipArchive? _zip;
    private readonly IArchive? _other;
    private readonly Dictionary<string, Func<Stream>> _openers = new(StringComparer.OrdinalIgnoreCase);

    public List<ArchiveEntryInfo> Entries { get; } = new();

    public long TotalBytes { get; }

    private ArchiveFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".zip" || LooksLikeZip(path))
            {
                _zip = ZipFile.OpenRead(path);
                foreach (var e in _zip.Entries)
                {
                    var name = Normalize(e.FullName);
                    if (name is null || name.EndsWith('/') || IsJunk(name))
                        continue;
                    Add(name, e.Length, () => e.Open());
                }
            }
            else
            {
                _other = ArchiveFactory.Open(path);
                foreach (var e in _other.Entries)
                {
                    if (e.IsDirectory || e.Key is null)
                        continue;
                    var name = Normalize(e.Key);
                    if (name is null || name.EndsWith('/') || IsJunk(name))
                        continue;
                    Add(name, e.Size, () => e.OpenEntryStream());
                }
            }
        }
        catch (ArchiveException)
        {
            Dispose();
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the readers throw many kinds of exception for a damaged or mislabelled file (a text file named .7z, say)
            Dispose();
            throw new ArchiveException(Loc.T("core.archive_unreadable", ex.Message));
        }

        if (Entries.Count > MaxEntries)
        {
            Dispose();
            throw new ArchiveException(Loc.T("core.archive_too_many", Entries.Count));
        }
        TotalBytes = Entries.Sum(e => e.Size);
        if (TotalBytes > MaxTotalBytes)
        {
            Dispose();
            throw new ArchiveException(Loc.T("core.archive_too_big"));
        }
    }

    public static ArchiveFile Open(string path) => new(path);

    public Stream OpenEntry(string path) =>
        _openers.TryGetValue(path, out var open) ? open() : throw new FileNotFoundException(path);

    public byte[] ReadAll(string path, long maxBytes = long.MaxValue)
    {
        using var s = OpenEntry(path);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            // the size in an archive header can be false, so count what is really read
            if (ms.Length + n > maxBytes)
                throw new ArchiveException(Loc.T("core.archive_lies"));
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        _zip?.Dispose();
        _other?.Dispose();
    }

    private void Add(string name, long size, Func<Stream> open)
    {
        if (_openers.ContainsKey(name))
            throw new ArchiveException(Loc.T("core.archive_duplicate", name));
        _openers[name] = open;
        Entries.Add(new ArchiveEntryInfo(name, size));
    }

    /// <summary>Returns a clean forward-slash path, or throws for paths that could escape the target folder.</summary>
    internal static string? Normalize(string raw)
    {
        var p = raw.Replace('\\', '/');
        while (p.StartsWith("./"))
            p = p[2..];
        if (p.Length == 0)
            return null;
        if (p.StartsWith('/') || (p.Length > 1 && p[1] == ':'))
            throw new ArchiveException(Loc.T("core.archive_absolute", raw));

        var segments = p.Split('/');
        foreach (var seg in segments)
        {
            if (seg == "..")
                throw new ArchiveException(Loc.T("core.archive_outside", raw));
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 && seg.Length > 0)
                throw new ArchiveException(Loc.T("core.archive_bad_name", raw));
        }
        if (segments[0].Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
            return null;
        return p;
    }

    private static bool IsJunk(string path) =>
        JunkNames.Contains(path.Split('/').Last(), StringComparer.OrdinalIgnoreCase);

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[4];
            return f.Read(b, 0, 4) == 4 && b[0] == 0x50 && b[1] == 0x4B;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

/// <summary>A mod folder inside an archive, found by its manifest.</summary>
internal sealed record ArchiveMod(string RootPath, ManifestSummary Manifest);

internal static class ArchiveInspector
{
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>Finds the outermost manifests, the same mod roots SMAPI would load.</summary>
    public static List<ArchiveMod> FindMods(ArchiveFile archive)
    {
        var roots = new List<string>();
        foreach (var e in archive.Entries)
        {
            if (!e.Path.Split('/').Last().Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;
            var slash = e.Path.LastIndexOf('/');
            roots.Add(slash < 0 ? "" : e.Path[..slash]);
        }

        var outer = roots
            .Where(r => !roots.Any(o => o != r && (o.Length == 0 || r.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var mods = new List<ArchiveMod>();
        foreach (var root in outer)
        {
            var entry = archive.Entries.First(e =>
                e.Path.Equals(root.Length == 0 ? "manifest.json" : root + "/manifest.json", StringComparison.OrdinalIgnoreCase)
                || (root.Length > 0 && e.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                    && e.Path[(root.Length + 1)..].Equals("manifest.json", StringComparison.OrdinalIgnoreCase)));
            if (entry.Size > MaxManifestBytes)
                continue; // a real manifest is a few kilobytes, and one this big would only fill memory
            var summary = ManifestReader.Summarize(archive.ReadAll(entry.Path, MaxManifestBytes));
            if (summary is not null)
                mods.Add(new ArchiveMod(root, summary));
        }
        return mods;
    }
}
