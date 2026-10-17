using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

public class ModArtTests
{
    private static readonly byte[] Png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4 };

    private static ModInfo Mod(params string[] keys) => new() { UniqueId = "x.y", Name = "X", UpdateKeys = keys };

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return;
            Thread.Sleep(25);
        }
        throw new TimeoutException("The picture never arrived.");
    }

    private static string ModJson(string pictureUrl) =>
        $$"""{ "name": "Thing", "summary": "Does <b>a thing</b>.", "picture_url": "{{pictureUrl}}", "author": "Someone", "mod_downloads": 1234, "endorsement_count": 56 }""";

    [Fact]
    public void KeysPreferNexusThenGitHubAndIgnoreTheRest()
    {
        Assert.Equal("nexus:1915", ModArtStore.KeyFor(Mod("Nexus:1915@main")));
        Assert.Equal("nexus:1915", ModArtStore.KeyFor(Mod("GitHub:me/repo", "Nexus:1915")));
        Assert.Equal("github:Pathoschild", ModArtStore.KeyFor(Mod("GitHub:Pathoschild/StardewMods")));
        Assert.Null(ModArtStore.KeyFor(Mod("CurseForge:1234")));
        Assert.Null(ModArtStore.KeyFor(Mod("Nexus:abc")));
        Assert.Null(ModArtStore.KeyFor(Mod()));
        Assert.Equal("nexus:7", ModArtStore.KeyFor(7, "me/repo"));
        Assert.Equal("github:me", ModArtStore.KeyFor(null, "me/repo"));
        Assert.Null(ModArtStore.KeyFor(null, null));
    }

    [Fact]
    public void OnlyRealImagesAreKept()
    {
        Assert.True(ModArtStore.LooksLikeImage(Png));
        Assert.True(ModArtStore.LooksLikeImage(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.True(ModArtStore.LooksLikeImage(System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ")));
        Assert.False(ModArtStore.LooksLikeImage(System.Text.Encoding.UTF8.GetBytes("<html><body>Not found</body></html>")));
        Assert.False(ModArtStore.LooksLikeImage(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ImageAddressesMustBeHttpsOnTheRightSite()
    {
        Assert.True(ModArtStore.IsAllowedImageUrl("https://staticdelivery.nexusmods.com/mods/1303/images/541-0.png", "nexusmods.com"));
        Assert.False(ModArtStore.IsAllowedImageUrl("http://staticdelivery.nexusmods.com/a.png", "nexusmods.com"));
        Assert.False(ModArtStore.IsAllowedImageUrl("https://evilnexusmods.com/a.png", "nexusmods.com"));
        Assert.False(ModArtStore.IsAllowedImageUrl("file:///C:/Windows/win.ini", "nexusmods.com"));
        Assert.False(ModArtStore.IsAllowedImageUrl(null, "nexusmods.com"));
    }

    [Fact]
    public void FetchesANexusPictureOnceAndRemembersIt()
    {
        using var web = new FakeWeb();
        using var dir = new TempDir();
        web.Handler = (path, _) =>
            path.EndsWith("/mods/1915.json") ? FakeWeb.Json(ModJson(web.Base + "/img/a.png"))
            : path == "/img/a.png" ? Png
            : null;
        var store = new ModArtStore(new NexusClient(() => "test-key"), dir.Path);
        var changed = 0;
        store.Changed += () => Interlocked.Increment(ref changed);

        store.Request("nexus:1915");
        WaitFor(() => store.PathFor("nexus:1915") is not null);

        var entry = store.Peek("nexus:1915")!;
        Assert.Equal("Does a thing.", entry.Summary);
        Assert.Equal("Someone", entry.Author);
        Assert.Equal(1234, entry.Downloads);
        Assert.Equal(Png, File.ReadAllBytes(store.PathFor("nexus:1915")!));
        Assert.True(changed >= 1);

        // a second request, even from a new store reading the same folder, asks the sites nothing
        var before = web.Requests.Count;
        store.Request("nexus:1915");
        new ModArtStore(new NexusClient(() => "test-key"), dir.Path).Request("nexus:1915");
        Thread.Sleep(300);
        Assert.Equal(before, web.Requests.Count);
    }

    [Fact]
    public void RemembersWhenAModHasNoPictureSoItIsNotAskedAgain()
    {
        using var web = new FakeWeb();
        using var dir = new TempDir();
        web.Handler = (_, _) => null; // 404 for everything
        var store = new ModArtStore(new NexusClient(() => "test-key"), dir.Path);

        store.Request("nexus:9");
        WaitFor(() => store.Peek("nexus:9") is not null);
        Assert.Null(store.PathFor("nexus:9"));

        var before = web.Requests.Count;
        store.Request("nexus:9");
        Thread.Sleep(300);
        Assert.Equal(before, web.Requests.Count);
    }

    [Fact]
    public void DoesNothingWithoutANexusKey()
    {
        using var web = new FakeWeb();
        using var dir = new TempDir();
        var store = new ModArtStore(new NexusClient(() => null), dir.Path);
        store.Request("nexus:1915");
        Thread.Sleep(400);
        Assert.Null(store.Peek("nexus:1915"));
        Assert.Empty(web.Requests);
    }

    [Fact]
    public void AnErrorPageIsNeverStoredAsAPicture()
    {
        using var web = new FakeWeb();
        using var dir = new TempDir();
        web.Handler = (path, _) =>
            path.EndsWith("/mods/5.json") ? FakeWeb.Json(ModJson(web.Base + "/img/bad.png"))
            : path == "/img/bad.png" ? System.Text.Encoding.UTF8.GetBytes("<html>Please log in to see this image</html>")
            : null;
        var store = new ModArtStore(new NexusClient(() => "test-key"), dir.Path);

        store.Request("nexus:5");
        WaitFor(() => store.Peek("nexus:5") is not null);

        Assert.Null(store.PathFor("nexus:5"));
        Assert.Equal("Does a thing.", store.Peek("nexus:5")!.Summary); // the text is still useful
        Assert.Empty(Directory.GetFiles(dir.Path, "*.img"));
    }

    [Fact]
    public void RefusesAPictureOnAnotherSiteEvenIfNexusSaysSo()
    {
        using var web = new FakeWeb();
        using var dir = new TempDir();
        Environment.SetEnvironmentVariable("SPRINKLER_ALLOW_LOCAL", null);
        try
        {
            web.Handler = (path, _) => path.EndsWith("/mods/6.json") ? FakeWeb.Json(ModJson("https://evil.example.com/a.png")) : null;
            var store = new ModArtStore(new NexusClient(() => "test-key"), dir.Path);
            store.Request("nexus:6");
            WaitFor(() => store.Peek("nexus:6") is not null);
            Assert.Null(store.PathFor("nexus:6"));
            Assert.DoesNotContain(web.Requests, r => r.Contains("evil"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPRINKLER_ALLOW_LOCAL", "1");
        }
    }

    [Fact]
    public void UsesTheAuthorsAvatarForGitHubMods()
    {
        using var web = new FakeWeb();
        using var dir = new TempDir();
        web.Handler = (path, _) => path == "/ghsite/Pathoschild.png" ? Png : null;
        var store = new ModArtStore(new NexusClient(() => null), dir.Path);

        store.Request("github:Pathoschild");
        WaitFor(() => store.PathFor("github:Pathoschild") is not null);
        Assert.Contains(web.Requests, r => r.EndsWith("/ghsite/Pathoschild.png"));
    }

    [Fact]
    public void ABadOwnerNameIsNotSentToAnyone()
    {
        using var web = new FakeWeb();
        using var dir = new TempDir();
        var store = new ModArtStore(new NexusClient(() => null), dir.Path);
        store.Request("github:../../etc");
        Thread.Sleep(400);
        Assert.Empty(web.Requests);
    }

    [Fact]
    public void ANexusRateLimitPausesTheFetching()
    {
        using var web = new FakeWeb();
        using var dir = new TempDir();
        web.Handler = (path, ctx) =>
        {
            ctx.Response.StatusCode = 429;
            return null;
        };
        var store = new ModArtStore(new NexusClient(() => "test-key"), dir.Path);

        store.Request("nexus:1");
        WaitFor(() => web.Requests.Count >= 1);
        Thread.Sleep(300);
        store.Request("nexus:2"); // paused: no second request
        Thread.Sleep(300);
        Assert.Single(web.Requests);
        Assert.Null(store.Peek("nexus:1"));
    }
}
