using System.Net;
using System.Text;
using System.Text.Json;
using StardewModUpdater.Core;
using Xunit;

namespace StardewModUpdater.Tests;

/// <summary>A tiny local stand-in for smapi.io, CurseForge and GitHub.</summary>
internal sealed class FakeWeb : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public int Port { get; }
    public string Base => $"http://127.0.0.1:{Port}";
    public Func<string, HttpListenerContext, byte[]?> Handler { get; set; } = (_, _) => null;
    public List<string> Requests { get; } = new();

    public FakeWeb()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add(Base + "/");
        _listener.Start();
        _ = Task.Run(Loop);

        Environment.SetEnvironmentVariable("SMU_SMAPI_URL", Base + "/smapi");
        Environment.SetEnvironmentVariable("SMU_CF_URL", Base + "/cf");
        Environment.SetEnvironmentVariable("SMU_GH_URL", Base + "/gh");
        Environment.SetEnvironmentVariable("SMU_NEXUS_URL", Base + "/nexus");
        Environment.SetEnvironmentVariable("SMU_NEXUS_SITE", Base + "/nexussite");
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }

            var path = ctx.Request.Url!.AbsolutePath;
            lock (Requests)
                Requests.Add(ctx.Request.HttpMethod + " " + path);
            var body = Handler(path, ctx);
            if (body is null)
            {
                ctx.Response.StatusCode = ctx.Response.StatusCode == 200 ? 404 : ctx.Response.StatusCode;
                ctx.Response.Close();
                continue;
            }
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
        }
    }

    public static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        foreach (var v in new[] { "SMU_SMAPI_URL", "SMU_CF_URL", "SMU_GH_URL", "SMU_NEXUS_URL", "SMU_NEXUS_SITE" })
            Environment.SetEnvironmentVariable(v, null);
    }
}

public class ServiceTests
{
    private sealed class Rig : IDisposable
    {
        public TempDir Dir = new();
        public FakeWeb Web = new();
        public string Game => Dir.Combine("game");
        public string Mods => Dir.Combine("game", "Mods");
        public List<string> Opened = new();
        public UpdateService Service;

        public Rig(SourcePolicy policy, string installedVersion = "1.0.0")
        {
            Directory.CreateDirectory(Mods);
            File.WriteAllText(Path.Combine(Game, "Stardew Valley.dll"), "");
            Dir.Write("game/Mods/Thing/manifest.json",
                Zips.Manifest("me.Thing", installedVersion, "Thing", ", \"UpdateKeys\": [\"Nexus:5\", \"CurseForge:10\", \"GitHub:me/thing\"]"));
            Dir.Write("game/Mods/Thing/config.json", "{\"keep\":1}");

            var settings = new SettingsStore(Dir.Combine("settings.json"));
            settings.Data.Policy = policy;
            settings.Data.DownloadsPath = Dir.Combine("downloads");
            Directory.CreateDirectory(Dir.Combine("downloads"));
            Service = new UpdateService(settings, new HistoryStore(Dir.Combine("history.json"), Dir.Combine("backups")), u => Opened.Add(u));

            Web.Handler = (path, ctx) =>
            {
                if (path == "/smapi")
                    return FakeWeb.Json("""[{"id":"me.Thing","suggestedUpdate":{"version":"1.1.0","url":"https://www.nexusmods.com/stardewvalley/mods/5"},"metadata":{"name":"Thing","nexusID":5,"curseForgeID":10,"gitHubRepo":"me/thing","main":{"version":"1.1.0"}},"errors":[]}]""");
                return null;
            };
        }

        public async Task<JsonElement> StartAsync()
        {
            Assert.Null(await Service.SetGameFolderAsync(Game));
            return Snapshot();
        }

        public JsonElement Snapshot() => JsonSerializer.SerializeToElement(Service.Snapshot(), AppEnvironment.Json);

        public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> done, int seconds = 20)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                var s = Snapshot();
                if (done(s))
                    return s;
                await Task.Delay(100);
            }
            throw new TimeoutException("Condition not met. Last state: " + Snapshot());
        }

        public static string ItemStatus(JsonElement s) => s.GetProperty("items")[0].GetProperty("status").GetString()!;

        public void Dispose()
        {
            Service.Dispose();
            Web.Dispose();
            Dir.Dispose();
        }
    }

    private static byte[] ZipBytes(string id, string version)
    {
        using var d = new TempDir();
        var path = Zips.Build(d, "z.zip", ("Thing/manifest.json", Zips.Manifest(id, version, "Thing")), ("Thing/Thing.dll", "new"));
        return File.ReadAllBytes(path);
    }

    private static void ServeCurseForge(Rig rig, byte[]? download, int downloadStatus = 200)
    {
        var inner = rig.Web.Handler;
        rig.Web.Handler = (path, ctx) =>
        {
            if (path == "/cf/api/v1/mods/10/files")
                return FakeWeb.Json("""{"data":[{"id":100,"dateCreated":"2026-10-01T00:00:00Z","displayName":"Thing 1.1.0","fileName":"Thing 1.1.0.zip","releaseType":1}]}""");
            if (path == "/cf/api/v1/mods/10/files/100/download")
            {
                ctx.Response.StatusCode = downloadStatus;
                return download;
            }
            return inner(path, ctx);
        };
    }

    private static void ServeGitHub(Rig rig, byte[] download)
    {
        var inner = rig.Web.Handler;
        rig.Web.Handler = (path, ctx) =>
        {
            if (path == "/gh/repos/me/thing/releases")
                return FakeWeb.Json($$"""[{"tag_name":"v1.1.0","name":"1.1.0","draft":false,"prerelease":false,"html_url":"{{rig.Web.Base}}/ghpage","assets":[{"name":"Thing-1.1.0.zip","browser_download_url":"{{rig.Web.Base}}/gh/dl/Thing-1.1.0.zip"}]}]""");
            if (path == "/gh/dl/Thing-1.1.0.zip")
                return download;
            return inner(path, ctx);
        };
    }

    [Fact]
    public async Task FindsTheUpdateAndRanksSourcesByPolicy()
    {
        using var rig = new Rig(SourcePolicy.NexusFirst);
        ServeCurseForge(rig, ZipBytes("me.Thing", "1.1.0"));
        ServeGitHub(rig, ZipBytes("me.Thing", "1.1.0"));
        await rig.StartAsync();
        var s = await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Review");

        var offers = s.GetProperty("items")[0].GetProperty("offers").EnumerateArray().Select(o => o.GetProperty("source").GetString()).ToList();
        Assert.Equal(new[] { "Nexus", "CurseForge", "GitHub" }, offers);
        Assert.Equal("Manual", s.GetProperty("items")[0].GetProperty("offers")[0].GetProperty("mode").GetString());
        Assert.Equal("Auto", s.GetProperty("items")[0].GetProperty("offers")[1].GetProperty("mode").GetString());
    }

    [Fact]
    public async Task FallsThroughBrokenSourcesToOneThatWorks()
    {
        using var rig = new Rig(SourcePolicy.InstantFirst);
        ServeCurseForge(rig, null, downloadStatus: 500);
        ServeGitHub(rig, ZipBytes("me.Thing", "1.1.0"));
        await rig.StartAsync();
        await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Review");

        _ = rig.Service.ApplyAsync(new[] { "nexus:5" });
        var s = await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Finished");

        Assert.Equal("Done", Rig.ItemStatus(s));
        var row = s.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("uniqueId").GetString() == "me.Thing");
        Assert.Equal("UpToDate", row.GetProperty("state").GetString());
        Assert.Equal("1.1.0", row.GetProperty("version").GetString());
        Assert.Equal("GitHub", s.GetProperty("history")[0].GetProperty("source").GetString());
        Assert.Equal("1.1.0", ManifestReader.ReadFile(rig.Dir.Combine("game", "Mods", "Thing", "manifest.json"), "x")!.Version);
        Assert.Equal("{\"keep\":1}", File.ReadAllText(rig.Dir.Combine("game", "Mods", "Thing", "config.json")));
    }

    [Fact]
    public async Task RejectsADownloadThatIsForADifferentMod()
    {
        using var rig = new Rig(SourcePolicy.InstantFirst);
        ServeCurseForge(rig, ZipBytes("someone.Else", "9.9.9"));
        ServeGitHub(rig, ZipBytes("me.Thing", "1.1.0"));
        await rig.StartAsync();
        await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Review");

        _ = rig.Service.ApplyAsync(new[] { "nexus:5" });
        var s = await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Finished");

        Assert.Equal("Done", Rig.ItemStatus(s));
        Assert.Equal("GitHub", s.GetProperty("history")[0].GetProperty("source").GetString());
        Assert.False(Directory.Exists(rig.Dir.Combine("game", "Mods", "Else")));
    }

    [Fact]
    public async Task WaitsForTheUserOnNexusThenInstallsTheDroppedFile()
    {
        using var rig = new Rig(SourcePolicy.NexusFirst);
        await rig.StartAsync();
        await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Review");

        _ = rig.Service.ApplyAsync(new[] { "nexus:5" });
        var waiting = await rig.WaitForAsync(x => x.GetProperty("items").GetArrayLength() > 0 && Rig.ItemStatus(x) == "WaitingForYou");
        Assert.Contains(rig.Opened, u => u.Contains("tab=files"));
        Assert.Equal("Manual", waiting.GetProperty("items")[0].GetProperty("offers")[0].GetProperty("mode").GetString());

        // an unrelated archive must not satisfy the wait
        using (var other = new TempDir())
        {
            var wrong = Zips.Build(other, "wrong.zip", ("X/manifest.json", Zips.Manifest("other.Mod", "3.0.0")));
            await rig.Service.HandleArchiveAsync(wrong, "test", CancellationToken.None);
            Assert.Equal("WaitingForYou", Rig.ItemStatus(rig.Snapshot()));
        }

        // an older file for the right mod is refused and the app keeps waiting
        using (var old = new TempDir())
        {
            var older = Zips.Build(old, "old.zip", ("Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0")));
            await rig.Service.HandleArchiveAsync(older, "test", CancellationToken.None);
            Assert.Equal("WaitingForYou", Rig.ItemStatus(rig.Snapshot()));
        }

        using (var good = new TempDir())
        {
            var zip = Zips.Build(good, "Thing-1.1.0.zip", ("Thing/manifest.json", Zips.Manifest("me.Thing", "1.1.0")));
            await rig.Service.HandleArchiveAsync(zip, "your browser download", CancellationToken.None);
        }
        var s = await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Finished");
        Assert.Equal("Done", Rig.ItemStatus(s));
        Assert.Equal("1.1.0", ManifestReader.ReadFile(rig.Dir.Combine("game", "Mods", "Thing", "manifest.json"), "x")!.Version);
    }

    [Fact]
    public async Task OffersToInstallAnUpdatedFileFoundInDownloads()
    {
        using var rig = new Rig(SourcePolicy.NexusFirst);
        await rig.StartAsync();
        await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Review");

        using var d = new TempDir();
        var zip = Zips.Build(d, "t.zip", ("Thing/manifest.json", Zips.Manifest("me.Thing", "1.5.0")));
        await rig.Service.HandleArchiveAsync(zip, "your browser download", CancellationToken.None);
        var found = rig.Snapshot().GetProperty("found");
        Assert.Equal(1, found.GetArrayLength());

        await rig.Service.InstallFoundAsync(found[0].GetProperty("id").GetString()!);
        Assert.Equal("1.5.0", ManifestReader.ReadFile(rig.Dir.Combine("game", "Mods", "Thing", "manifest.json"), "x")!.Version);
    }

    [Fact]
    public async Task FlagsMissingRequiredMods()
    {
        using var rig = new Rig(SourcePolicy.NexusFirst);
        rig.Dir.Write("game/Mods/Needy/manifest.json",
            Zips.Manifest("me.Needy", "1.0.0", "Needy", ", \"Dependencies\": [{\"UniqueID\":\"fw.Framework\"}], \"ContentPackFor\": {\"UniqueID\":\"Pathoschild.ContentPatcher\"}"));
        var inner = rig.Web.Handler;
        rig.Web.Handler = (path, ctx) =>
        {
            if (path == "/smapi")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                var asked = reader.ReadToEnd();
                var entries = new List<string>();
                if (asked.Contains("fw.Framework"))
                    entries.Add("""{"id":"fw.Framework","metadata":{"name":"The Framework","nexusID":77,"main":{"version":"2.0.0"}},"errors":[]}""");
                return FakeWeb.Json("[" + string.Join(",", entries) + "]");
            }
            return inner(path, ctx);
        };
        await rig.StartAsync();
        var s = await rig.WaitForAsync(x => x.GetProperty("phase").GetString() == "Review");

        var items = s.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("isNew").GetBoolean() && i.GetProperty("name").GetString() == "The Framework");
        Assert.Contains(s.GetProperty("problems").EnumerateArray(), p => p.GetProperty("title").GetString()!.Contains("Content Patcher") || p.GetProperty("title").GetString()!.Contains("Pathoschild"));
    }
}

public class NexusFlowTests
{
    private static byte[] ZipBytes(string id, string version)
    {
        using var d = new TempDir();
        var path = Zips.Build(d, "z.zip", ("Thing/manifest.json", Zips.Manifest(id, version, "Thing")), ("Thing/Thing.dll", "new"));
        return File.ReadAllBytes(path);
    }

    private static (TempDir Dir, FakeWeb Web, UpdateService Service, List<string> Opened) Make(bool premium, bool requireNxmKey)
    {
        var dir = new TempDir();
        var web = new FakeWeb();
        var opened = new List<string>();
        Directory.CreateDirectory(dir.Combine("game", "Mods"));
        File.WriteAllText(dir.Combine("game", "Stardew Valley.dll"), "");
        dir.Write("game/Mods/Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0", "Thing", ", \"UpdateKeys\": [\"Nexus:5\"]"));

        var settings = new SettingsStore(dir.Combine("settings.json"));
        settings.Data.DownloadsPath = dir.Combine("downloads");
        Directory.CreateDirectory(dir.Combine("downloads"));
        var service = new UpdateService(settings, new HistoryStore(dir.Combine("history.json"), dir.Combine("backups")), u => opened.Add(u));

        var zip = ZipBytes("me.Thing", "1.1.0");
        web.Handler = (path, ctx) =>
        {
            if (path == "/smapi")
                return FakeWeb.Json("""[{"id":"me.Thing","suggestedUpdate":{"version":"1.1.0","url":"x"},"metadata":{"name":"Thing","nexusID":5,"main":{"version":"1.1.0"}},"errors":[]}]""");
            if (path.StartsWith("/nexus/"))
            {
                if (ctx.Request.Headers["apikey"] != "k-0123456789012345678901234567890")
                {
                    ctx.Response.StatusCode = 401;
                    return null;
                }
                if (ctx.Request.Headers["Application-Name"] is null)
                {
                    ctx.Response.StatusCode = 400;
                    return null;
                }
                if (path == "/nexus/users/validate.json")
                    return FakeWeb.Json($$"""{"name":"Tester","is_premium":{{(premium ? "true" : "false")}}}""");
                if (path == "/nexus/games/stardewvalley/mods/5/files.json")
                    return FakeWeb.Json("""{"files":[{"file_id":900,"name":"Thing","version":"1.1.0","mod_version":"1.1.0","category_name":"MAIN","is_primary":true,"file_name":"Thing-1.1.0.zip","uploaded_timestamp":10}]}""");
                if (path == "/nexus/games/stardewvalley/mods/5/changelogs.json")
                    return FakeWeb.Json("""{"1.1.0":["Fixed <b>bugs</b>","Added things"]}""");
                if (path == "/nexus/games/stardewvalley/mods/5/files/900/download_link.json")
                {
                    var hasKey = ctx.Request.QueryString["key"] == "NXMKEY" && ctx.Request.QueryString["expires"] == "999";
                    if (!premium && requireNxmKey && !hasKey)
                    {
                        ctx.Response.StatusCode = 403;
                        return null;
                    }
                    return FakeWeb.Json($$"""[{"name":"Nexus CDN","short_name":"Nexus CDN","URI":"{{web.Base}}/cdn/Thing-1.1.0.zip"}]""");
                }
            }
            if (path == "/cdn/Thing-1.1.0.zip")
                return zip;
            return null;
        };
        return (dir, web, service, opened);
    }

    private static async Task<JsonElement> WaitAsync(UpdateService s, Func<JsonElement, bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < end)
        {
            var snap = JsonSerializer.SerializeToElement(s.Snapshot(), AppEnvironment.Json);
            if (done(snap)) return snap;
            await Task.Delay(100);
        }
        throw new TimeoutException(JsonSerializer.Serialize(s.Snapshot(), AppEnvironment.Json));
    }

    [Fact]
    public async Task PremiumAccountDownloadsStraightFromNexus()
    {
        var (dir, web, service, _) = Make(premium: true, requireNxmKey: false);
        using (dir) using (web) using (service)
        {
            await service.SetGameFolderAsync(dir.Combine("game"));
            Assert.Null(await service.ConnectNexusAsync("k-0123456789012345678901234567890"));
            var s = await WaitAsync(service, x => x.GetProperty("phase").GetString() == "Review");

            var item = s.GetProperty("items")[0];
            Assert.Equal("Auto", item.GetProperty("offers")[0].GetProperty("mode").GetString());
            Assert.True(s.GetProperty("nexus").GetProperty("premium").GetBoolean());
            Assert.Equal("Fixed bugs", item.GetProperty("changelog")[0].GetString());

            _ = service.ApplyAsync(new[] { "nexus:5" });
            s = await WaitAsync(service, x => x.GetProperty("phase").GetString() == "Finished");
            Assert.Equal("Done", s.GetProperty("items")[0].GetProperty("status").GetString());
            Assert.Equal("Nexus Mods", s.GetProperty("history")[0].GetProperty("source").GetString());
        }
    }

    [Fact]
    public async Task FreeAccountOpensTheFilePageAndFinishesFromAnNxmLink()
    {
        var (dir, web, service, opened) = Make(premium: false, requireNxmKey: true);
        using (dir) using (web) using (service)
        {
            await service.SetGameFolderAsync(dir.Combine("game"));
            Assert.Null(await service.ConnectNexusAsync("k-0123456789012345678901234567890"));
            var s = await WaitAsync(service, x => x.GetProperty("phase").GetString() == "Review");

            var offer = s.GetProperty("items")[0].GetProperty("offers")[0];
            Assert.NotEqual("Auto", offer.GetProperty("mode").GetString());

            _ = service.ApplyAsync(new[] { "nexus:5" });
            await WaitAsync(service, x => x.GetProperty("items").GetArrayLength() > 0 && x.GetProperty("items")[0].GetProperty("status").GetString() == "WaitingForYou");
            Assert.Contains(opened, u => u.Contains("/mods/5") && u.Contains("file_id=900"));

            // without the one-time key, Nexus refuses and the app keeps waiting
            await service.HandleNxmAsync("nxm://stardewvalley/mods/5/files/900?key=WRONG&expires=1");
            s = await WaitAsync(service, x => x.GetProperty("items")[0].GetProperty("message").GetString()!.Contains("Try the download button"));
            Assert.Equal("WaitingForYou", s.GetProperty("items")[0].GetProperty("status").GetString());

            await service.HandleNxmAsync("nxm://stardewvalley/mods/5/files/900?key=NXMKEY&expires=999");
            s = await WaitAsync(service, x => x.GetProperty("phase").GetString() == "Finished");
            Assert.Equal("Done", s.GetProperty("items")[0].GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task SavedKeyIsValidatedBeforeTheFirstCheck()
    {
        var (dir, web, service, _) = Make(premium: true, requireNxmKey: false);
        using (dir) using (web) using (service)
        {
            service.Settings.NexusKey = "k-0123456789012345678901234567890";
            service.Settings.Data.GamePath = dir.Combine("game");
            await service.InitializeAsync();
            var s = await WaitAsync(service, x => x.GetProperty("phase").GetString() == "Review");
            Assert.Equal("Auto", s.GetProperty("items")[0].GetProperty("offers")[0].GetProperty("mode").GetString());
            Assert.True(s.GetProperty("nexus").GetProperty("connected").GetBoolean());
        }
    }

    [Fact]
    public async Task RejectsABadKeyWithAHelpfulMessage()
    {
        var (dir, web, service, _) = Make(premium: true, requireNxmKey: false);
        using (dir) using (web) using (service)
        {
            var error = await service.ConnectNexusAsync("wrong-key-0123456789012345678901234567890");
            Assert.Contains("did not accept", error);
            Assert.Null(service.Settings.NexusKey);
        }
    }
}

public class AppInfoTests
{
    private static UpdateService MakeService(TempDir dir) =>
        new(new SettingsStore(dir.Combine("settings.json")), new HistoryStore(dir.Combine("history.json"), dir.Combine("backups")), _ => { });

    [Fact]
    public async Task ReportsANewerReleaseOfTheApp()
    {
        using var dir = new TempDir();
        using var web = new FakeWeb();
        web.Handler = (path, ctx) => path == $"/gh/repos/{AppEnvironment.RepoSlug}/releases/latest"
            ? FakeWeb.Json("""{"tag_name":"v99.0.0","html_url":"https://github.com/Kkthnx-Stardew/StardewModUpdater/releases/tag/v99.0.0"}""")
            : null;
        using var service = MakeService(dir);
        await service.CheckAppUpdateAsync();
        var app = JsonSerializer.SerializeToElement(service.Snapshot(), AppEnvironment.Json).GetProperty("app");
        Assert.Equal("99.0.0", app.GetProperty("update").GetProperty("version").GetString());
        Assert.StartsWith("https://github.com/", app.GetProperty("repo").GetString());
    }

    [Fact]
    public async Task IgnoresAnOlderOrMissingRelease()
    {
        using var dir = new TempDir();
        using var web = new FakeWeb();
        web.Handler = (path, ctx) => path.EndsWith("/releases/latest") ? FakeWeb.Json("""{"tag_name":"v0.0.1","html_url":"https://github.com/x/y"}""") : null;
        using var service = MakeService(dir);
        await service.CheckAppUpdateAsync();
        var app = JsonSerializer.SerializeToElement(service.Snapshot(), AppEnvironment.Json).GetProperty("app");
        Assert.False(app.TryGetProperty("update", out _));
    }

    [Fact]
    public void DiagnosticsNeverContainTheKeyAndMaskTheUserFolder()
    {
        using var dir = new TempDir();
        using var service = MakeService(dir);
        service.Settings.NexusKey = "k-super-secret-0123456789012345678901";
        Log.Write("test line in " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var text = service.Diagnostics();
        Assert.Contains(AppEnvironment.Version, text);
        Assert.DoesNotContain("k-super-secret", text);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyKnownSitesCanBeOpened()
    {
        Assert.True(StardewModUpdater.Web.WebHost.TryOpenable("https://www.nexusmods.com/stardewvalley/mods/1915?tab=files", out _));
        Assert.True(StardewModUpdater.Web.WebHost.TryOpenable("https://github.com/Kkthnx-Stardew/StardewModUpdater/issues/new", out _));
        Assert.False(StardewModUpdater.Web.WebHost.TryOpenable("http://www.nexusmods.com/", out _));
        Assert.False(StardewModUpdater.Web.WebHost.TryOpenable("https://evilnexusmods.com/", out _));
        Assert.False(StardewModUpdater.Web.WebHost.TryOpenable("file:///C:/Windows/System32/calc.exe", out _));
    }
}

public class SmapiLogTests
{
    // Lines follow what SMAPI's own source writes (LogManager.LogModInfo and ModResolver).
    private const string Sample = """
[03:20:32 INFO  SMAPI] SMAPI 4.5.2 with Stardew Valley 1.6.15 build 24356 on Windows 11 (10.0.26200.0)
[03:20:44 INFO  SMAPI] Loaded 18 mods:
[03:20:44 INFO  SMAPI]    Content Patcher 2.9.1 by Pathoschild | Loads content packs.
[03:20:44 INFO  SMAPI] Loaded 17 content packs:
[03:20:44 ERROR SMAPI]    Skipped mods
[03:20:44 ERROR SMAPI]    --------------------------------------------------
[03:20:44 ERROR SMAPI]       These mods could not be added to your game.
[03:20:44 ERROR SMAPI]
[03:20:44 ERROR SMAPI]       - Sunberry Village 1.1.2 because it requires mods which aren't installed (Esca's Modding Plugins, Item Extensions).
[03:20:44 ERROR SMAPI]       - Old Thing 0.9 because it's no longer compatible. Please check for a new version at https://www.nexusmods.com/stardewvalley/mods/1.
[03:20:44 ERROR SMAPI]       - Odd Mod because something unexpected happened.
[03:20:44 ERROR SMAPI]
[03:20:44 WARN  SMAPI]    Changed save serializer
[03:20:44 WARN  SMAPI]    --------------------------------------------------
[03:20:44 WARN  SMAPI]       - SpaceCore
[03:21:10 ERROR Some Mod] Something broke
[03:21:11 ERROR Some Mod] Something broke again
[03:21:12 ERROR Other Mod] Oops
[03:21:13 ERROR game] Not counted
""";

    [Fact]
    public void FindsSkippedModsAndTheReasonsSmapiGave()
    {
        var log = SmapiLog.Parse(Sample.Split('\n'), DateTime.Now);
        Assert.Equal(18, log.LoadedMods);
        Assert.Equal(17, log.LoadedContentPacks);
        Assert.Equal(3, log.Skipped.Count);

        var sunberry = log.Skipped[0];
        Assert.Equal("Sunberry Village", sunberry.Name);
        Assert.Equal("1.1.2", sunberry.Version);
        Assert.Equal(SkipKind.MissingMods, sunberry.Kind);
        Assert.Contains("Item Extensions", sunberry.Reason);
        Assert.Equal(SkipKind.Incompatible, log.Skipped[1].Kind);
        Assert.Equal(SkipKind.Other, log.Skipped[2].Kind);
        Assert.Null(log.Skipped[2].Version);
    }

    [Fact]
    public void ModsInOtherWarningGroupsAreNotSkippedMods()
    {
        var log = SmapiLog.Parse(Sample.Split('\n'), DateTime.Now);
        Assert.DoesNotContain(log.Skipped, s => s.Name == "SpaceCore");
    }

    [Fact]
    public void CountsErrorsByModAndIgnoresSmapiAndGame()
    {
        var log = SmapiLog.Parse(Sample.Split('\n'), DateTime.Now);
        Assert.Equal(new[] { ("Some Mod", 2), ("Other Mod", 1) }, log.Errors.Select(e => (e.Source, e.Count)).ToArray());
    }

    [Fact]
    public void ReadsALogThatTheGameStillHasOpen()
    {
        using var d = new TempDir();
        var path = d.Write("SMAPI-latest.txt", Sample);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal(3, SmapiLog.Read(path)!.Skipped.Count);
        Assert.Null(SmapiLog.Read(d.Combine("missing.txt")));
    }

    [Theory]
    [InlineData("Nexus:1915")]
    [InlineData("nexus:1915")]
    [InlineData("NEXUS:1915")]
    public void UpdateKeysAreCaseInsensitive(string key) => Assert.Equal(SourceKind.Nexus, UpdateKey.Parse(key)!.Site);
}

public class ToggleTests
{
    private static async Task<(TempDir Dir, FakeWeb Web, UpdateService Service)> MakeAsync()
    {
        var dir = new TempDir();
        var web = new FakeWeb { Handler = (path, _) => path == "/smapi" ? FakeWeb.Json("[]") : null };
        Directory.CreateDirectory(dir.Combine("game", "Mods"));
        File.WriteAllText(dir.Combine("game", "Stardew Valley.dll"), "");
        dir.Write("game/Mods/Framework/manifest.json", Zips.Manifest("fw.Core", "1.0.0", "Framework"));
        dir.Write("game/Mods/Pack/manifest.json", Zips.Manifest("me.Pack", "1.0.0", "Pack", ", \"ContentPackFor\": {\"UniqueID\":\"fw.Core\"}"));
        var service = new UpdateService(new SettingsStore(dir.Combine("settings.json")), new HistoryStore(dir.Combine("history.json"), dir.Combine("backups")), _ => { });
        await service.SetGameFolderAsync(dir.Combine("game"));
        return (dir, web, service);
    }

    [Fact]
    public async Task TurnsAModOffAndOnWithTheDotConvention()
    {
        var (dir, web, service) = await MakeAsync();
        using (dir) using (web) using (service)
        {
            var (error, deps) = await service.SetModEnabledAsync("me.Pack", enable: false, force: false);
            Assert.Null(error); Assert.Null(deps);
            Assert.True(Directory.Exists(dir.Combine("game", "Mods", ".Pack")));
            Assert.False(Directory.Exists(dir.Combine("game", "Mods", "Pack")));

            (error, _) = await service.SetModEnabledAsync("me.Pack", enable: true, force: false);
            Assert.Null(error);
            Assert.True(Directory.Exists(dir.Combine("game", "Mods", "Pack")));
        }
    }

    [Fact]
    public async Task WarnsBeforeTurningOffAModThatOthersNeed()
    {
        var (dir, web, service) = await MakeAsync();
        using (dir) using (web) using (service)
        {
            var (error, deps) = await service.SetModEnabledAsync("fw.Core", enable: false, force: false);
            Assert.Null(error);
            Assert.Equal(new[] { "Pack" }, deps);
            Assert.True(Directory.Exists(dir.Combine("game", "Mods", "Framework")));

            (error, deps) = await service.SetModEnabledAsync("fw.Core", enable: false, force: true);
            Assert.Null(error); Assert.Null(deps);
            Assert.True(Directory.Exists(dir.Combine("game", "Mods", ".Framework")));
        }
    }

    [Fact]
    public async Task RefusesWhenTheTargetNameIsTaken()
    {
        var (dir, web, service) = await MakeAsync();
        using (dir) using (web) using (service)
        {
            Directory.CreateDirectory(dir.Combine("game", "Mods", ".Pack"));
            var (error, _) = await service.SetModEnabledAsync("me.Pack", enable: false, force: false);
            Assert.Contains("already exists", error);
            Assert.True(Directory.Exists(dir.Combine("game", "Mods", "Pack")));
        }
    }
}

public class PrefsTests
{
    [Fact]
    public void ThemeAndTourAreRememberedByTheApp()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        using (var service = new UpdateService(new SettingsStore(path), new HistoryStore(dir.Combine("h.json"), dir.Combine("b")), _ => { }))
        {
            service.SetPrefs("dark", true);
            service.SetPrefs("not-a-theme", null);
            var s = JsonSerializer.SerializeToElement(service.Snapshot(), AppEnvironment.Json).GetProperty("settings");
            Assert.Equal("dark", s.GetProperty("theme").GetString());
            Assert.True(s.GetProperty("tourSeen").GetBoolean());
        }

        var reloaded = new SettingsStore(path);
        Assert.Equal("dark", reloaded.Data.Theme);
        Assert.True(reloaded.Data.TourSeen);
    }
}
