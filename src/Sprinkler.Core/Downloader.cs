namespace Sprinkler.Core;

using static Sprinkler.Core.Cleanup;

/// <summary>A download that did not work. <paramref name="transient"/> marks a failure worth another try, such as a dropped connection.</summary>
internal sealed class DownloadException(string message, bool transient = false) : Exception(message)
{
    public bool Transient { get; } = transient;
}

internal static class Downloader
{
    private static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };

    private const int Attempts = 3;

    /// <summary>A connection that sends nothing for this long is treated as dropped.</summary>
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    internal static TimeSpan RetryPause { get; set; } = TimeSpan.FromSeconds(2);

    public static Task<string> FetchAsync(string url, string? suggestedName, Action<int>? progress, CancellationToken ct) =>
        FetchAsync(new[] { url }, suggestedName, progress, ct);

    /// <summary>
    /// Downloads a file into a fresh staging folder and returns its path. A dropped or stalled connection is tried again,
    /// and when a link keeps failing the next one (a different mirror of the same file) is used.
    /// </summary>
    public static async Task<string> FetchAsync(IReadOnlyList<string> urls, string? suggestedName, Action<int>? progress, CancellationToken ct)
    {
        DownloadException? last = null;
        foreach (var url in urls)
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    return await FetchOnceAsync(url, suggestedName, progress, ct);
                }
                catch (DownloadException ex)
                {
                    last = ex;
                    Log.Write($"Download from {HostOf(url)} failed (try {attempt} of {Attempts}): {ex.Message}");
                    if (!ex.Transient || attempt == Attempts)
                        break;
                    await Task.Delay(RetryPause * attempt, ct);
                }
            }
        }
        throw last ?? new DownloadException(Loc.T("core.no_link"));
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "the server";

    private static async Task<string> FetchOnceAsync(string url, string? suggestedName, Action<int>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(AppEnvironment.StagingDir, "dl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            // a server that accepts the connection and then says nothing must not hold the download for the client's own ten minutes
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waiting.CancelAfter(StallTimeout);
            HttpResponseMessage response;
            try
            {
                response = await AppEnvironment.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, waiting.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DownloadException(Loc.T("core.dl_stalled"), transient: true);
            }
            using var owned = response;
            waiting.CancelAfter(Timeout.InfiniteTimeSpan);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                throw new DownloadException(Loc.T("core.dl_status", code), transient: code is 408 or 425 or 429 or >= 500);
            }

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                throw new DownloadException(Loc.T("core.dl_html"));

            var name = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? suggestedName
                ?? Path.GetFileName(response.RequestMessage?.RequestUri?.LocalPath ?? "");
            name = CleanName(name);
            if (!ArchiveExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
                name += ".zip";

            var total = response.Content.Headers.ContentLength;
            if (total > ArchiveFile.MaxTotalBytes)
                throw new DownloadException(Loc.T("core.dl_big"));

            var path = Path.Combine(dir, name);
            long read = 0;
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(path);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var buffer = new byte[81920];
                var lastPercent = -1;
                while (true)
                {
                    idle.CancelAfter(StallTimeout);
                    var n = await input.ReadAsync(buffer, idle.Token);
                    if (n <= 0)
                        break;
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (read > ArchiveFile.MaxTotalBytes)
                        throw new DownloadException(Loc.T("core.dl_big_stopped"));
                    if (total is > 0 && progress is not null)
                    {
                        var percent = (int)Math.Min(100, read * 100 / total.Value);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress(percent);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DownloadException(Loc.T("core.dl_stalled"), transient: true);
            }

            // a connection that closes early still ends the stream normally, so the size the server promised is checked
            if (total is > 0 && read < total && response.Content.Headers.ContentEncoding.Count == 0)
                throw new DownloadException(Loc.T("core.dl_incomplete", read / (1024 * 1024), total.Value / (1024 * 1024)), transient: true);
            if (read == 0)
                throw new DownloadException(Loc.T("core.dl_empty"), transient: true);

            path = FixExtension(path);
            Log.Write($"Downloaded {Path.GetFileName(path)} ({read / 1024} KB) from {HostOf(url)}.");
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDeleteDirectory(dir);
            throw new DownloadException(Loc.T("core.dl_failed", ex.Message), transient: true);
        }
        catch
        {
            TryDeleteDirectory(dir);
            throw;
        }
    }

    /// <summary>Names the file after what it really is, and refuses a web page that was sent as if it were a file.</summary>
    private static string FixExtension(string path)
    {
        var kind = ArchiveFile.Sniff(path);
        if (kind == ArchiveKind.Unknown)
        {
            if (LooksLikeWebPage(path))
                throw new DownloadException(Loc.T("core.dl_html"));
            return path;
        }

        var wanted = kind switch { ArchiveKind.SevenZip => ".7z", ArchiveKind.Rar => ".rar", _ => ".zip" };
        if (Path.GetExtension(path).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            return path;
        var fixedPath = Path.ChangeExtension(path, wanted);
        File.Move(path, fixedPath, overwrite: true);
        return fixedPath;
    }

    private static bool LooksLikeWebPage(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[512];
        var n = f.Read(head, 0, head.Length);
        var text = System.Text.Encoding.UTF8.GetString(head, 0, n).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    public static void Cleanup(string archivePath)
    {
        var dir = Path.GetDirectoryName(archivePath);
        if (dir is not null && Path.GetFileName(dir).StartsWith("dl-"))
            TryDeleteDirectory(dir);
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
        {
            Log.Write("The downloads folder does not exist, so nothing is watched: " + folder);
            return;
        }

        Folder = folder;
        _watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, e) => Consider(e.FullPath);
        _watcher.Renamed += (_, e) => Consider(e.FullPath);
        _watcher.Error += (_, e) =>
        {
            // too many changes at once overflow the buffer and events are lost: look at the folder instead
            Log.Write("The downloads watcher lost events (" + e.GetException().Message + "), so the folder is read again.");
            ScanRecent(TimeSpan.FromMinutes(15));
        };
        Log.Write("Watching for downloads in " + folder);
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        Folder = null;
    }

    /// <summary>
    /// Looks at the archives already in the folder that were written recently. A watcher can miss a file (a full event buffer, a
    /// network or synced folder), so this is run now and then while an update waits for a download.
    /// </summary>
    public void ScanRecent(TimeSpan maxAge)
    {
        var folder = Folder;
        if (folder is null)
            return;
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(folder).Where(Downloader.IsArchiveName).ToList())
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff && info.CreationTimeUtc < cutoff)
                    continue;
                lock (_seen)
                {
                    if (_seen.Contains($"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"))
                        continue;
                }
                Consider(file, ignoreStartGate: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not read the downloads folder: " + ex.Message);
        }
    }

    private void Consider(string path, bool ignoreStartGate = false)
    {
        if (!Downloader.IsArchiveName(path))
            return;
        _ = Task.Run(async () =>
        {
            if (!await WaitUntilStable(path))
            {
                Log.Write($"Gave up on {Path.GetFileName(path)}: it kept changing or was removed.");
                return;
            }
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (info.Length > MaxWatchedBytes)
                    return;
            }
            catch (IOException)
            {
                return; // the browser removed or renamed it again
            }
            if (!ignoreStartGate && info.CreationTimeUtc < _startedAt && info.LastWriteTimeUtc < _startedAt)
                return;
            var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (_seen)
            {
                if (!_seen.Add(key))
                    return;
            }
            Log.Write($"Saw the download {Path.GetFileName(path)} ({info.Length / 1024} KB).");
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

    /// <summary>
    /// Waits until the file stops growing and can be opened. A big download may take a long while, so the wait only gives up
    /// after two quiet minutes (or an hour in all).
    /// </summary>
    private static async Task<bool> WaitUntilStable(string path)
    {
        var started = DateTime.UtcNow;
        var changed = started;
        long last = -1;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(60) && DateTime.UtcNow - changed < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(1000);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return false;
                if (info.Length != last)
                {
                    last = info.Length;
                    changed = DateTime.UtcNow;
                    continue;
                }
                if (info.Length > 0)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return true;
                }
            }
            catch (IOException)
            {
                // still being written or held by the browser
            }
        }
        return false;
    }

    public void Dispose() => Stop();
}
