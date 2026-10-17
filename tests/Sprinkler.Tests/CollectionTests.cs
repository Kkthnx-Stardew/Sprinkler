using System.Text.Json;
using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

public class CollectionAddressTests
{
    [Theory]
    [InlineData("https://www.nexusmods.com/games/stardewvalley/collections/htknoa", "htknoa", null)]
    [InlineData("https://www.nexusmods.com/games/stardewvalley/collections/HTKNOA/revisions/3", "htknoa", 3)]
    [InlineData("https://next.nexusmods.com/stardewvalley/collections/g14kxi", "g14kxi", null)]
    [InlineData("  nexusmods.com/stardewvalley/collections/tckf0m?tab=mods  ", "tckf0m", null)]
    [InlineData("htknoa", "htknoa", null)]
    public void AddressesAndBareCodesAreUnderstood(string text, string slug, int? revision)
    {
        var parsed = NexusCollections.Parse(text);
        Assert.NotNull(parsed);
        Assert.Equal(slug, parsed!.Value.Slug);
        Assert.Equal(revision, parsed.Value.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("https://example.com/collections/abcd")]
    [InlineData("ab")]
    public void AnythingElseIsNotACollection(string text) => Assert.Null(NexusCollections.Parse(text));

    [Fact]
    public void ACollectionForAnotherGameIsRefusedInPlainWords()
    {
        var ex = Assert.Throws<CollectionException>(() => NexusCollections.Parse("https://www.nexusmods.com/games/skyrimspecialedition/collections/abcdef"));
        Assert.Contains("different game", ex.Message);
    }

    [Theory]
    [InlineData("Content Patcher-1915-2-9-1-1752000000.zip", 1915)]
    [InlineData("Custom Companions-10448-5-1-0-1710000000.7z", 10448)]
    [InlineData("Mod-With-Dashes-77-1-0-0-123.rar", 77)]
    [InlineData(@"C:\Users\me\Downloads\Thing-5-1-1-0-9.zip", 5)]
    public void TheModIdIsReadFromANexusFileName(string path, long id) => Assert.Equal(id, UpdateService.NexusFileModId(path));

    [Theory]
    [InlineData("thing.zip")]
    [InlineData("Thing-1.0.zip")]
    [InlineData("holiday-photos-2024.zip")]
    public void OtherFileNamesGiveNoGuess(string path) => Assert.Null(UpdateService.NexusFileModId(path));
}

public class CollectionLookupTests
{
    private const string Reply = """
        {"data":{"collectionRevision":{"revisionNumber":4,
          "collection":{"name":"Cozy <b>Valley</b>","summary":"A <i>gentle</i> set.","user":{"name":"curator1"}},
          "modFiles":[
            {"fileId":10,"optional":false,"file":{"fileId":10,"name":"a","version":"1.0.0","mod":{"modId":100,"name":"Alpha","author":"A"}}},
            {"fileId":20,"optional":true,"file":{"fileId":20,"name":"b","version":"2.0.0","mod":{"modId":200,"name":"Beta","author":"B"}}},
            {"fileId":30,"optional":false,"file":{"fileId":30,"name":"smapi","version":"4.0","mod":{"modId":2400,"name":"SMAPI","author":"Pathoschild"}}},
            {"fileId":40,"optional":false,"file":null},
            {"fileId":50,"optional":false,"file":{"fileId":50,"name":"dup","version":"9","mod":{"modId":100,"name":"Alpha again","author":"A"}}}
          ]}}}
        """;

    [Fact]
    public async Task ReadsTheNameCuratorAndModsAndSkipsSmapiBrokenEntriesAndDuplicates()
    {
        using var web = new FakeWeb();
        string? sent = null;
        string? key = null;
        web.Handler = (path, ctx) =>
        {
            sent = new StreamReader(ctx.Request.InputStream).ReadToEnd();
            key = ctx.Request.Headers["apikey"];
            return path == "/graphql" ? FakeWeb.Json(Reply) : null;
        };

        var rev = await NexusCollections.FetchAsync("cozy1", null, CancellationToken.None);

        Assert.Equal("Cozy Valley", rev.Name);
        Assert.Equal("curator1", rev.Curator);
        Assert.Equal("A gentle set.", rev.Summary);
        Assert.Equal(4, rev.Revision);
        Assert.Equal(new long[] { 100, 200 }, rev.Mods.Select(m => m.ModId));
        Assert.Equal("Alpha", rev.Mods[0].Name);
        Assert.False(rev.Mods[0].Optional);
        Assert.True(rev.Mods[1].Optional);
        Assert.Equal(20, rev.Mods[1].FileId);
        Assert.Contains("\"slug\":\"cozy1\"", sent);
        Assert.Contains("\"domainName\":\"stardewvalley\"", sent);
        Assert.Null(key); // looking up a collection never sends the person's key
    }

    [Fact]
    public async Task ARevisionNumberIsPassedOn()
    {
        using var web = new FakeWeb();
        string? sent = null;
        web.Handler = (_, ctx) =>
        {
            sent = new StreamReader(ctx.Request.InputStream).ReadToEnd();
            return FakeWeb.Json(Reply);
        };
        await NexusCollections.FetchAsync("cozy1", 2, CancellationToken.None);
        Assert.Contains("\"revision\":2", sent);
    }

    [Fact]
    public async Task AnUnknownCollectionSaysSo()
    {
        using var web = new FakeWeb();
        web.Handler = (_, _) => FakeWeb.Json("""{"data":{"collectionRevision":null},"errors":[{"message":"not found"}]}""");
        var ex = await Assert.ThrowsAsync<CollectionException>(() => NexusCollections.FetchAsync("nope", null, CancellationToken.None));
        Assert.Contains("no collection called \"nope\"", ex.Message);
    }

    [Fact]
    public async Task ServerAndFormatProblemsBecomeReadableMessages()
    {
        using var web = new FakeWeb();
        web.Handler = (_, ctx) =>
        {
            ctx.Response.StatusCode = 503;
            return null;
        };
        var down = await Assert.ThrowsAsync<CollectionException>(() => NexusCollections.FetchAsync("x1234", null, CancellationToken.None));
        Assert.Contains("503", down.Message);

        web.Handler = (_, _) => FakeWeb.Json("<html>not json</html>");
        var junk = await Assert.ThrowsAsync<CollectionException>(() => NexusCollections.FetchAsync("x1234", null, CancellationToken.None));
        Assert.Contains("could not read", junk.Message);
    }
}

/// <summary>A collection through the whole service: look up, add as a profile, install what is missing, watch the profile grow.</summary>
public class CollectionServiceTests
{
    private const string Key = "k-0123456789012345678901234567890";

    private sealed class Rig : IDisposable
    {
        public TempDir Dir = new();
        public FakeWeb Web = new();
        public UpdateService Service;
        public List<string> Opened = new();
        public string Mods => Dir.Combine("game", "Mods");

        public Rig(bool premium)
        {
            Directory.CreateDirectory(Mods);
            File.WriteAllText(Dir.Combine("game", "Stardew Valley.dll"), "");
            Dir.Write("game/Mods/Alpha/manifest.json", Zips.Manifest("me.Alpha", "1.0.0", "Alpha", ", \"UpdateKeys\": [\"Nexus:100\"]"));
            Directory.CreateDirectory(Dir.Combine("downloads"));

            byte[] zip;
            using (var z = new TempDir())
                zip = File.ReadAllBytes(Zips.Build(z, "z.zip", ("Beta/manifest.json", Zips.Manifest("me.Beta", "2.0.0", "Beta", ", \"UpdateKeys\": [\"Nexus:200\"]")), ("Beta/Beta.dll", "x")));

            Web.Handler = (path, ctx) =>
            {
                switch (path)
                {
                    case "/smapi":
                        return FakeWeb.Json("[]");
                    case "/graphql":
                        return FakeWeb.Json("""
                            {"data":{"collectionRevision":{"revisionNumber":2,"collection":{"name":"Cozy Valley","summary":"Nice","user":{"name":"curator1"}},"modFiles":[
                              {"fileId":10,"optional":false,"file":{"fileId":10,"name":"a","version":"1.0.0","mod":{"modId":100,"name":"Alpha","author":"A"}}},
                              {"fileId":700,"optional":false,"file":{"fileId":700,"name":"b","version":"2.0.0","mod":{"modId":200,"name":"Beta","author":"B"}}},
                              {"fileId":30,"optional":true,"file":{"fileId":30,"name":"c","version":"3.0.0","mod":{"modId":300,"name":"Gamma","author":"C"}}}]}}}
                            """);
                    case "/nexus/users/validate.json":
                        return FakeWeb.Json($$"""{"name":"Tester","is_premium":{{(premium ? "true" : "false")}}}""");
                    case "/nexus/games/stardewvalley/mods/200/files.json":
                        return FakeWeb.Json("""{"files":[{"file_id":700,"name":"Beta","version":"2.0.0","mod_version":"2.0.0","category_name":"MAIN","is_primary":true,"file_name":"Beta.zip","uploaded_timestamp":10}]}""");
                    case "/nexus/games/stardewvalley/mods/200/files/700/download_link.json":
                        return FakeWeb.Json($$"""[{"name":"CDN","short_name":"CDN","URI":"{{Web.Base}}/cdn/Beta.zip"}]""");
                    case "/cdn/Beta.zip":
                        return zip;
                    case "/nexus/user/endorsements.json":
                        return FakeWeb.Json("[]");
                }
                return null;
            };

            var settings = new SettingsStore(Dir.Combine("settings.json"));
            settings.Data.DownloadsPath = Dir.Combine("downloads");
            Service = new UpdateService(settings, new HistoryStore(Dir.Combine("history.json"), Dir.Combine("backups")), u => Opened.Add(u), new ProfileStore(Dir.Combine("profiles.json")));
        }

        public JsonElement Snapshot() => JsonSerializer.SerializeToElement(Service.Snapshot(), AppEnvironment.Json);

        public async Task<JsonElement> WaitAsync(Func<JsonElement, bool> done, int seconds = 20)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                var s = Snapshot();
                if (done(s))
                    return s;
                await Task.Delay(50);
            }
            throw new TimeoutException("Not reached: " + Snapshot());
        }

        public async Task StartAsync()
        {
            Assert.Null(await Service.SetGameFolderAsync(Dir.Combine("game")));
            Assert.Null(await Service.ConnectNexusAsync(Key));
            await WaitAsync(s => s.GetProperty("phase").GetString() == "Review");
        }

        public JsonElement Profile() => Snapshot().GetProperty("profiles").GetProperty("profiles")[0];

        public void Dispose()
        {
            Service.Dispose();
            Web.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task ThePreviewCountsWhatIsAlreadyInstalled()
    {
        using var rig = new Rig(premium: true);
        await rig.StartAsync();

        var collection = await rig.Service.LookUpCollectionAsync("https://www.nexusmods.com/games/stardewvalley/collections/abc123");
        var preview = rig.Service.PreviewCollection(collection);

        Assert.Equal("Cozy Valley", preview.Name);
        Assert.Equal("curator1", preview.Curator);
        Assert.Equal(2, preview.Required);
        Assert.Equal(1, preview.Optional);
        Assert.Equal(1, preview.RequiredInstalled); // Alpha has Nexus:100
        Assert.Equal(0, preview.OptionalInstalled);
    }

    [Fact]
    public async Task AGarbledAddressIsExplainedBeforeAnyRequest()
    {
        using var rig = new Rig(premium: true);
        await rig.StartAsync();
        var before = rig.Web.Requests.Count;
        var ex = await Assert.ThrowsAsync<CollectionException>(() => rig.Service.LookUpCollectionAsync("what is this"));
        Assert.Contains("Paste the collection", ex.Message);
        Assert.Equal(before, rig.Web.Requests.Count);
    }

    [Fact]
    public async Task AddingMakesAProfileAndOffersMissingModsAsInstalls()
    {
        using var rig = new Rig(premium: true);
        await rig.StartAsync();
        var collection = await rig.Service.LookUpCollectionAsync("abc123");

        var result = rig.Service.AddCollection(collection, includeOptional: false);
        Assert.Null(result.Error);

        var s = await rig.WaitAsync(x => x.GetProperty("items").GetArrayLength() == 1 && x.GetProperty("phase").GetString() == "Review");
        var profile = rig.Profile();
        Assert.Equal("Cozy Valley", profile.GetProperty("name").GetString());
        Assert.Equal("Cozy Valley", profile.GetProperty("collectionName").GetString());
        Assert.Equal("curator1", profile.GetProperty("curator").GetString());
        Assert.True(profile.GetProperty("pending").GetBoolean());
        Assert.Equal(1, profile.GetProperty("toInstall").GetInt32());
        Assert.Equal(1, profile.GetProperty("modCount").GetInt32()); // only Alpha is installed so far
        Assert.False(s.GetProperty("profiles").TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.String); // adding never switches

        var item = s.GetProperty("items")[0];
        Assert.Equal("Beta", item.GetProperty("name").GetString());
        Assert.True(item.GetProperty("isNew").GetBoolean());
        Assert.Equal("2.0.0", item.GetProperty("target").GetString());
        Assert.Contains("Cozy Valley", item.GetProperty("why").GetString());
        Assert.Equal("Auto", item.GetProperty("offers")[0].GetProperty("mode").GetString());
    }

    [Fact]
    public async Task OptionalModsAreOnlyAddedWhenAsked()
    {
        using var rig = new Rig(premium: true);
        await rig.StartAsync();
        var collection = await rig.Service.LookUpCollectionAsync("abc123");
        rig.Service.AddCollection(collection, includeOptional: true);
        var profile = rig.Profile();
        Assert.Equal(2, profile.GetProperty("toInstall").GetInt32()); // Beta and Gamma
    }

    [Fact]
    public async Task APremiumInstallFinishesAndTheNewModJoinsTheProfile()
    {
        using var rig = new Rig(premium: true);
        await rig.StartAsync();
        rig.Service.AddCollection(await rig.Service.LookUpCollectionAsync("abc123"), false);
        await rig.WaitAsync(x => x.GetProperty("items").GetArrayLength() == 1 && x.GetProperty("phase").GetString() == "Review");

        _ = rig.Service.ApplyAsync(new[] { "new:nexus:200" });
        var s = await rig.WaitAsync(x => x.GetProperty("phase").GetString() == "Finished");
        Assert.Equal("Done", s.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(rig.Mods, "Beta", "manifest.json")));

        // the next check sees the new mod by its Nexus update key and the collection is complete
        await rig.Service.CheckAsync();
        s = await rig.WaitAsync(x => x.GetProperty("phase").GetString() == "Review" && !rig.Profile().GetProperty("pending").GetBoolean());
        Assert.Equal(2, rig.Profile().GetProperty("modCount").GetInt32());
        Assert.Equal(0, rig.Profile().GetProperty("toInstall").GetInt32());
        Assert.Equal(0, s.GetProperty("items").GetArrayLength());
        Assert.Contains(rig.Service.Profiles.Find("Cozy Valley")!.Enabled, id => id == "me.Beta");
    }

    [Fact]
    public async Task AFreeAccountOpensThePageAndTheDownloadedFileIsMatchedByItsName()
    {
        using var rig = new Rig(premium: false);
        await rig.StartAsync();
        rig.Service.AddCollection(await rig.Service.LookUpCollectionAsync("abc123"), false);
        await rig.WaitAsync(x => x.GetProperty("items").GetArrayLength() == 1 && x.GetProperty("phase").GetString() == "Review");
        Assert.NotEqual("Auto", rig.Snapshot().GetProperty("items")[0].GetProperty("offers")[0].GetProperty("mode").GetString());

        _ = rig.Service.ApplyAsync(new[] { "new:nexus:200" });
        await rig.WaitAsync(x => x.GetProperty("items")[0].GetProperty("status").GetString() == "WaitingForYou");
        Assert.Contains(rig.Opened, u => u.Contains("/mods/200"));

        // the person downloads in the browser; Nexus names the file with the mod's ID
        var file = Zips.Build(rig.Dir, "Beta-200-2-0-0-1752000000.zip", ("Beta/manifest.json", Zips.Manifest("me.Beta", "2.0.0", "Beta", ", \"UpdateKeys\": [\"Nexus:200\"]")), ("Beta/Beta.dll", "x"));
        await rig.Service.HandleArchiveAsync(file, "your browser download", CancellationToken.None);

        await rig.WaitAsync(x => x.GetProperty("phase").GetString() == "Finished");
        Assert.True(File.Exists(Path.Combine(rig.Mods, "Beta", "manifest.json")));
    }

    [Fact]
    public async Task StoppingTheInstallsRemovesTheOffersButKeepsTheProfile()
    {
        using var rig = new Rig(premium: true);
        await rig.StartAsync();
        rig.Service.AddCollection(await rig.Service.LookUpCollectionAsync("abc123"), false);
        await rig.WaitAsync(x => x.GetProperty("items").GetArrayLength() == 1 && x.GetProperty("phase").GetString() == "Review");

        Assert.Null(rig.Service.StopInstalling("Cozy Valley").Error);
        await rig.WaitAsync(x => x.GetProperty("items").GetArrayLength() == 0 && x.GetProperty("phase").GetString() == "Review");
        Assert.False(rig.Profile().GetProperty("pending").GetBoolean());
        Assert.Equal("Cozy Valley", rig.Profile().GetProperty("name").GetString());
    }

    [Fact]
    public async Task AddingTheSameCollectionTwiceMakesASecondProfile()
    {
        using var rig = new Rig(premium: true);
        await rig.StartAsync();
        var collection = await rig.Service.LookUpCollectionAsync("abc123");
        rig.Service.AddCollection(collection, false);
        rig.Service.AddCollection(collection, true);
        Assert.Equal(new[] { "Cozy Valley", "Cozy Valley (2)" }, rig.Service.Profiles.All().Select(p => p.Name));
    }
}
