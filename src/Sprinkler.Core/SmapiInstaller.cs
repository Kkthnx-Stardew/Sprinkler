using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sprinkler.Core;

/// <summary>A SMAPI release that can be installed. The fingerprint comes from GitHub's own record of the file.</summary>
internal sealed record SmapiRelease(string Version, string PageUrl, string AssetName, string AssetUrl, long AssetSize, string Sha256);

internal sealed class SmapiInstallException(string message) : Exception(message);

/// <summary>
/// Installs or updates SMAPI by running SMAPI's own installer in its scripting mode on the game folder. The installer comes from SMAPI's
/// official GitHub release, is checked against the fingerprint GitHub records for the file, and is unpacked without trusting a path.
/// Nothing but SMAPI's own files change: the installer leaves the Mods folder and the saves alone.
/// </summary>
internal static partial class SmapiInstaller
{
    public const string Repo = "Pathoschild/SMAPI";

    private const long MaxDownloadBytes = 200L * 1024 * 1024;
    private const long MaxUnpackedBytes = 400L * 1024 * 1024;
    private const int MaxEntries = 4000;

    /// <summary>How long the installer may run. It copies a few files, so a run past this is stuck, for example on a locked file.</summary>
    internal static TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>For tests: runs the installer program with these arguments and says how it ended, in place of starting it.</summary>
    internal static Func<string, IReadOnlyList<string>, string, Task<(int ExitCode, string Output)>>? RunnerOverride { get; set; }

    /// <summary>SMAPI ships installers for Windows, Linux and macOS, but only the first two are tested here.</summary>
    public static bool IsSupported => Platform.IsWindows || Platform.IsLinux;

    private static string PlatformFolder => Platform.IsWindows ? "windows" : "linux";

    private static string InstallerFile => Platform.IsWindows ? "SMAPI.Installer.exe" : "SMAPI.Installer";

    [GeneratedRegex(@"^SMAPI-(?<v>\d+\.\d+\.\d+)-installer\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerName();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Text();

    // ---------------------------------------------------------------- finding the release

    /// <summary>Looks up the newest stable SMAPI on GitHub and the installer file in it.</summary>
    public static async Task<SmapiRelease> FindLatestAsync(CancellationToken ct)
    {
        Release? release;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AppEnvironment.GitHubApiUrl}/repos/{Repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await AppEnvironment.Http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new SmapiInstallException(Loc.T("smapi.lookup_failed", (int)response.StatusCode));
            release = await response.Content.ReadFromJsonAsync<Release>(AppEnvironment.Json, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new SmapiInstallException(Loc.T("smapi.lookup_failed", ex.Message));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SmapiInstallException(Loc.T("smapi.lookup_failed", Loc.T("core.dl_stalled")));
        }
        return Pick(release);
    }

    internal static SmapiRelease Pick(Release? release)
    {
        if (release is null || release.Draft || release.Prerelease)
            throw new SmapiInstallException(Loc.T("smapi.no_release"));

        foreach (var asset in release.Assets ?? new())
        {
            if (asset.Name is null || asset.BrowserDownloadUrl is null)
                continue;
            var match = InstallerName().Match(asset.Name);
            if (!match.Success)
                continue;

            // the link must be on SMAPI's own release path, whatever the API said
            var allowed = $"{AppEnvironment.GitHubSiteUrl}/{Repo}/releases/download/";
            if (!asset.BrowserDownloadUrl.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new SmapiInstallException(Loc.T("smapi.no_release"));

            var digest = asset.Digest ?? "";
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Sha256Text().IsMatch(digest[prefix.Length..]))
                throw new SmapiInstallException(Loc.T("smapi.no_release"));
            if (asset.Size is <= 0 or > MaxDownloadBytes)
                throw new SmapiInstallException(Loc.T("smapi.no_release"));

            return new SmapiRelease(match.Groups["v"].Value, release.HtmlUrl ?? $"{AppEnvironment.GitHubSiteUrl}/{Repo}/releases", asset.Name,
                asset.BrowserDownloadUrl, asset.Size, digest[prefix.Length..].ToLowerInvariant());
        }
        throw new SmapiInstallException(Loc.T("smapi.no_release"));
    }

    // ---------------------------------------------------------------- installing

    /// <summary>
    /// Downloads, checks and runs the installer. Returns the SMAPI version found in the game folder afterwards. Throws
    /// <see cref="SmapiInstallException"/> with a sentence for the person when anything goes wrong.
    /// </summary>
    public static async Task<string> InstallAsync(GameInstall game, SmapiRelease release, Action<int>? progress, CancellationToken ct)
    {
        if (!IsSupported)
            throw new SmapiInstallException(Loc.T("smapi.no_release"));
        if (GameLocator.IsGameRunning(fresh: true))
            throw new SmapiInstallException(Loc.T("core.close_game_files"));
        CheckWritable(game.GamePath);

        string? archive = null;
        var work = Path.Combine(AppEnvironment.StagingDir, "smapi-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            try
            {
                archive = await Downloader.FetchAsync(release.AssetUrl, release.AssetName, p => progress?.Invoke(p * 80 / 100), ct);
            }
            catch (DownloadException ex)
            {
                throw new SmapiInstallException(ex.Message);
            }

            Verify(archive, release);
            progress?.Invoke(85);
            Directory.CreateDirectory(work);
            Unpack(archive, work);
            progress?.Invoke(90);

            var program = Path.Combine(work, InstallerFile);
            if (!File.Exists(program))
                throw new SmapiInstallException(Loc.T("smapi.no_release"));

            var arguments = new[] { "--install", "--no-prompt", "--game-path", game.GamePath };
            var (code, output) = await RunAsync(program, arguments, work, ct);
            Log.Write($"The SMAPI {release.Version} installer ended with code {code}. {Tail(output, 1500)}");
            if (code != 0)
                throw new SmapiInstallException(Loc.T("smapi.failed", Tail(output, 300)));

            var found = GameLocator.Inspect(game.GamePath, game.Store)?.SmapiVersion;
            if (ModVersion.Parse(found) != ModVersion.Parse(release.Version))
                throw new SmapiInstallException(Loc.T("smapi.failed", $"SMAPI {found ?? "none"} is in the game folder, not {release.Version}"));
            progress?.Invoke(100);
            return found!;
        }
        finally
        {
            Cleanup.TryDeleteDirectory(work);
            if (archive is not null)
                Downloader.Cleanup(archive);
        }
    }

    private static void CheckWritable(string folder)
    {
        var probe = Path.Combine(folder, ".sprinkler-write-test-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SmapiInstallException(Loc.T("smapi.not_writable", folder));
        }
    }

    /// <summary>Compares the file with the fingerprint GitHub records for it.</summary>
    internal static void Verify(string path, SmapiRelease release)
    {
        string actual;
        using (var stream = File.OpenRead(path))
        {
            if (stream.Length > MaxDownloadBytes)
                throw new SmapiInstallException(Loc.T("smapi.no_release"));
            actual = Convert.ToHexString(SHA256.HashData(stream));
        }
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SmapiInstallException(Loc.T("smapi.untrusted"));
    }

    /// <summary>
    /// Takes the installer for this system out of the zip: only the files under <c>internal/windows</c> or <c>internal/linux</c>, with no
    /// path allowed to leave the work folder, and limits on the size and the number of files.
    /// </summary>
    internal static void Unpack(string archivePath, string work)
    {
        var root = Path.GetFullPath(work) + Path.DirectorySeparatorChar;
        var marker = "/internal/" + PlatformFolder + "/";
        long total = 0;
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.Entries.Count > MaxEntries)
                throw new SmapiInstallException(Loc.T("smapi.untrusted"));
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var at = ("/" + name).IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0 || name.EndsWith('/'))
                    continue;
                // the part after internal/<system>/ is where the file goes, and it may not climb out of the work folder
                var relative = ("/" + name)[(at + marker.Length)..];
                if (relative.Length == 0 || relative.Split('/').Any(part => part is ".." or "." or ""))
                    throw new SmapiInstallException(Loc.T("smapi.untrusted"));

                total += entry.Length;
                if (total > MaxUnpackedBytes)
                    throw new SmapiInstallException(Loc.T("smapi.untrusted"));

                var target = Path.GetFullPath(Path.Combine(work, relative));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw new SmapiInstallException(Loc.T("smapi.untrusted"));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);

                // a zip made on Linux keeps the executable bit, and the installer program needs it
                var mode = entry.ExternalAttributes >> 16 & 0x1FF;
                if (!OperatingSystem.IsWindows() && mode != 0)
                    File.SetUnixFileMode(target, (UnixFileMode)mode);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new SmapiInstallException(Loc.T("smapi.untrusted"));
        }

        if (!OperatingSystem.IsWindows())
        {
            var program = Path.Combine(work, InstallerFile);
            if (File.Exists(program))
                File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, string work, CancellationToken ct)
    {
        if (RunnerOverride is { } runner)
            return await runner(program, arguments, work);

        var info = new ProcessStartInfo(program)
        {
            WorkingDirectory = work,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new SmapiInstallException(Loc.T("smapi.failed", "?"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SmapiInstallException(Loc.T("smapi.failed", ex.Message));
        }

        using (process)
        {
            // nothing should ask a question in this mode, but an installer waiting for Enter must not hang for good
            process.StandardInput.Close();
            var output = new StringBuilder();
            var gate = new object();
            void Collect(string? line)
            {
                if (line is null)
                    return;
                lock (gate)
                    if (output.Length < 64 * 1024)
                        output.AppendLine(line);
            }
            process.OutputDataReceived += (_, e) => Collect(e.Data);
            process.ErrorDataReceived += (_, e) => Collect(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RunTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                process.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (ct.IsCancellationRequested)
                    throw;
                throw new SmapiInstallException(Loc.T("smapi.failed", "it did not finish in time and was stopped"));
            }
            lock (gate)
                return (process.ExitCode, output.ToString());
        }
    }

    /// <summary>The end of the installer's output, which is where it says what went wrong.</summary>
    internal static string Tail(string text, int length)
    {
        var clean = Regex.Replace(text.Trim(), @"\s*\r?\n\s*", " ");
        return clean.Length <= length ? clean : "..." + clean[^length..];
    }

    internal sealed class Release
    {
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        public List<Asset>? Assets { get; set; }
    }

    internal sealed class Asset
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? Digest { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
