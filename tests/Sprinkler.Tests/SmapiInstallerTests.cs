using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

public class SmapiInstallerTests : IDisposable
{
    private static string Folder => Platform.IsWindows ? "windows" : "linux";

    private static string Installer => Platform.IsWindows ? "SMAPI.Installer.exe" : "SMAPI.Installer";

    private readonly TempDir _temp = new();

    public void Dispose()
    {
        SmapiInstaller.RunnerOverride = null;
        SmapiInstaller.RunTimeout = TimeSpan.FromMinutes(4);
        _temp.Dispose();
    }

    private static string Release(string version = "4.5.2", string digest = "sha256:1b19ec7336e935c454674986aaa3182f576be085a9ab5c39a6b0b1385c64d9ad",
        bool draft = false, bool prerelease = false, string? link = null, string? extraAsset = null)
    {
        link ??= $"{AppEnvironment.GitHubSiteUrl}/Pathoschild/SMAPI/releases/download/{version}/SMAPI-{version}-installer.zip";
        return $$"""
            {"draft":{{draft.ToString().ToLowerInvariant()}},"prerelease":{{prerelease.ToString().ToLowerInvariant()}},"html_url":"https://github.com/Pathoschild/SMAPI/releases/tag/{{version}}",
             "assets":[{{extraAsset}}{"name":"SMAPI-{{version}}-installer.zip","size":41889142,"digest":"{{digest}}","browser_download_url":"{{link}}"}]}
            """;
    }

    private static SmapiRelease Pick(string json) =>
        SmapiInstaller.Pick(System.Text.Json.JsonSerializer.Deserialize<SmapiInstaller.Release>(json, AppEnvironment.Json));

    // ---------------------------------------------------------------- choosing the release

    [Fact]
    public void TheInstallerFileAndItsFingerprintAreTaken()
    {
        var release = Pick(Release());
        Assert.Equal("4.5.2", release.Version);
        Assert.Equal("SMAPI-4.5.2-installer.zip", release.AssetName);
        Assert.Equal("1b19ec7336e935c454674986aaa3182f576be085a9ab5c39a6b0b1385c64d9ad", release.Sha256);
        Assert.Equal(41889142, release.AssetSize);
    }

    [Fact]
    public void TheDoubleZippedFileIsNotTheOneThatIsPicked()
    {
        var twice = $$"""{"name":"SMAPI-4.5.2-installer-double-zipped.zip","size":41766494,"digest":"sha256:{{new string('a', 64)}}","browser_download_url":"{{AppEnvironment.GitHubSiteUrl}}/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer-double-zipped.zip"},""";
        Assert.Equal("SMAPI-4.5.2-installer.zip", Pick(Release(extraAsset: twice)).AssetName);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DraftsAndPreReleasesAreNotOffered(bool draft, bool prerelease) =>
        Assert.Throws<SmapiInstallException>(() => Pick(Release(draft: draft, prerelease: prerelease)));

    [Fact]
    public void AFileWithoutAFingerprintCannotBeChecked()
    {
        Assert.Throws<SmapiInstallException>(() => Pick(Release(digest: "")));
        Assert.Throws<SmapiInstallException>(() => Pick(Release(digest: "sha256:abc")));
        Assert.Throws<SmapiInstallException>(() => Pick(Release(digest: "md5:" + new string('a', 32))));
    }

    [Fact]
    public void OnlySmapisOwnReleasePathIsAccepted()
    {
        Assert.Throws<SmapiInstallException>(() => Pick(Release(link: "https://evil.example/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip")));
        Assert.Throws<SmapiInstallException>(() => Pick(Release(link: $"{AppEnvironment.GitHubSiteUrl}/someone/else/releases/download/4.5.2/SMAPI-4.5.2-installer.zip")));
    }

    [Fact]
    public void ANameThatIsNotTheInstallerIsIgnored()
    {
        var json = Release().Replace("SMAPI-4.5.2-installer.zip\",\"size", "SMAPI-4.5.2-source.zip\",\"size");
        Assert.Throws<SmapiInstallException>(() => Pick(json));
        Assert.Throws<SmapiInstallException>(() => SmapiInstaller.Pick(null));
    }

    // ---------------------------------------------------------------- the file

    private static byte[] InstallerZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static SmapiRelease Fake(byte[] zip, string version = "4.5.2", string? url = null) => new(
        version, "https://github.com/Pathoschild/SMAPI/releases", $"SMAPI-{version}-installer.zip",
        url ?? $"{AppEnvironment.GitHubSiteUrl}/Pathoschild/SMAPI/releases/download/{version}/SMAPI-{version}-installer.zip",
        zip.Length, Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());

    [Fact]
    public void AFileThatDoesNotMatchItsFingerprintIsRefused()
    {
        var zip = InstallerZip(("a/internal/windows/x", "x"));
        var path = _temp.Combine("f.zip");
        File.WriteAllBytes(path, zip);
        SmapiInstaller.Verify(path, Fake(zip));
        Assert.Throws<SmapiInstallException>(() => SmapiInstaller.Verify(path, Fake(zip) with { Sha256 = new string('0', 64) }));
    }

    [Fact]
    public void OnlyTheFilesForThisSystemAreUnpacked()
    {
        var zip = InstallerZip(
            ("SMAPI 4.5.2 installer/install on Windows.bat", "bat"),
            ("SMAPI 4.5.2 installer/internal/windows/SMAPI.Installer.exe", "win"),
            ("SMAPI 4.5.2 installer/internal/windows/lib/deep.dll", "deep"),
            ("SMAPI 4.5.2 installer/internal/linux/SMAPI.Installer", "lin"),
            ("SMAPI 4.5.2 installer/internal/macOS/SMAPI.Installer", "mac"));
        var path = _temp.Combine("f.zip");
        File.WriteAllBytes(path, zip);
        var work = _temp.Combine("work");
        Directory.CreateDirectory(work);

        SmapiInstaller.Unpack(path, work);

        Assert.Equal(Platform.IsWindows ? "win" : "lin", File.ReadAllText(Path.Combine(work, Installer)));
        Assert.Single(Directory.GetFiles(work, "*", SearchOption.TopDirectoryOnly));
        Assert.Equal(Platform.IsWindows, File.Exists(Path.Combine(work, "lib", "deep.dll")));
    }

    [Theory]
    [InlineData("SMAPI 4.5.2 installer/internal/{0}/../../../escaped.txt")]
    [InlineData("SMAPI 4.5.2 installer/internal/{0}/..\\..\\escaped.txt")]
    [InlineData("SMAPI 4.5.2 installer/internal/{0}//absolute.txt")]
    public void APathThatClimbsOutOfTheWorkFolderIsRefused(string pattern)
    {
        var zip = InstallerZip((string.Format(pattern, Folder), "bad"));
        var path = _temp.Combine("f.zip");
        File.WriteAllBytes(path, zip);
        var work = _temp.Combine("work");
        Directory.CreateDirectory(work);

        Assert.Throws<SmapiInstallException>(() => SmapiInstaller.Unpack(path, work));
        Assert.False(File.Exists(_temp.Combine("escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_temp.Path)!, "escaped.txt")));
    }

    [Fact]
    public void ABrokenZipIsRefusedWithASentence()
    {
        var path = _temp.Combine("f.zip");
        File.WriteAllText(path, "this is not a zip");
        var work = _temp.Combine("work");
        Directory.CreateDirectory(work);
        Assert.Throws<SmapiInstallException>(() => SmapiInstaller.Unpack(path, work));
    }

    [Fact]
    public void TheEndOfTheOutputIsKeptShort()
    {
        Assert.Equal("a b c", SmapiInstaller.Tail("a\r\nb\n  c ", 100));
        Assert.StartsWith("...", SmapiInstaller.Tail(new string('x', 500), 50));
        Assert.Equal(53, SmapiInstaller.Tail(new string('x', 500), 50).Length);
    }

    // ---------------------------------------------------------------- the whole install

    /// <summary>A game folder that passes as one: the two game files exist, and nothing of SMAPI does.</summary>
    private GameInstall FakeGame()
    {
        var game = _temp.Combine("game");
        Directory.CreateDirectory(Path.Combine(game, "Mods"));
        // any real assembly will do for the version lookup
        File.Copy(typeof(SmapiInstaller).Assembly.Location, Path.Combine(game, "Stardew Valley.dll"));
        return new GameInstall(game, Path.Combine(game, "Mods"), "Manual", "1.6.15", null);
    }

    private static string ThisVersion => AppEnvironment.Version.Split('-')[0];

    [Fact]
    public async Task TheInstallerIsDownloadedCheckedUnpackedAndRunOnTheGameFolder()
    {
        var game = FakeGame();
        var zip = InstallerZip(($"SMAPI x installer/internal/{Folder}/{Installer}", "program"), ("SMAPI x installer/internal/linux/ignored", "x"));
        using var web = new FakeWeb { Handler = (path, _) => path.EndsWith("-installer.zip", StringComparison.Ordinal) ? zip : null };
        var release = Fake(zip, ThisVersion);
        var seen = new List<string>();
        SmapiInstaller.RunnerOverride = (program, args, work) =>
        {
            seen.AddRange(args);
            Assert.Equal("program", File.ReadAllText(program));
            // what SMAPI's installer does: put its files in the game folder
            File.Copy(typeof(SmapiInstaller).Assembly.Location, Path.Combine(game.GamePath, "StardewModdingAPI.dll"));
            return Task.FromResult((0, "SMAPI is installed!"));
        };
        var percents = new List<int>();

        var version = await SmapiInstaller.InstallAsync(game, release with { AssetUrl = release.AssetUrl.Replace(AppEnvironment.GitHubSiteUrl, web.Base + "/ghsite") }, percents.Add, CancellationToken.None);

        Assert.Equal(ThisVersion, version);
        Assert.Equal(new[] { "--install", "--no-prompt", "--game-path", game.GamePath }, seen);
        Assert.Equal(100, percents[^1]);
        Assert.Equal(percents.OrderBy(p => p), percents);
        // nothing is left in the staging folder
        Assert.Empty(Directory.GetDirectories(AppEnvironment.StagingDir, "smapi-*"));
    }

    [Fact]
    public async Task ATamperedDownloadNeverRuns()
    {
        var game = FakeGame();
        var zip = InstallerZip(($"SMAPI x installer/internal/{Folder}/{Installer}", "program"));
        using var web = new FakeWeb { Handler = (path, _) => path.EndsWith("-installer.zip", StringComparison.Ordinal) ? zip.Reverse().ToArray() : null };
        var ran = false;
        SmapiInstaller.RunnerOverride = (_, _, _) => { ran = true; return Task.FromResult((0, "")); };
        var release = Fake(zip, ThisVersion);

        await Assert.ThrowsAsync<SmapiInstallException>(() =>
            SmapiInstaller.InstallAsync(game, release with { AssetUrl = web.Base + "/ghsite/x-installer.zip" }, null, CancellationToken.None));

        Assert.False(ran);
    }

    [Fact]
    public async Task AFailedInstallerIsReportedWithWhatItSaid()
    {
        var game = FakeGame();
        var zip = InstallerZip(($"SMAPI x installer/internal/{Folder}/{Installer}", "program"));
        using var web = new FakeWeb { Handler = (path, _) => path.EndsWith("-installer.zip", StringComparison.Ordinal) ? zip : null };
        SmapiInstaller.RunnerOverride = (_, _, _) => Task.FromResult((1, "Oops!\r\nThe game folder is read only."));
        var release = Fake(zip, ThisVersion);

        var ex = await Assert.ThrowsAsync<SmapiInstallException>(() =>
            SmapiInstaller.InstallAsync(game, release with { AssetUrl = web.Base + "/ghsite/x-installer.zip" }, null, CancellationToken.None));

        Assert.Contains("read only", ex.Message);
    }

    [Fact]
    public async Task AnInstallerThatSucceedsButLeavesTheWrongVersionIsCaught()
    {
        var game = FakeGame();
        var zip = InstallerZip(($"SMAPI x installer/internal/{Folder}/{Installer}", "program"));
        using var web = new FakeWeb { Handler = (path, _) => path.EndsWith("-installer.zip", StringComparison.Ordinal) ? zip : null };
        // exits cleanly and installs nothing
        SmapiInstaller.RunnerOverride = (_, _, _) => Task.FromResult((0, ""));
        var release = Fake(zip, "9.9.9");

        var ex = await Assert.ThrowsAsync<SmapiInstallException>(() =>
            SmapiInstaller.InstallAsync(game, release with { AssetUrl = web.Base + "/ghsite/x-installer.zip" }, null, CancellationToken.None));

        Assert.Contains("9.9.9", ex.Message);
    }

    [Fact]
    public async Task ALatestReleaseIsReadFromGitHubsApi()
    {
        using var web = new FakeWeb { Handler = (path, _) => path == "/gh/repos/Pathoschild/SMAPI/releases/latest" ? Encoding.UTF8.GetBytes(Release()) : null };
        var release = await SmapiInstaller.FindLatestAsync(CancellationToken.None);
        Assert.Equal("4.5.2", release.Version);

        web.Handler = (_, _) => null;
        await Assert.ThrowsAsync<SmapiInstallException>(() => SmapiInstaller.FindLatestAsync(CancellationToken.None));
    }
}

/// <summary>
/// Runs SMAPI's real installer, downloaded from GitHub, on a copy of a game folder. Off unless SPRINKLER_REAL_SMAPI_GAME names a
/// folder to use (it is changed, so point it at a throwaway copy that holds Stardew Valley.dll and the game's executable).
/// </summary>
public class RealSmapiInstallerTests
{
    [Fact]
    public async Task TheRealInstallerInstallsAndThenUpdatesOverItself()
    {
        var folder = Environment.GetEnvironmentVariable("SPRINKLER_REAL_SMAPI_GAME");
        if (string.IsNullOrEmpty(folder))
            return;

        var game = GameLocator.Inspect(folder, "Manual") ?? throw new InvalidOperationException("Not a game folder: " + folder);
        var release = await SmapiInstaller.FindLatestAsync(CancellationToken.None);

        var first = await SmapiInstaller.InstallAsync(game, release, null, CancellationToken.None);
        Assert.Equal(release.Version, first);
        Assert.True(Directory.Exists(Path.Combine(folder, "Mods")));

        // a second run replaces the files of the first, which is what an update is
        var second = await SmapiInstaller.InstallAsync(GameLocator.Inspect(folder, "Manual")!, release, null, CancellationToken.None);
        Assert.Equal(release.Version, second);
    }
}
