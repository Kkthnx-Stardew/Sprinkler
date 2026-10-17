using System.Text.Json;
using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

public class RemoveModTests
{
    private static string LatestHistoryId(ProfileServiceTests.Rig rig) =>
        rig.Snapshot().GetProperty("history")[0].GetProperty("id").GetString()!;

    [Fact]
    public async Task RemovingBacksUpFirstAndUndoBringsTheModBackWithItsSettings()
    {
        using var rig = new ProfileServiceTests.Rig();
        File.WriteAllText(Path.Combine(rig.Mods, "C", "config.json"), "{\"mine\":true}");
        await rig.StartAsync();

        var (error, dependents) = await rig.Service.RemoveModAsync("me.C", force: false);

        Assert.Null(error);
        Assert.Null(dependents);
        Assert.False(Directory.Exists(Path.Combine(rig.Mods, "C")));
        var entry = rig.Snapshot().GetProperty("history")[0];
        Assert.True(entry.GetProperty("removed").GetBoolean());
        Assert.True(entry.GetProperty("canUndo").GetBoolean());
        Assert.Equal("Gamma", entry.GetProperty("name").GetString());
        Assert.Single(Directory.GetFiles(rig.Dir.Combine("backups"), "*.zip"));

        await rig.WaitForReviewAsync();
        Assert.Null(await rig.Service.UndoAsync(LatestHistoryId(rig)));

        Assert.Equal("{\"mine\":true}", File.ReadAllText(Path.Combine(rig.Mods, "C", "config.json")));
        Assert.True(File.Exists(Path.Combine(rig.Mods, "C", "manifest.json")));
        Assert.True(rig.Snapshot().GetProperty("history")[0].GetProperty("undone").GetBoolean());
    }

    [Fact]
    public async Task ModsThatOthersNeedAreNamedBeforeAnythingIsDeleted()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();

        var (error, dependents) = await rig.Service.RemoveModAsync("me.A", force: false);

        Assert.Null(error);
        Assert.Equal(new[] { "Beta" }, dependents);
        Assert.True(Directory.Exists(Path.Combine(rig.Mods, "A")));
        Assert.Empty(rig.Snapshot().GetProperty("history").EnumerateArray());

        var forced = await rig.Service.RemoveModAsync("me.A", force: true);
        Assert.Null(forced.Error);
        Assert.False(Directory.Exists(Path.Combine(rig.Mods, "A")));
    }

    [Fact]
    public async Task ASwitchedOffModCanBeRemovedAndRestoredStillSwitchedOff()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        await rig.Service.SetModEnabledAsync("me.C", false, false);
        await rig.WaitForReviewAsync();

        Assert.Null((await rig.Service.RemoveModAsync("me.C", false)).Error);
        Assert.DoesNotContain(".C", rig.Folders());
        await rig.WaitForReviewAsync();
        Assert.Null(await rig.Service.UndoAsync(LatestHistoryId(rig)));
        Assert.Contains(".C", rig.Folders());
    }

    [Fact]
    public async Task UnknownAndBundledModsAreRefusedPolitely()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        Assert.NotNull((await rig.Service.RemoveModAsync("no.such.mod", false)).Error);
        Assert.Equal(new[] { "A", "B", "C" }, rig.Folders());
    }

    [Fact]
    public void TheInstallerRefusesFoldersOutsideTheModsFolder()
    {
        using var d = new TempDir();
        d.Write("Mods/A/manifest.json", "{}");
        d.Write("Elsewhere/B/manifest.json", "{}");
        var installer = new Installer(new HistoryStore(d.Combine("h.json"), d.Combine("backups")), new SettingsStore(d.Combine("s.json")));

        Assert.Throws<InstallException>(() => installer.Remove(d.Combine("Elsewhere", "B"), d.Combine("Mods"), "x.B", "B", "1"));
        Assert.Throws<InstallException>(() => installer.Remove(d.Combine("Mods"), d.Combine("Mods"), "x.all", "All", "1"));
        Assert.True(Directory.Exists(d.Combine("Elsewhere", "B")));
        Assert.True(Directory.Exists(d.Combine("Mods", "A")));
    }
}

public class ModNoteTests
{
    [Fact]
    public async Task ANoteIsKeptTrimmedReplacedAndRemoved()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();

        rig.Service.SetModNote("me.A", "  needs the 1.2 patch  ");
        var notes = rig.Snapshot().GetProperty("modNotes");
        Assert.Equal("needs the 1.2 patch", notes.GetProperty("me.A").GetString());

        rig.Service.SetModNote("ME.A", "replaced"); // same mod, different capitalization
        notes = rig.Snapshot().GetProperty("modNotes");
        Assert.Equal(1, notes.EnumerateObject().Count());
        Assert.Equal("replaced", notes.EnumerateObject().Single().Value.GetString());

        rig.Service.SetModNote("me.A", "   ");
        Assert.Empty(rig.Snapshot().GetProperty("modNotes").EnumerateObject());
    }

    [Fact]
    public async Task NotesAreCappedAndSurviveARestart()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        rig.Service.SetModNote("me.B", new string('x', 500));
        Assert.Equal(300, rig.Snapshot().GetProperty("modNotes").GetProperty("me.B").GetString()!.Length);

        var reloaded = new SettingsStore(rig.Dir.Combine("settings.json"));
        Assert.Equal(300, reloaded.Data.ModNotes["me.B"].Length);
    }
}

public class EndorsementTests
{
    private static ProfileServiceTests.Rig Connected(Func<string, System.Net.HttpListenerContext, byte[]?> nexus)
    {
        var rig = new ProfileServiceTests.Rig();
        rig.Web.Handler = (path, ctx) =>
        {
            if (path == "/smapi")
                return FakeWeb.Json("[]");
            if (path == "/nexus/users/validate.json")
                return FakeWeb.Json("""{"name":"Tester","is_premium":false}""");
            return nexus(path, ctx);
        };
        return rig;
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < end && !done())
            await Task.Delay(50);
        Assert.True(done(), "Condition not met in time.");
    }

    [Fact]
    public async Task OnlyStardewEndorsementsAreLoaded()
    {
        using var rig = Connected((path, _) => path == "/nexus/user/endorsements.json"
            ? FakeWeb.Json("""[{"mod_id":5,"domain_name":"stardewvalley","status":"Endorsed"},{"mod_id":6,"domain_name":"stardewvalley","status":"Abstained"},{"mod_id":9,"domain_name":"skyrimspecialedition","status":"Endorsed"}]""")
            : null);
        await rig.StartAsync();

        Assert.Null(await rig.Service.ConnectNexusAsync(new string('k', 32)));
        await WaitUntilAsync(() => rig.Snapshot().GetProperty("endorsed").GetArrayLength() > 0);

        Assert.Equal(new long[] { 5 }, rig.Snapshot().GetProperty("endorsed").EnumerateArray().Select(e => e.GetInt64()));
    }

    [Fact]
    public async Task EndorsingAndTakingItBackUpdateTheList()
    {
        var body = "";
        using var rig = Connected((path, ctx) =>
        {
            if (path == "/nexus/user/endorsements.json")
                return FakeWeb.Json("[]");
            if (path == "/nexus/games/stardewvalley/mods/6/endorse.json")
            {
                body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                return FakeWeb.Json("""{"status":"ENDORSED","message":"ok"}""");
            }
            if (path == "/nexus/games/stardewvalley/mods/6/abstain.json")
                return FakeWeb.Json("""{"status":"ABSTAINED","message":"ok"}""");
            return null;
        });
        await rig.StartAsync();
        Assert.Null(await rig.Service.ConnectNexusAsync(new string('k', 32)));

        var endorsed = await rig.Service.SetEndorsementAsync(6, true, "2.4.0");
        Assert.True(endorsed.Ok);
        Assert.Contains("\"Version\":\"2.4.0\"", body);
        Assert.Contains(6L, rig.Snapshot().GetProperty("endorsed").EnumerateArray().Select(e => e.GetInt64()));

        var removed = await rig.Service.SetEndorsementAsync(6, false, "2.4.0");
        Assert.True(removed.Ok);
        Assert.DoesNotContain(6L, rig.Snapshot().GetProperty("endorsed").EnumerateArray().Select(e => e.GetInt64()));
    }

    [Theory]
    [InlineData("TOO_SOON_AFTER_DOWNLOAD", "for a while")]
    [InlineData("NOT_DOWNLOADED_MOD", "downloaded")]
    [InlineData("IS_OWN_MOD", "your own mod")]
    [InlineData("SOMETHING_NEW", "did not accept")]
    public async Task NexusRefusalsAreExplainedInPlainWords(string code, string phrase)
    {
        using var rig = Connected((path, _) => path.EndsWith("/endorse.json") ? FakeWeb.Json($$"""{"status":"ERROR","message":"{{code}}"}""") : path == "/nexus/user/endorsements.json" ? FakeWeb.Json("[]") : null);
        await rig.StartAsync();
        Assert.Null(await rig.Service.ConnectNexusAsync(new string('k', 32)));

        var result = await rig.Service.SetEndorsementAsync(7, true, "1.0.0");

        Assert.False(result.Ok);
        Assert.Contains(phrase, result.Message);
        Assert.Empty(rig.Snapshot().GetProperty("endorsed").EnumerateArray());
    }

    [Fact]
    public async Task WithoutNexusConnectedItAsksToConnect()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        var result = await rig.Service.SetEndorsementAsync(5, true, "1.0.0");
        Assert.False(result.Ok);
        Assert.Contains("Connect", result.Message);
    }
}
