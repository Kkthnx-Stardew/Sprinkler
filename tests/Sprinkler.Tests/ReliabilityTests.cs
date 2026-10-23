using System.IO.Compression;
using System.Net;
using System.Text;
using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>Downloads and archives that arrive in an unhelpful shape must still end up installed, or fail with a clear reason.</summary>
public class ReliabilityTests
{
    private static byte[] ZipBytes(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Cleanly(string path)
    {
        Downloader.Cleanup(path);
        return path;
    }

    // ------------------------------------------------------------ archives

    [Fact]
    public void AZipNamedLikeA7zIsStillRead()
    {
        using var d = new TempDir();
        var path = d.Combine("mod.7z");
        File.WriteAllBytes(path, ZipBytes(("Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0"))));
        using var a = ArchiveFile.Open(path);
        Assert.Equal("me.Thing", Assert.Single(ArchiveInspector.FindMods(a)).Manifest.UniqueId);
    }

    [Fact]
    public void SniffingReadsTheFirstBytesNotTheName()
    {
        using var d = new TempDir();
        var zip = d.Combine("a.rar");
        File.WriteAllBytes(zip, ZipBytes(("x.txt", "x")));
        var seven = d.Combine("b.zip");
        File.WriteAllBytes(seven, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0 });
        var rar = d.Combine("c.zip");
        File.WriteAllBytes(rar, Encoding.ASCII.GetBytes("Rar!\x1A\x07\x00"));
        var text = d.Write("d.zip", "hello");

        Assert.Equal(ArchiveKind.Zip, ArchiveFile.Sniff(zip));
        Assert.Equal(ArchiveKind.SevenZip, ArchiveFile.Sniff(seven));
        Assert.Equal(ArchiveKind.Rar, ArchiveFile.Sniff(rar));
        Assert.Equal(ArchiveKind.Unknown, ArchiveFile.Sniff(text));
    }

    [Fact]
    public void AZipInsideAZipIsOpenedAndInstalled()
    {
        using var d = new TempDir();
        var inner = ZipBytes(("Thing/manifest.json", Zips.Manifest("me.Thing", "2.0.0", "Thing")), ("Thing/Thing.dll", "code"));
        var outer = d.Combine("Download.zip");
        File.WriteAllBytes(outer, BuildOuter(inner));

        var mods = d.Combine("Mods");
        Directory.CreateDirectory(mods);
        var settings = new SettingsStore(d.Combine("settings.json"));
        var installer = new Installer(new HistoryStore(d.Combine("history.json"), d.Combine("backups")), settings);

        using (var a = ArchiveFile.Open(outer))
        {
            var found = Assert.Single(ArchiveInspector.FindMods(a));
            Assert.Equal("me.Thing", found.Manifest.UniqueId);
            var plan = Installer.Plan(outer, a, mods, ModScanner.Scan(mods));
            installer.Apply(plan, mods, "test");
        }

        Assert.Equal("2.0.0", ManifestReader.ReadFile(Path.Combine(mods, "Thing", "manifest.json"), "x")!.Version);
        Assert.True(File.Exists(Path.Combine(mods, "Thing", "Thing.dll")));
        Assert.Empty(Directory.Exists(AppEnvironment.StagingDir)
            ? Directory.GetDirectories(AppEnvironment.StagingDir, "nested-*")
            : Array.Empty<string>());
    }

    private static byte[] BuildOuter(byte[] inner)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open()))
                w.Write("hi");
            using var s = zip.CreateEntry("Thing 2.0.0.zip").Open();
            s.Write(inner, 0, inner.Length);
        }
        return ms.ToArray();
    }

    [Fact]
    public void ARootLevelModInsideAZipGetsItsNameFromTheManifest()
    {
        using var d = new TempDir();
        var inner = ZipBytes(("manifest.json", Zips.Manifest("me.Flat", "1.0.0", "Flat Mod")), ("Flat.dll", "code"));
        var outer = d.Combine("Download.zip");
        File.WriteAllBytes(outer, BuildOuter(inner));
        var mods = d.Combine("Mods");
        Directory.CreateDirectory(mods);

        using var a = ArchiveFile.Open(outer);
        var plan = Installer.Plan(outer, a, mods, ModScanner.Scan(mods));
        Assert.Equal(Path.Combine(mods, "Flat Mod"), Assert.Single(plan.Folders).TargetPath);
    }

    [Fact]
    public void AnArchiveWithoutAnyModStillSaysSo()
    {
        using var d = new TempDir();
        var outer = d.Combine("Download.zip");
        File.WriteAllBytes(outer, BuildOuter(ZipBytes(("notes.txt", "just notes"))));
        using var a = ArchiveFile.Open(outer);
        Assert.Empty(ArchiveInspector.FindMods(a));
    }

    [Fact]
    public void TwoCopiesOfOneModInAFileUseTheNewerAndSayWhy()
    {
        using var d = new TempDir();
        var zip = Zips.Build(d, "both.zip",
            ("Old/manifest.json", Zips.Manifest("me.Thing", "1.0.0", "Thing")),
            ("New/manifest.json", Zips.Manifest("me.Thing", "1.5.0", "Thing")));
        var mods = d.Combine("Mods");
        Directory.CreateDirectory(mods);

        using var a = ArchiveFile.Open(zip);
        var plan = Installer.Plan(zip, a, mods, ModScanner.Scan(mods));
        var folder = Assert.Single(plan.Folders);
        Assert.Equal("1.5.0", folder.Source.Manifest.Version);
        Assert.Single(folder.Warnings);
        Assert.Contains("Old", folder.Warnings[0]);
    }

    // ------------------------------------------------------------ downloads

    private sealed class FastRetries : IDisposable
    {
        private readonly TimeSpan _before = Downloader.RetryPause;

        public FastRetries() => Downloader.RetryPause = TimeSpan.FromMilliseconds(10);

        public void Dispose() => Downloader.RetryPause = _before;
    }

    [Fact]
    public async Task ADownloadIsTriedAgainAfterAServerError()
    {
        using var _ = new FastRetries();
        using var web = new FakeWeb();
        var calls = 0;
        var bytes = ZipBytes(("Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0")));
        web.Handler = (path, ctx) =>
        {
            if (path != "/file.zip")
                return null;
            if (Interlocked.Increment(ref calls) == 1)
            {
                ctx.Response.StatusCode = 503;
                return null;
            }
            return bytes;
        };

        var file = Cleanly(await Downloader.FetchAsync(web.Base + "/file.zip", null, null, CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Equal(".zip", Path.GetExtension(file));
    }

    [Fact]
    public async Task ADownloadMovesToTheNextMirrorWithoutRetryingAMissingFile()
    {
        using var _ = new FastRetries();
        using var web = new FakeWeb();
        var bytes = ZipBytes(("Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0")));
        web.Handler = (path, ctx) => path == "/good.zip" ? bytes : null;

        var file = await Downloader.FetchAsync(new[] { web.Base + "/gone.zip", web.Base + "/good.zip" }, null, null, CancellationToken.None);
        try
        {
            Assert.Equal(1, web.Requests.Count(r => r.EndsWith("/gone.zip")));
            Assert.Equal(1, web.Requests.Count(r => r.EndsWith("/good.zip")));
        }
        finally
        {
            Downloader.Cleanup(file);
        }
    }

    [Fact]
    public async Task AFailingDownloadReportsTheLastReason()
    {
        using var _ = new FastRetries();
        using var web = new FakeWeb();
        web.Handler = (_, ctx) =>
        {
            ctx.Response.StatusCode = 500;
            return null;
        };
        var ex = await Assert.ThrowsAsync<DownloadException>(() => Downloader.FetchAsync(web.Base + "/x.zip", null, null, CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.Equal(3, web.Requests.Count);
    }

    [Fact]
    public async Task ADownloadIsNamedByWhatItIs()
    {
        using var web = new FakeWeb();
        var bytes = ZipBytes(("Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0")));
        web.Handler = (_, _) => bytes;
        var file = Cleanly(await Downloader.FetchAsync(web.Base + "/odd", "mod.7z", null, CancellationToken.None));
        Assert.Equal(".zip", Path.GetExtension(file));
    }

    [Fact]
    public async Task AWebPageSentAsAFileIsRefused()
    {
        using var web = new FakeWeb();
        web.Handler = (_, ctx) =>
        {
            ctx.Response.ContentType = "application/octet-stream";
            return Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Please log in</body></html>");
        };
        await Assert.ThrowsAsync<DownloadException>(() => Downloader.FetchAsync(web.Base + "/x.zip", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task ADownloadThatGoesQuietIsGivenUp()
    {
        using var _ = new FastRetries();
        var before = Downloader.StallTimeout;
        Downloader.StallTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var web = new FakeWeb();
            web.Handler = (path, ctx) =>
            {
                // promise a body, send a little, then say nothing
                ctx.Response.ContentLength64 = 5000;
                ctx.Response.OutputStream.Write(new byte[10]);
                ctx.Response.OutputStream.Flush();
                Thread.Sleep(1000);
                return null;
            };
            var ex = await Assert.ThrowsAsync<DownloadException>(() => Downloader.FetchAsync(web.Base + "/slow.zip", null, null, CancellationToken.None));
            Assert.True(ex.Transient);
        }
        finally
        {
            Downloader.StallTimeout = before;
        }
    }

    // ------------------------------------------------------------ watcher

    [Fact]
    public async Task TheWatcherFindsAFileItMissedWhenTheFolderIsReadAgain()
    {
        using var d = new TempDir();
        var downloads = d.Combine("downloads");
        Directory.CreateDirectory(downloads);
        var seen = new List<string>();
        using var watcher = new DownloadWatcher(path =>
        {
            lock (seen)
                seen.Add(Path.GetFileName(path));
            return Task.CompletedTask;
        });

        var zip = Zips.Build(d, "Late-1-0-0.zip", ("Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0")));
        // the folder is watched only after the file is already there, with an old start gate
        File.Move(zip, Path.Combine(downloads, "Late-1-0-0.zip"));
        File.SetCreationTimeUtc(Path.Combine(downloads, "Late-1-0-0.zip"), DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(Path.Combine(downloads, "Late-1-0-0.zip"), DateTime.UtcNow.AddMinutes(-5));
        watcher.Start(downloads);
        watcher.ScanRecent(TimeSpan.FromMinutes(20));

        var end = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < end)
        {
            lock (seen)
                if (seen.Count > 0)
                    break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        lock (seen)
            Assert.Equal(new[] { "Late-1-0-0.zip" }, seen);

        // a second look does not hand over the same file again
        watcher.ScanRecent(TimeSpan.FromMinutes(20));
        await Task.Delay(3000, TestContext.Current.CancellationToken);
        lock (seen)
            Assert.Single(seen);
    }
}
