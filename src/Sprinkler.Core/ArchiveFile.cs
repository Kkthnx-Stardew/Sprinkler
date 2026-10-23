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

    /// <summary>Starts the folder name of an archive that was found inside another one, so its mods get a name of their own.</summary>
    public const string NestedMarker = "@inner";

    private const int MaxNested = 8;

    private ZipArchive? _zip;
    private IArchive? _other;
    private readonly Dictionary<string, Func<Stream>> _openers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ArchiveFile> _inner = new();
    private string? _innerDir;

    public List<ArchiveEntryInfo> Entries { get; } = new();

    public long TotalBytes { get; }

    private ArchiveFile(string path, int depth)
    {
        try
        {
            LoadEntries(path);
            if (depth == 0)
                UnwrapNested();
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

    /// <summary>
    /// The extension is only a hint: a download is often named .zip while it is a 7z, or the other way round. The first bytes decide,
    /// and a file that fails as a zip is tried with the other readers before it is called damaged.
    /// </summary>
    private void LoadEntries(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var zipMagic = LooksLikeZip(path);
        if (zipMagic || ext == ".zip")
        {
            try
            {
                LoadZip(path);
                return;
            }
            catch (InvalidDataException) when (!zipMagic)
            {
                _zip?.Dispose();
                _zip = null;
                _openers.Clear();
                Entries.Clear();
            }
        }
        LoadOther(path);
    }

    private void LoadZip(string path)
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

    private void LoadOther(string path)
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

    /// <summary>
    /// Some authors zip their zip. When the archive holds no manifest but a few archives, the ones that do hold a mod are opened
    /// and their files show up here under a folder of their own, so the rest of the app never needs to know.
    /// </summary>
    private void UnwrapNested()
    {
        if (Entries.Any(e => IsManifestPath(e.Path)))
            return;
        var nested = Entries.Where(e => IsArchivePath(e.Path) && e.Size > 0 && e.Size <= MaxTotalBytes).ToList();
        if (nested.Count == 0 || nested.Count > MaxNested)
            return;

        var written = 0L;
        for (var i = 0; i < nested.Count; i++)
        {
            var outer = nested[i];
            var temp = Path.Combine(_innerDir ??= Path.Combine(AppEnvironment.StagingDir, "nested-" + Guid.NewGuid().ToString("N")[..8]),
                i + Path.GetExtension(outer.Path).ToLowerInvariant());
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            try
            {
                using (var input = OpenEntry(outer.Path))
                using (var output = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += n;
                        if (written > MaxTotalBytes)
                            throw new ArchiveException(Loc.T("core.archive_lies"));
                        output.Write(buffer, 0, n);
                    }
                }

                var inner = new ArchiveFile(temp, depth: 1);
                if (!inner.Entries.Any(e => IsManifestPath(e.Path)))
                {
                    inner.Dispose();
                    continue;
                }
                _inner.Add(inner);
                foreach (var e in inner.Entries)
                {
                    var innerPath = e.Path;
                    Add($"{NestedMarker}{i}/{innerPath}", e.Size, () => inner.OpenEntry(innerPath));
                }
                Entries.Remove(outer);
                Log.Write($"Opened {outer.Path} inside the archive and found {inner.Entries.Count(e => IsManifestPath(e.Path))} mod(s) in it.");
            }
            catch (ArchiveException ex)
            {
                Log.Write($"Skipped {outer.Path} inside the archive: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Write($"Skipped {outer.Path} inside the archive: {ex.Message}");
            }
        }
    }

    private static bool IsManifestPath(string path) =>
        path.Split('/').Last().Equals("manifest.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsArchivePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".7z" or ".rar";

    public static ArchiveFile Open(string path) => new(path, depth: 0);

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
        foreach (var inner in _inner)
            inner.Dispose();
        _zip?.Dispose();
        _other?.Dispose();
        if (_innerDir is not null)
            Cleanup.TryDeleteDirectory(_innerDir);
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

    private static bool LooksLikeZip(string path) => Sniff(path) == ArchiveKind.Zip;

    /// <summary>What a file really is, judged by its first bytes and not by its name.</summary>
    internal static ArchiveKind Sniff(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[6];
            var n = f.Read(b, 0, b.Length);
            if (n >= 2 && b[0] == 0x50 && b[1] == 0x4B)
                return ArchiveKind.Zip;
            if (n >= 6 && b[0] == 0x37 && b[1] == 0x7A && b[2] == 0xBC && b[3] == 0xAF && b[4] == 0x27 && b[5] == 0x1C)
                return ArchiveKind.SevenZip;
            if (n >= 4 && b[0] == 0x52 && b[1] == 0x61 && b[2] == 0x72 && b[3] == 0x21)
                return ArchiveKind.Rar;
            return ArchiveKind.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ArchiveKind.Unknown;
        }
    }
}

internal enum ArchiveKind
{
    Unknown,
    Zip,
    SevenZip,
    Rar,
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
