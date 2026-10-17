using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sprinkler.Core;
using Sprinkler.ViewModels;
using Xunit;

namespace Sprinkler.Tests;

public class SelfUpdatePickTests
{
    private const string Page = "https://github.com/Kkthnx-Stardew/Sprinkler/releases/tag/";

    private static JsonElement Releases(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Release(string tag, bool prerelease = false, bool draft = false, string assets = "[]") =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{Page}}{{tag}}","prerelease":{{prerelease.ToString().ToLowerInvariant()}},"draft":{{draft.ToString().ToLowerInvariant()}},"assets":{{assets}}}""";

    [Fact]
    public void ThePickIsTheNewestReleaseOverTheCurrentVersion()
    {
        var list = Releases($"[{Release("v1.1.0")},{Release("v1.3.0")},{Release("v1.2.0")},{Release("v0.9.0")}]");
        Assert.Equal("1.3.0", SelfUpdate.Pick(list, ModVersion.Parse("1.0.0")!, "win-x64.zip")!.Version);
        Assert.Null(SelfUpdate.Pick(list, ModVersion.Parse("1.3.0")!, "win-x64.zip"));
        Assert.Null(SelfUpdate.Pick(list, ModVersion.Parse("2.0.0")!, "win-x64.zip"));
    }

    [Fact]
    public void DraftsAreSkippedAndPreReleasesOnlyCountForPreReleaseUsers()
    {
        var list = Releases($"[{Release("v2.0.0", draft: true)},{Release("v1.5.0-beta.1", prerelease: true)},{Release("v1.1.0")}]");
        Assert.Equal("1.1.0", SelfUpdate.Pick(list, ModVersion.Parse("1.0.0")!, "win-x64.zip")!.Version);
        Assert.Equal("1.5.0-beta.1", SelfUpdate.Pick(list, ModVersion.Parse("1.0.0-alpha.2")!, "win-x64.zip")!.Version);
        // an alpha tester is also offered the stable release when it is the newest
        var stable = Releases($"[{Release("v1.0.0")},{Release("v1.0.0-beta.1", prerelease: true)}]");
        Assert.Equal("1.0.0", SelfUpdate.Pick(stable, ModVersion.Parse("1.0.0-beta.1")!, "win-x64.zip")!.Version);
    }

    [Fact]
    public void OnlyTheProjectsOwnReleaseFilesAreAccepted()
    {
        var good = $"{AppEnvironment.GitHubSiteUrl}/{AppEnvironment.RepoSlug}/releases/download/v1.1.0/";
        var assets = $$"""
            [{"name":"Sprinkler-v1.1.0-win-x64.zip","browser_download_url":"{{good}}Sprinkler-v1.1.0-win-x64.zip","size":24000000},
             {"name":"Sprinkler-v1.1.0-win-x64.zip.sha256","browser_download_url":"{{good}}Sprinkler-v1.1.0-win-x64.zip.sha256","size":100},
             {"name":"Sprinkler-v1.1.0-win-x64.zip.sha256.sig","browser_download_url":"{{good}}Sprinkler-v1.1.0-win-x64.zip.sha256.sig","size":72}]
            """;
        var release = SelfUpdate.Pick(Releases($"[{Release("v1.1.0", assets: assets)}]"), ModVersion.Parse("1.0.0")!, "win-x64.zip")!;
        Assert.True(release.CanDownload);
        Assert.Equal("Sprinkler-v1.1.0-win-x64.zip", release.AssetName);
        Assert.Equal(24000000, release.AssetSize);

        var foreign = assets.Replace(good, "https://evil.example/releases/download/v1.1.0/");
        Assert.False(SelfUpdate.Pick(Releases($"[{Release("v1.1.0", assets: foreign)}]"), ModVersion.Parse("1.0.0")!, "win-x64.zip")!.CanDownload);

        // no signature file means no automatic install, only the page
        var unsigned = assets.Replace(".sha256.sig", ".other");
        Assert.False(SelfUpdate.Pick(Releases($"[{Release("v1.1.0", assets: unsigned)}]"), ModVersion.Parse("1.0.0")!, "win-x64.zip")!.CanDownload);

        // a build for another system is not offered
        Assert.False(SelfUpdate.Pick(Releases($"[{Release("v1.1.0", assets: assets)}]"), ModVersion.Parse("1.0.0")!, "linux-x64.tar.gz")!.CanDownload);
    }

    [Fact]
    public void ReleasePagesOutsideGitHubAreNotTrusted()
    {
        var list = Releases("""[{"tag_name":"v9.0.0","html_url":"https://example.com/x","prerelease":false,"draft":false,"assets":[]}]""");
        Assert.Null(SelfUpdate.Pick(list, ModVersion.Parse("1.0.0")!, "win-x64.zip"));
    }

    [Fact]
    public void HashesAreReadFromASha256sumList()
    {
        var hash = new string('a', 64);
        var other = new string('B', 64);
        var text = $"{other}  other.zip\n{hash} *Sprinkler-v1.zip\r\n";
        Assert.Equal(hash, SelfUpdate.HashFor(text, "Sprinkler-v1.zip"));
        Assert.Equal(other.ToLowerInvariant(), SelfUpdate.HashFor(text, "other.zip"));
        Assert.Null(SelfUpdate.HashFor(text, "missing.zip"));
        Assert.Null(SelfUpdate.HashFor("not a hash  Sprinkler-v1.zip", "Sprinkler-v1.zip"));
    }

    [Fact]
    public void TheBuiltInKeyIsAValidP256PublicKey()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(SelfUpdate.DefaultKey, out _);
        Assert.Equal(256, key.KeySize);
    }
}

public class SelfUpdateSignatureTests
{
    private static (byte[] Pub, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportSubjectPublicKeyInfo(), key);
    }

    [Fact]
    public void ASignedListVerifiesAndAnyChangeFails()
    {
        var (pub, key) = NewKey();
        using var _ = key;
        var data = Encoding.UTF8.GetBytes("abc  file.zip\n");
        var sig = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        SelfUpdate.VerifySignature(data, sig, pub);
        Assert.Throws<AppUpdateException>(() => SelfUpdate.VerifySignature(Encoding.UTF8.GetBytes("abd  file.zip\n"), sig, pub));

        var tampered = (byte[])sig.Clone();
        tampered[^1] ^= 1;
        Assert.Throws<AppUpdateException>(() => SelfUpdate.VerifySignature(data, tampered, pub));
        Assert.Throws<AppUpdateException>(() => SelfUpdate.VerifySignature(data, new byte[] { 1, 2, 3 }, pub));
    }

    [Fact]
    public void ASignatureFromAnotherKeyFails()
    {
        var (pub, key) = NewKey();
        var (_, stranger) = NewKey();
        using var _a = key;
        using var _b = stranger;
        var data = Encoding.UTF8.GetBytes("abc  file.zip\n");
        var sig = stranger.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Throws<AppUpdateException>(() => SelfUpdate.VerifySignature(data, sig, pub));
    }
}

public class SelfUpdateFileTests
{
    internal static string ProgramName => OperatingSystem.IsWindows() ? "Sprinkler.exe" : "Sprinkler";

    /// <summary>A stand-in for the program: big enough, and starting the way a program for this system starts.</summary>
    internal static byte[] FakeProgram(byte fill = 0)
    {
        var bytes = new byte[1_500_000];
        Array.Fill(bytes, fill);
        if (OperatingSystem.IsWindows())
        {
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
        }
        else
        {
            bytes[0] = 0x7F;
            bytes[1] = (byte)'E';
            bytes[2] = (byte)'L';
            bytes[3] = (byte)'F';
        }
        return bytes;
    }

    internal static string MakeZip(TempDir dir, params (string Name, byte[] Data)[] files)
    {
        var path = dir.Combine("pack-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, data) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        return path;
    }

    internal static string MakeTar(TempDir dir, params (string Name, byte[] Data)[] files)
    {
        var path = dir.Combine("pack-" + Guid.NewGuid().ToString("N")[..6] + ".tar.gz");
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip);
        foreach (var (name, data) in files)
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
        return path;
    }

    private static string Current(TempDir dir)
    {
        Directory.CreateDirectory(dir.Combine("app"));
        var path = dir.Combine("app", ProgramName);
        File.WriteAllBytes(path, FakeProgram(1));
        return path;
    }

    [Fact]
    public void TheProgramIsTakenFromTheTopOfAZipOrOneFolderDown()
    {
        using var dir = new TempDir();
        var current = Current(dir);
        var fresh = FakeProgram(2);

        var flat = SelfUpdate.Unpack(MakeZip(dir, ("README.md", new byte[] { 1 }), (ProgramName, fresh)), current);
        Assert.Equal(fresh, File.ReadAllBytes(flat));
        Assert.Equal(dir.Combine("app"), Path.GetDirectoryName(flat));

        var nested = SelfUpdate.Unpack(MakeZip(dir, ("Sprinkler-v1/" + ProgramName, fresh)), current);
        Assert.Equal(fresh, File.ReadAllBytes(nested));
    }

    [Fact]
    public void TheProgramIsTakenFromATarGz()
    {
        using var dir = new TempDir();
        var current = Current(dir);
        var fresh = FakeProgram(3);
        var path = SelfUpdate.Unpack(MakeTar(dir, ("Sprinkler-v1/LICENSE", new byte[] { 1 }), ("Sprinkler-v1/" + ProgramName, fresh)), current);
        Assert.Equal(fresh, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("a/b/c/{0}")]
    [InlineData("../{0}")]
    [InlineData("x/../{0}")]
    [InlineData("C:/{0}")]
    [InlineData("Other.exe")]
    public void NothingElseCountsAsTheProgram(string pattern)
    {
        using var dir = new TempDir();
        var current = Current(dir);
        var name = string.Format(pattern, ProgramName);
        Assert.Throws<AppUpdateException>(() => SelfUpdate.Unpack(MakeZip(dir, (name, FakeProgram())), current));
        Assert.DoesNotContain(Directory.GetFiles(dir.Combine("app")), f => Path.GetFileName(f).StartsWith("Sprinkler.new", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileThatIsNotAProgramIsRefused()
    {
        using var dir = new TempDir();
        var current = Current(dir);
        var small = new byte[100];
        Assert.Throws<AppUpdateException>(() => SelfUpdate.Unpack(MakeZip(dir, (ProgramName, small)), current));
        var wrongKind = FakeProgram();
        wrongKind[0] = (byte)'#';
        Assert.Throws<AppUpdateException>(() => SelfUpdate.Unpack(MakeZip(dir, (ProgramName, wrongKind)), current));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(current)!, OperatingSystem.IsWindows() ? "Sprinkler.new.exe" : "Sprinkler.new")));
    }

    [Fact]
    public void SwappingKeepsTheOldProgramOnWindowsAndReplacesItOnLinux()
    {
        using var dir = new TempDir();
        var current = Current(dir);
        var fresh = FakeProgram(9);
        var staged = SelfUpdate.Unpack(MakeZip(dir, (ProgramName, fresh)), current);

        SelfUpdate.Swap(staged, current);

        Assert.Equal(fresh, File.ReadAllBytes(current));
        Assert.False(File.Exists(staged));
        if (OperatingSystem.IsWindows())
            Assert.Equal(FakeProgram(1), File.ReadAllBytes(current + ".old"));

        SelfUpdate.RemoveLeftovers(current);
        Assert.False(File.Exists(current + ".old"));
        Assert.True(File.Exists(current));
    }

    [Fact]
    public void AFailedSwapPutsTheOldProgramBack()
    {
        using var dir = new TempDir();
        var current = Current(dir);
        var missing = dir.Combine("app", "does-not-exist");
        Assert.Throws<AppUpdateException>(() => SelfUpdate.Swap(missing, current));
        Assert.Equal(FakeProgram(1), File.ReadAllBytes(current));
    }

    [Fact]
    public void OnlyAProgramNamedSprinklerInAWritableFolderMayReplaceItself()
    {
        using var dir = new TempDir();
        var current = Current(dir);
        Assert.Equal(current, SelfUpdate.ReplaceablePath(current));
        Assert.Null(SelfUpdate.ReplaceablePath(dir.Combine("app", "dotnet")));
        Assert.Null(SelfUpdate.ReplaceablePath(dir.Combine("app", "missing", ProgramName)));
    }
}

public class SelfUpdateFlowTests
{
    private const string Tag = "v99.0.0";

    internal sealed class Release : IDisposable
    {
        public TempDir Dir = new();
        public FakeWeb Web = new();
        public ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Current;
        public string AssetName;
        public byte[] Archive = Array.Empty<byte>();
        public byte[] Sums = Array.Empty<byte>();
        public byte[] Signature = Array.Empty<byte>();
        public byte[] FreshProgram = SelfUpdateFileTests.FakeProgram(7);
        public UpdateService Service;

        public Release()
        {
            Directory.CreateDirectory(Dir.Combine("app"));
            Current = Dir.Combine("app", SelfUpdateFileTests.ProgramName);
            File.WriteAllBytes(Current, SelfUpdateFileTests.FakeProgram(1));
            AssetName = $"Sprinkler-{Tag}-{SelfUpdate.PlatformSuffix()}";

            var packed = AssetName.EndsWith(".zip", StringComparison.Ordinal)
                ? SelfUpdateFileTests.MakeZip(Dir, (SelfUpdateFileTests.ProgramName, FreshProgram))
                : SelfUpdateFileTests.MakeTar(Dir, ("Sprinkler/" + SelfUpdateFileTests.ProgramName, FreshProgram));
            Archive = File.ReadAllBytes(packed);
            Resign();

            var down = $"{Web.Base}/ghsite/{AppEnvironment.RepoSlug}/releases/download/{Tag}/";
            var json = $$"""
                [{"tag_name":"{{Tag}}","html_url":"https://github.com/Kkthnx-Stardew/Sprinkler/releases/tag/{{Tag}}","prerelease":false,"draft":false,"assets":[
                  {"name":"{{AssetName}}","browser_download_url":"{{down}}{{AssetName}}","size":{{Archive.Length}}},
                  {"name":"{{AssetName}}.sha256","browser_download_url":"{{down}}{{AssetName}}.sha256","size":100},
                  {"name":"{{AssetName}}.sha256.sig","browser_download_url":"{{down}}{{AssetName}}.sha256.sig","size":72}]}]
                """;
            Web.Handler = (path, _) =>
            {
                if (path == $"/gh/repos/{AppEnvironment.RepoSlug}/releases")
                    return FakeWeb.Json(json);
                if (path.EndsWith(AssetName, StringComparison.Ordinal))
                    return Archive;
                if (path.EndsWith(".sha256", StringComparison.Ordinal))
                    return Sums;
                if (path.EndsWith(".sha256.sig", StringComparison.Ordinal))
                    return Signature;
                return null;
            };

            Service = new UpdateService(new SettingsStore(Dir.Combine("settings.json")), new HistoryStore(Dir.Combine("history.json"), Dir.Combine("backups")), _ => { })
            {
                SelfPathOverride = Current,
                SelfKeyOverride = Key.ExportSubjectPublicKeyInfo(),
                SelfCheckOverride = (_, _) => Task.FromResult(true),
            };
        }

        public void Resign()
        {
            var hash = Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant();
            Sums = Encoding.UTF8.GetBytes($"{hash}  {AssetName}\n");
            Signature = Key.SignData(Sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }

        public JsonElement App() => JsonSerializer.SerializeToElement(Service.Snapshot(), AppEnvironment.Json).GetProperty("app");

        public void Dispose()
        {
            Service.Dispose();
            Key.Dispose();
            Web.Dispose();
            Dir.Dispose();
        }
    }

    private static bool Supported => SelfUpdate.PlatformSuffix() is not null;

    [Fact]
    public async Task AnInstallableReleaseIsOfferedAndInstalled()
    {
        if (!Supported)
            return;
        using var r = new Release();
        await r.Service.CheckAppUpdateAsync();
        var update = r.App().GetProperty("update");
        Assert.Equal("99.0.0", update.GetProperty("version").GetString());
        Assert.True(update.GetProperty("canInstall").GetBoolean());

        var error = await r.Service.InstallAppUpdateAsync();

        Assert.Null(error);
        Assert.Equal(r.FreshProgram, File.ReadAllBytes(r.Current));
        Assert.DoesNotContain(Directory.GetFiles(r.Dir.Combine("app")), f => Path.GetFileName(f).StartsWith("Sprinkler.new", StringComparison.Ordinal));
        Assert.False(r.App().GetProperty("update").GetProperty("busy").GetBoolean());
    }

    [Fact]
    public async Task ADamagedDownloadChangesNothing()
    {
        if (!Supported)
            return;
        using var r = new Release();
        await r.Service.CheckAppUpdateAsync();
        var damaged = (byte[])r.Archive.Clone();
        damaged[damaged.Length / 2] ^= 0xFF;
        r.Archive = damaged; // the signed list still names the original

        var error = await r.Service.InstallAppUpdateAsync();

        Assert.Contains("fingerprint", error);
        Assert.Equal(SelfUpdateFileTests.FakeProgram(1), File.ReadAllBytes(r.Current));
    }

    [Fact]
    public async Task AReleaseSignedWithAnotherKeyChangesNothing()
    {
        if (!Supported)
            return;
        using var r = new Release();
        await r.Service.CheckAppUpdateAsync();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        r.Signature = stranger.SignData(r.Sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var error = await r.Service.InstallAppUpdateAsync();

        Assert.Contains("signature", error);
        Assert.Equal(SelfUpdateFileTests.FakeProgram(1), File.ReadAllBytes(r.Current));
    }

    [Fact]
    public async Task ASignedListThatDoesNotNameTheFileChangesNothing()
    {
        if (!Supported)
            return;
        using var r = new Release();
        await r.Service.CheckAppUpdateAsync();
        r.Sums = Encoding.UTF8.GetBytes($"{new string('0', 64)}  some-other-file.zip\n");
        r.Signature = r.Key.SignData(r.Sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var error = await r.Service.InstallAppUpdateAsync();

        Assert.Contains("does not mention", error);
        Assert.Equal(SelfUpdateFileTests.FakeProgram(1), File.ReadAllBytes(r.Current));
    }

    [Fact]
    public async Task ANewProgramThatDoesNotStartIsNotInstalled()
    {
        if (!Supported)
            return;
        using var r = new Release();
        r.Service.SelfCheckOverride = (_, _) => Task.FromResult(false);
        await r.Service.CheckAppUpdateAsync();

        var error = await r.Service.InstallAppUpdateAsync();

        Assert.Contains("did not start", error);
        Assert.Equal(SelfUpdateFileTests.FakeProgram(1), File.ReadAllBytes(r.Current));
        Assert.DoesNotContain(Directory.GetFiles(r.Dir.Combine("app")), f => Path.GetFileName(f).StartsWith("Sprinkler.new", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAReplaceableProgramOnlyTheReleasePageIsOffered()
    {
        if (!Supported)
            return;
        using var r = new Release();
        r.Service.SelfPathOverride = null;
        // running from source or from a read-only folder: the real check finds no program that may replace itself
        await r.Service.CheckAppUpdateAsync();
        var update = r.App().GetProperty("update");
        Assert.False(update.GetProperty("canInstall").GetBoolean());
        Assert.NotNull(await r.Service.InstallAppUpdateAsync());
    }

    [Fact]
    public async Task SwitchingTheLookOffStopsAskingGitHub()
    {
        if (!Supported)
            return;
        using var r = new Release();
        r.Service.SetCheckAppUpdates(false);
        await r.Service.CheckAppUpdateAsync();
        Assert.DoesNotContain(r.Web.Requests, q => q.Contains("/releases"));
        Assert.False(r.App().TryGetProperty("update", out _));
        Assert.False(JsonSerializer.SerializeToElement(r.Service.Snapshot(), AppEnvironment.Json).GetProperty("settings").GetProperty("checkAppUpdates").GetBoolean());

        r.Service.SetCheckAppUpdates(true);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end && !r.App().TryGetProperty("update", out _))
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(r.App().TryGetProperty("update", out _));
    }
}

public class SelfUpdateUiTests
{
    [AvaloniaFact]
    public async Task ThePillAsksThenUpdatesAndClosesTheWindow()
    {
        if (SelfUpdate.PlatformSuffix() is null)
            return;
        using var r = new SelfUpdateFlowTests.Release();
        r.Service.RelaunchOverride = path => path == r.Current;
        await r.Service.CheckAppUpdateAsync();
        var ui = new FakeUi();
        var vm = new MainViewModel(r.Service) { Ui = ui, SuppressTour = true };
        vm.Refresh();

        Assert.True(vm.HasAppUpdate);
        Assert.Contains("99.0.0", vm.AppUpdateText);

        var press = vm.OpenAppUpdateCommand.ExecuteAsync(null);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end && vm.Dialog is not ConfirmDialogViewModel)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        var dialog = Assert.IsType<ConfirmDialogViewModel>(vm.Dialog);
        Assert.Contains("99.0.0", dialog.Title);
        Assert.Equal(3, dialog.Bullets.Count);
        dialog.YesCommand.Execute(null);
        await press;

        Assert.True(ui.Exited);
        Assert.Equal(r.FreshProgram, File.ReadAllBytes(r.Current));
    }

    [AvaloniaFact]
    public async Task ACancelledQuestionChangesNothing()
    {
        if (SelfUpdate.PlatformSuffix() is null)
            return;
        using var r = new SelfUpdateFlowTests.Release();
        await r.Service.CheckAppUpdateAsync();
        var ui = new FakeUi();
        var vm = new MainViewModel(r.Service) { Ui = ui, SuppressTour = true };
        vm.Refresh();

        var press = vm.OpenAppUpdateCommand.ExecuteAsync(null);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end && vm.Dialog is not ConfirmDialogViewModel)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        ((ConfirmDialogViewModel)vm.Dialog!).No();
        await press;

        Assert.False(ui.Exited);
        Assert.Equal(SelfUpdateFileTests.FakeProgram(1), File.ReadAllBytes(r.Current));
    }
}
