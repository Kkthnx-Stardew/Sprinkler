namespace Sprinkler.Core;

internal sealed class DownloadException(string message) : Exception(message);

internal static class Downloader
{
    private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };

    /// <summary>Downloads a file into a fresh staging folder and returns its path.</summary>
    public static async Task<string> FetchAsync(string url, string? suggestedName, Action<int>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(AppEnvironment.StagingDir, "dl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            using var response = await AppEnvironment.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new DownloadException(Loc.T("core.dl_status", (int)response.StatusCode));

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                throw new DownloadException(Loc.T("core.dl_html"));

            var name = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? suggestedName
                ?? Path.GetFileName(response.RequestMessage?.RequestUri?.LocalPath ?? "") ;
            name = CleanName(name);
            if (!ArchiveExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
                name += ".zip";

            var total = response.Content.Headers.ContentLength;
            if (total > ArchiveFile.MaxTotalBytes)
                throw new DownloadException(Loc.T("core.dl_big"));

            var path = Path.Combine(dir, name);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(path))
            {
                var buffer = new byte[81920];
                long read = 0;
                int lastPercent = -1;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (read > ArchiveFile.MaxTotalBytes)
                        throw new DownloadException(Loc.T("core.dl_big_stopped"));
                    if (total is > 0 && progress is not null)
                    {
                        var percent = (int)(read * 100 / total.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress(percent);
                        }
                    }
                }
            }
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDelete(dir);
            throw new DownloadException(Loc.T("core.dl_failed", ex.Message));
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    public static void Cleanup(string archivePath)
    {
        var dir = Path.GetDirectoryName(archivePath);
        if (dir is not null && Path.GetFileName(dir).StartsWith("dl-"))
            TryDelete(dir);
    }

    public static bool IsArchiveName(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string CleanName(string? name)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "download" : Path.GetFileName(Uri.UnescapeDataString(name));
        foreach (var c in Path.GetInvalidFileNameChars())
            n = n.Replace(c, '_');
        return n.Length == 0 ? "download" : n;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Watches the browser's download folder and reports finished archives.</summary>
internal sealed class DownloadWatcher : IDisposable
{
    private readonly Func<string, Task> _onArchive;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _startedAt = DateTime.UtcNow.AddSeconds(-30);
    private const long MaxWatchedBytes = 3L * 1024 * 1024 * 1024;
    private FileSystemWatcher? _watcher;

    public string? Folder { get; private set; }

    public DownloadWatcher(Func<string, Task> onArchive) => _onArchive = onArchive;

    public void Start(string folder)
    {
        Stop();
        if (!Directory.Exists(folder))
            return;

        Folder = folder;
        _watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, e) => Consider(e.FullPath);
        _watcher.Renamed += (_, e) => Consider(e.FullPath);
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        Folder = null;
    }

    private void Consider(string path)
    {
        if (!Downloader.IsArchiveName(path))
            return;
        _ = Task.Run(async () =>
        {
            if (!await WaitUntilStable(path))
                return;
            var info = new FileInfo(path);
            if (info.Length > MaxWatchedBytes)
                return;
            if (info.CreationTimeUtc < _startedAt && info.LastWriteTimeUtc < _startedAt)
                return;
            var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (_seen)
            {
                if (!_seen.Add(key))
                    return;
            }
            try
            {
                await _onArchive(path);
            }
            catch (Exception ex)
            {
                Log.Write("Watcher handler failed for " + path + ": " + ex.Message);
            }
        });
    }

    private static async Task<bool> WaitUntilStable(string path)
    {
        long last = -1;
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return false;
                if (info.Length == last && info.Length > 0)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return true;
                }
                last = info.Length;
            }
            catch (IOException)
            {
                last = -1;
            }
        }
        return false;
    }

    public void Dispose() => Stop();
}
