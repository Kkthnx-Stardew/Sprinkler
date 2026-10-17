using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sprinkler.Core;

using static Sprinkler.Core.Cleanup;

internal sealed class AppUpdateException(string message) : Exception(message);

/// <summary>A newer release of this program, with the files the updater needs. Anything missing means "open the page instead".</summary>
internal sealed record AppRelease(string Version, string PageUrl, string? AssetName, string? AssetUrl, long AssetSize, string? SumsUrl, string? SigUrl)
{
    public bool CanDownload => AssetName is not null && AssetUrl is not null && SumsUrl is not null && SigUrl is not null && AssetSize > 0;
}

/// <summary>
/// Updating the program itself. A release carries the program in an archive, a SHA-256 list for it, and a signature of that
/// list made with a private key that never leaves the maintainer. The matching public key is built into the program, so a
/// swapped file on GitHub fails the signature check, and a damaged download fails the hash check. Only then is the new
/// program started once to confirm it reports the version it should, and put in place of the old one.
/// </summary>
internal static class SelfUpdate
{
    /// <summary>Public half of the release signing key (ECDSA P-256, base64 of the SubjectPublicKeyInfo).</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOQCPo4AjcV2Dcw7AS4lE5LgxKhKyitMIyOIdxApOl/b7viPrb/sRr20zeswpmIQfMJpLsT/GK7zU7AbTYFLsjg==";

    public const long MaxArchiveBytes = 150L * 1024 * 1024;
    public const long MaxProgramBytes = 200L * 1024 * 1024;

    public static byte[] DefaultKey => Convert.FromBase64String(PublicKey);

    /// <summary>The release file for this system, or null where no build is published (macOS, ARM).</summary>
    public static string? PlatformSuffix() =>
        RuntimeInformation.ProcessArchitecture != Architecture.X64 ? null
        : Platform.IsWindows ? "win-x64.zip"
        : Platform.IsLinux ? "linux-x64.tar.gz"
        : null;

    private static string ProgramName => Platform.IsWindows ? "Sprinkler.exe" : "Sprinkler";

    // ---------------------------------------------------------------- finding a release

    /// <summary>
    /// Picks the newest release that is newer than <paramref name="current"/> from GitHub's release list. Drafts are skipped, and
    /// pre-releases only count for someone already running a pre-release.
    /// </summary>
    public static AppRelease? Pick(JsonElement releases, ModVersion current, string? suffix = null)
    {
        if (releases.ValueKind != JsonValueKind.Array)
            return null;
        suffix ??= PlatformSuffix();
        AppRelease? best = null;
        ModVersion? bestVersion = null;
        foreach (var r in releases.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object || Flag(r, "draft"))
                continue;
            var tag = Text(r, "tag_name");
            var page = Text(r, "html_url");
            if (ModVersion.Parse(tag) is not { } version || page is null || !page.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                continue;
            if ((Flag(r, "prerelease") || version.IsPrerelease) && !current.IsPrerelease)
                continue;
            if (version <= current || (bestVersion is not null && version <= bestVersion))
                continue;
            bestVersion = version;
            best = Describe(r, tag!, version, page, suffix);
        }
        return best;
    }

    private static AppRelease Describe(JsonElement release, string tag, ModVersion version, string page, string? suffix)
    {
        var clean = version.ToString().TrimStart('v', 'V');
        var name = suffix is null ? null : $"Sprinkler-{tag}-{suffix}";
        string? archive = null, sums = null, sig = null;
        long size = 0;
        if (name is not null && release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var assetName = Text(a, "name");
                var url = Text(a, "browser_download_url");
                if (assetName is null || url is null || !DownloadUrlOk(url))
                    continue;
                if (assetName == name)
                {
                    archive = url;
                    size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                }
                else if (assetName == name + ".sha256")
                {
                    sums = url;
                }
                else if (assetName == name + ".sha256.sig")
                {
                    sig = url;
                }
            }
        }
        return new AppRelease(clean, page, archive is null ? null : name, archive, size, sums, sig);
    }

    /// <summary>Downloads are only taken from this project's own release files.</summary>
    public static bool DownloadUrlOk(string url) =>
        url.StartsWith($"{AppEnvironment.GitHubSiteUrl}/{AppEnvironment.RepoSlug}/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ---------------------------------------------------------------- can this copy replace itself

    /// <summary>The single-file program that is running now, if it is one this updater may replace. Null for a build run from source.</summary>
    public static string? ReplaceablePath(string? processPath = null)
    {
        var path = processPath ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        var comparison = Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFileName(path).Equals(ProgramName, comparison))
            return null;
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(path)!, ".sprinkler-write-test-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- download and check

    /// <summary>
    /// Downloads, checks and unpacks a release next to the running program and returns the path of the new program.
    /// Nothing is changed yet. Throws <see cref="AppUpdateException"/> with a sentence for the person if anything is wrong.
    /// </summary>
    public static async Task<string> FetchAsync(AppRelease release, byte[] publicKey, string currentPath, Action<int>? progress, CancellationToken ct)
    {
        if (!release.CanDownload)
            throw new AppUpdateException(Loc.T("upd.no_files"));
        if (release.AssetSize > MaxArchiveBytes)
            throw new AppUpdateException(Loc.T("upd.too_big"));

        var sums = await GetSmallAsync(release.SumsUrl!, 4096, ct);
        var signature = await GetSmallAsync(release.SigUrl!, 512, ct);
        VerifySignature(sums, signature, publicKey);
        var expected = HashFor(Encoding.UTF8.GetString(sums), release.AssetName!)
            ?? throw new AppUpdateException(Loc.T("upd.not_listed"));

        var work = Path.Combine(AppEnvironment.StagingDir, "app-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, release.AssetName!);
            await DownloadAsync(release.AssetUrl!, archive, release.AssetSize, expected, progress, ct);
            return Unpack(archive, currentPath);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private static async Task<byte[]> GetSmallAsync(string url, int limit, CancellationToken ct)
    {
        try
        {
            using var response = await AppEnvironment.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new AppUpdateException(Loc.T("upd.http", (int)response.StatusCode));
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[1024];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > limit)
                    throw new AppUpdateException(Loc.T("upd.bad_sums"));
            }
            return buffer.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new AppUpdateException(Loc.T("upd.network", ex.Message));
        }
    }

    public static void VerifySignature(byte[] data, byte[] signature, byte[] publicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (!key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new AppUpdateException(Loc.T("upd.bad_signature"));
        }
        catch (CryptographicException)
        {
            throw new AppUpdateException(Loc.T("upd.bad_signature"));
        }
    }

    /// <summary>Reads the hash for one file from a sha256sum style list ("hash, two spaces, name").</summary>
    public static string? HashFor(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success && m.Groups[2].Value.Trim() == fileName)
                return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    private static async Task DownloadAsync(string url, string path, long declared, string expectedHash, Action<int>? progress, CancellationToken ct)
    {
        try
        {
            using var response = await AppEnvironment.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new AppUpdateException(Loc.T("upd.http", (int)response.StatusCode));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(path))
            {
                var buffer = new byte[81920];
                long read = 0;
                var last = -1;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    read += n;
                    if (read > Math.Max(declared, 1) + 4096 || read > MaxArchiveBytes)
                        throw new AppUpdateException(Loc.T("upd.too_big"));
                    hash.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    var percent = (int)(read * 100 / Math.Max(declared, 1));
                    if (percent != last && percent <= 100)
                    {
                        last = percent;
                        progress?.Invoke(percent);
                    }
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != expectedHash)
                throw new AppUpdateException(Loc.T("upd.bad_hash"));
        }
        catch (HttpRequestException ex)
        {
            throw new AppUpdateException(Loc.T("upd.network", ex.Message));
        }
    }

    /// <summary>Takes the one program file out of the archive and writes it beside the running program.</summary>
    public static string Unpack(string archive, string currentPath)
    {
        var target = Path.Combine(Path.GetDirectoryName(currentPath)!, Platform.IsWindows ? "Sprinkler.new.exe" : "Sprinkler.new");
        TryDeleteFile(target);
        try
        {
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                UnpackZip(archive, target);
            else if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                UnpackTar(archive, target);
            else
                throw new AppUpdateException(Loc.T("upd.no_program"));

            if (!LooksLikeProgram(target))
                throw new AppUpdateException(Loc.T("upd.no_program"));
            return target;
        }
        catch
        {
            TryDeleteFile(target);
            throw;
        }
    }

    private static void UnpackZip(string archive, string target)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.Entries.FirstOrDefault(e => IsProgram(e.FullName))
            ?? throw new AppUpdateException(Loc.T("upd.no_program"));
        if (entry.Length > MaxProgramBytes)
            throw new AppUpdateException(Loc.T("upd.too_big"));
        using var input = entry.Open();
        CopyLimited(input, target);
    }

    private static void UnpackTar(string archive, string target)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is not null && IsProgram(entry.Name))
            {
                if (entry.Length > MaxProgramBytes)
                    throw new AppUpdateException(Loc.T("upd.too_big"));
                CopyLimited(entry.DataStream, target);
                return;
            }
            // a stream that cannot seek only moves on correctly if what was not wanted is read through first
            entry.DataStream?.CopyTo(Stream.Null);
        }
        throw new AppUpdateException(Loc.T("upd.no_program"));
    }

    /// <summary>Only the program at the top of the archive, or one folder down, counts. Paths with ".." or a drive never do.</summary>
    private static bool IsProgram(string entryName)
    {
        var name = entryName.Replace('\\', '/').TrimStart('/');
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains(':'))
            return false;
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is 1 or 2 && parts[^1] == ProgramName;
    }

    private static void CopyLimited(Stream input, string target)
    {
        using var output = File.Create(target);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > MaxProgramBytes)
                throw new AppUpdateException(Loc.T("upd.too_big"));
            output.Write(buffer, 0, n);
        }
    }

    private static bool LooksLikeProgram(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1024 * 1024)
            return false;
        var head = new byte[4];
        using var stream = File.OpenRead(path);
        if (stream.Read(head, 0, 4) < 4)
            return false;
        return Platform.IsWindows ? head[0] == 'M' && head[1] == 'Z' : head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F';
    }

    /// <summary>Starts the new program once with --version, and accepts it only if it prints the version it should.</summary>
    public static async Task<bool> ReportsVersionAsync(string path, string version)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var info = new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(info);
            if (process is null)
                return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 && output.Trim() == $"{AppEnvironment.AppName} {version}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- put it in place

    /// <summary>
    /// Replaces the running program with the new one. Windows will not overwrite a running program but allows renaming it,
    /// so the old one is kept as .old (removed at the next start) and put back if the move fails. Linux renames over it.
    /// </summary>
    public static void Swap(string stagedPath, string currentPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var old = currentPath + ".old";
                TryDeleteFile(old);
                File.Move(currentPath, old);
                try
                {
                    File.Move(stagedPath, currentPath);
                }
                catch
                {
                    File.Move(old, currentPath, overwrite: true);
                    throw;
                }
            }
            else
            {
                File.SetUnixFileMode(stagedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(stagedPath, currentPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(stagedPath);
            throw new AppUpdateException(Loc.T("upd.swap_failed", ex.Message));
        }
    }

    /// <summary>Starts the program at <paramref name="path"/> and tells it to wait for this process to end first.</summary>
    public static bool Relaunch(string path)
    {
        try
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = false };
            info.ArgumentList.Add("--wait-for");
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            return Process.Start(info) is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Write("Could not restart after the update: " + ex.Message);
            return false;
        }
    }

    /// <summary>On start: removes the old program kept by the last update, and any half-finished download beside the program.</summary>
    public static void RemoveLeftovers(string? processPath = null)
    {
        try
        {
            var path = processPath ?? Environment.ProcessPath;
            var dir = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            if (dir is null)
                return;
            TryDeleteFile(path + ".old");
            TryDeleteFile(Path.Combine(dir, "Sprinkler.new.exe"));
            TryDeleteFile(Path.Combine(dir, "Sprinkler.new"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>For a restart: waits for the program that started this one to end, so the single-instance check does not mistake it for a second copy.</summary>
    public static void WaitForExit(int processId)
    {
        try
        {
            using var other = Process.GetProcessById(processId);
            other.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    /// <summary>Removes a file the updater staged and will not use.</summary>
    public static void Discard(string path) => TryDeleteFile(path);
}
