using System.Text.Json;
using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

public class ProfileStoreTests
{
    [Fact]
    public void NamesAreTidiedAndMustBeUnique()
    {
        using var d = new TempDir();
        var store = new ProfileStore(d.Combine("profiles.json"));
        store.Add(new Profile { Name = "  Co-op   farm " }, makeActive: true);

        Assert.Equal("Co-op farm", store.All().Single().Name);
        Assert.Equal("Co-op farm", store.ActiveName);
        Assert.Throws<ProfileException>(() => store.Add(new Profile { Name = "co-OP FARM" }, false));
        Assert.Throws<ProfileException>(() => store.CleanName("   "));
        Assert.Throws<ProfileException>(() => store.CleanName(new string('x', ProfileStore.MaxNameLength + 1)));
        Assert.Throws<ProfileException>(() => store.CleanName("bad\u0007name"));
    }

    [Fact]
    public void RenamingKeepsTheActiveProfileAndDeletingClearsIt()
    {
        using var d = new TempDir();
        var store = new ProfileStore(d.Combine("profiles.json"));
        store.Add(new Profile { Name = "One" }, makeActive: true);
        store.Add(new Profile { Name = "Two" }, makeActive: false);

        store.Rename("One", "Uno");
        Assert.Equal("Uno", store.ActiveName);
        Assert.Throws<ProfileException>(() => store.Rename("Uno", "two")); // taken, ignoring case
        store.Rename("Uno", "UNO"); // only the capitalization changes: allowed
        Assert.Equal("UNO", store.ActiveName);

        store.Delete("UNO");
        Assert.Null(store.ActiveName);
        Assert.Equal(new[] { "Two" }, store.All().Select(p => p.Name));
    }

    [Fact]
    public void EverythingSurvivesAClosedApp()
    {
        using var d = new TempDir();
        var file = d.Combine("profiles.json");
        var first = new ProfileStore(file);
        first.KeepConfigs = true;
        first.Add(new Profile { Name = "Saved", Enabled = { "a.b", "c.d" }, Configs = { ["a.b"] = "{\"x\":1}" } }, makeActive: true);

        var second = new ProfileStore(file);
        Assert.True(second.KeepConfigs);
        Assert.Equal("Saved", second.ActiveName);
        var p = second.Find("saved")!;
        Assert.Equal(new[] { "a.b", "c.d" }, p.Enabled);
        Assert.Equal("{\"x\":1}", p.Configs["A.B"]); // lookups ignore case
    }

    [Fact]
    public void AnUnreadableFileIsKeptNotOverwritten()
    {
        using var d = new TempDir();
        var file = d.Write("profiles.json", "{ this is not json");
        var store = new ProfileStore(file);
        Assert.Empty(store.All());
        Assert.True(File.Exists(file + ".unreadable"));
        store.Add(new Profile { Name = "Fresh" }, false);
        Assert.Equal("{ this is not json", File.ReadAllText(file + ".unreadable"));
    }

    [Fact]
    public void CopiesDoNotShareTheirLists()
    {
        var a = new Profile { Name = "A", Enabled = { "x" } };
        var b = a.Copy("B");
        b.Enabled.Add("y");
        Assert.Equal(new[] { "x" }, a.Enabled);
    }
}

public class SharedProfileTests
{
    [Fact]
    public void ARoundTripKeepsTheModList()
    {
        var shared = new PortableProfile(PortableProfile.FormatName, 1, "Friends", DateTime.UtcNow, new()
        {
            new PortableMod("a.b", "Alpha", "1.0.0", 15, null),
            new PortableMod("c.d", "Gamma", null, null, "me/gamma"),
        });
        var back = PortableProfile.Parse(shared.ToJson());
        Assert.Equal("Friends", back.Name);
        Assert.Equal(new[] { "a.b", "c.d" }, back.Mods.Select(m => m.UniqueId));
        Assert.Equal(15, back.Mods[0].NexusId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"something-else\",\"version\":1,\"name\":\"x\",\"mods\":[]}")]
    [InlineData("[1,2,3]")]
    public void OnlyRealProfilesAreAccepted(string text) =>
        Assert.Throws<ProfileException>(() => PortableProfile.Parse(text));

    [Fact]
    public void ANewerFormatSaysSoInsteadOfGuessing()
    {
        var ex = Assert.Throws<ProfileException>(() => PortableProfile.Parse("{\"format\":\"sprinkler-profile\",\"version\":9,\"name\":\"x\",\"mods\":[]}"));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void JunkEntriesAreDroppedAndDuplicatesMerged()
    {
        var json = """
            {"format":"sprinkler-profile","version":1,"name":"x","exported":"2026-10-17T00:00:00Z","mods":[
              {"uniqueId":"a.b","name":"Alpha"},{"uniqueId":"A.B","name":"Again"},{"uniqueId":"","name":"Nothing"},
              {"uniqueId":"c\u0001d","name":"Bad"},{"uniqueId":"e.f","name":"  "}]}
            """;
        var p = PortableProfile.Parse(json);
        Assert.Equal(new[] { "a.b", "e.f" }, p.Mods.Select(m => m.UniqueId));
        Assert.Equal("e.f", p.Mods[1].Name); // a blank name falls back to the ID
    }

    [Fact]
    public void ATooLargeFileIsRefused() =>
        Assert.Throws<ProfileException>(() => PortableProfile.Parse(new string(' ', PortableProfile.MaxBytes + 1)));
}

/// <summary>The service against a real folder of fake mods: three mods, B needs A.</summary>
public class ProfileServiceTests
{
    internal sealed class Rig : IDisposable
    {
        public TempDir Dir = new();
        public FakeWeb Web = new();
        public UpdateService Service;
        public string Mods => Dir.Combine("game", "Mods");

        public Rig(string smapi = "[]", bool keepConfigs = false)
        {
            Directory.CreateDirectory(Mods);
            File.WriteAllText(Dir.Combine("game", "Stardew Valley.dll"), "");
            Dir.Write("game/Mods/A/manifest.json", Zips.Manifest("me.A", "1.0.0", "Alpha"));
            Dir.Write("game/Mods/A/config.json", "{\"level\":1}");
            Dir.Write("game/Mods/B/manifest.json", Zips.Manifest("me.B", "1.0.0", "Beta", ", \"Dependencies\": [ { \"UniqueID\": \"me.A\" } ]"));
            Dir.Write("game/Mods/C/manifest.json", Zips.Manifest("me.C", "1.0.0", "Gamma"));

            Web.Handler = (path, _) => path == "/smapi" ? FakeWeb.Json(smapi) : null;
            var settings = new SettingsStore(Dir.Combine("settings.json"));
            settings.Data.DownloadsPath = Dir.Combine("downloads");
            Directory.CreateDirectory(Dir.Combine("downloads"));
            var profiles = new ProfileStore(Dir.Combine("profiles.json")) { KeepConfigs = keepConfigs };
            Service = new UpdateService(settings, new HistoryStore(Dir.Combine("history.json"), Dir.Combine("backups")), _ => { }, profiles);
        }

        public async Task StartAsync()
        {
            Assert.Null(await Service.SetGameFolderAsync(Dir.Combine("game")));
            await WaitForReviewAsync();
        }

        public JsonElement Snapshot() => JsonSerializer.SerializeToElement(Service.Snapshot(), AppEnvironment.Json);

        public async Task WaitForReviewAsync()
        {
            var end = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < end)
            {
                if (Snapshot().GetProperty("phase").GetString() == "Review")
                    return;
                await Task.Delay(50);
            }
            throw new TimeoutException("Never reached Review: " + Snapshot());
        }

        public string[] Folders() => Directory.GetDirectories(Mods).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;

        public JsonElement Profiles() => Snapshot().GetProperty("profiles");

        public void Dispose()
        {
            Service.Dispose();
            Web.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task ASavedProfileRemembersWhatIsOnAndBecomesActive()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        var result = rig.Service.CreateProfile("Everything");
        Assert.Null(result.Error);

        var p = rig.Profiles();
        Assert.Equal("Everything", p.GetProperty("active").GetString());
        Assert.False(p.GetProperty("modified").GetBoolean());
        Assert.Equal(3, p.GetProperty("profiles")[0].GetProperty("modCount").GetInt32());
    }

    [Fact]
    public async Task SwitchingOnOrOffAfterwardsMarksTheProfileModifiedUntilSaved()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Service.CreateProfile("Everything");

        Assert.Null((await rig.Service.SetModEnabledAsync("me.C", false, false)).Error);
        await rig.WaitForReviewAsync();
        Assert.True(rig.Profiles().GetProperty("modified").GetBoolean());

        Assert.Null(rig.Service.SaveActiveProfile().Error);
        Assert.False(rig.Profiles().GetProperty("modified").GetBoolean());
        Assert.Equal(2, rig.Profiles().GetProperty("profiles")[0].GetProperty("modCount").GetInt32());
    }

    [Fact]
    public async Task SwitchingToAProfileRenamesFoldersToMatch()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Service.CreateProfile("Everything");
        await rig.Service.SetModEnabledAsync("me.C", false, false);
        await rig.WaitForReviewAsync();
        await rig.Service.SetModEnabledAsync("me.B", false, true);
        await rig.WaitForReviewAsync();
        rig.Service.SaveActiveProfile(); // "Everything" now means only A
        rig.Service.CreateProfile("Only A");
        Assert.Equal(new[] { ".B", ".C", "A" }, rig.Folders());

        // the first profile was saved with all three before the changes above were saved over it, so rebuild it by hand
        rig.Service.Profiles.Update("Everything", p => p.Enabled = new() { "me.A", "me.B", "me.C" });
        var result = rig.Service.ApplyProfile("Everything");

        Assert.Null(result.Error);
        Assert.Equal(new[] { "A", "B", "C" }, rig.Folders());
        Assert.Contains("2 turned on", result.Message);
        Assert.Equal("Everything", rig.Profiles().GetProperty("active").GetString());

        Assert.Null(rig.Service.ApplyProfile("Only A").Error);
        Assert.Equal(new[] { ".B", ".C", "A" }, rig.Folders());
    }

    [Fact]
    public async Task WhatAModNeedsIsTurnedOnToo()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Service.Profiles.Add(new Profile { Name = "Just B", Enabled = { "me.B" } }, false);

        var result = rig.Service.ApplyProfile("Just B");

        Assert.Equal(new[] { ".C", "A", "B" }, rig.Folders()); // B needs A, so A stays on
        Assert.Contains("Alpha", result.Message);
    }

    [Fact]
    public async Task EachProfileCanKeepItsOwnSettings()
    {
        using var rig = new Rig(keepConfigs: true);
        await rig.StartAsync();
        var config = Path.Combine(rig.Mods, "A", "config.json");

        rig.Service.CreateProfile("Easy"); // config.json says level 1
        File.WriteAllText(config, "{\"level\":9}");
        rig.Service.CreateProfile("Hard"); // level 9

        Assert.Null(rig.Service.ApplyProfile("Easy").Error);
        Assert.Equal("{\"level\":1}", File.ReadAllText(config));

        File.WriteAllText(config, "{\"level\":2}"); // tweaked while on Easy
        Assert.Null(rig.Service.ApplyProfile("Hard").Error);
        Assert.Equal("{\"level\":9}", File.ReadAllText(config));

        Assert.Null(rig.Service.ApplyProfile("Easy").Error);
        Assert.Equal("{\"level\":2}", File.ReadAllText(config)); // the tweak was remembered when leaving
    }

    [Fact]
    public async Task SettingsAreLeftAloneUnlessThePersonAskedForThat()
    {
        using var rig = new Rig(keepConfigs: false);
        await rig.StartAsync();
        var config = Path.Combine(rig.Mods, "A", "config.json");
        rig.Service.CreateProfile("One");
        File.WriteAllText(config, "{\"level\":5}");
        rig.Service.CreateProfile("Two");
        rig.Service.ApplyProfile("One");
        Assert.Equal("{\"level\":5}", File.ReadAllText(config));
    }

    [Fact]
    public async Task LeavingWithNoProfileStillSavesTheCurrentSetupFirst()
    {
        using var rig = new Rig(keepConfigs: true);
        await rig.StartAsync();
        var config = Path.Combine(rig.Mods, "A", "config.json");
        rig.Service.Profiles.Add(new Profile { Name = "Target", Enabled = { "me.A" }, Configs = { ["me.A"] = "{\"level\":7}" } }, false);
        File.WriteAllText(config, "{\"level\":3}");

        rig.Service.ApplyProfile("Target");

        Assert.Equal("{\"level\":7}", File.ReadAllText(config));
        var saved = rig.Service.Profiles.All().Single(p => p.Name.StartsWith("Previous setup"));
        Assert.Equal("{\"level\":3}", saved.Configs["me.A"]); // nothing was lost
    }

    [Fact]
    public async Task ASharedProfileListsModsThatAreNotInstalledAndTheyBecomeInstalls()
    {
        const string smapi = """[{"id":"ghost.Mod","suggestedUpdate":null,"metadata":{"name":"Ghost","nexusID":77,"main":{"version":"2.0.0"}},"errors":[]}]""";
        using var rig = new Rig(smapi);
        await rig.StartAsync();

        var shared = new PortableProfile(PortableProfile.FormatName, 1, "Friends", DateTime.UtcNow, new()
        {
            new PortableMod("me.A", "Alpha", "1.0.0", null, null),
            new PortableMod("ghost.Mod", "Ghost", "2.0.0", 77, null),
        }).ToJson();

        var imported = rig.Service.ImportProfile(shared);
        Assert.Null(imported.Error);
        Assert.Equal(new[] { "ghost.Mod" }, imported.Missing);
        Assert.Equal(1, rig.Profiles().GetProperty("profiles")[0].GetProperty("missingCount").GetInt32());

        var applied = rig.Service.ApplyProfile("Friends");
        Assert.Contains("not installed", applied.Message);
        Assert.Equal(new[] { ".B", ".C", "A" }, rig.Folders());

        await rig.WaitForReviewAsync();
        var end = DateTime.UtcNow.AddSeconds(15);
        JsonElement items = default;
        while (DateTime.UtcNow < end)
        {
            await rig.WaitForReviewAsync();
            items = rig.Snapshot().GetProperty("items");
            if (items.GetArrayLength() > 0)
                break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        var item = Assert.Single(items.EnumerateArray());
        Assert.True(item.GetProperty("isNew").GetBoolean());
        Assert.Equal("Ghost", item.GetProperty("name").GetString());
        Assert.Contains("Friends", item.GetProperty("why").GetString());
    }

    [Fact]
    public async Task ImportingTwiceMakesASecondProfileInsteadOfOverwriting()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var json = new PortableProfile(PortableProfile.FormatName, 1, "Same", DateTime.UtcNow, new()).ToJson();
        Assert.Null(rig.Service.ImportProfile(json).Error);
        Assert.Null(rig.Service.ImportProfile(json).Error);
        Assert.Equal(new[] { "Same", "Same (2)" }, rig.Service.Profiles.All().Select(p => p.Name));
        Assert.NotNull(rig.Service.ImportProfile("garbage").Error);
    }

    [Fact]
    public async Task ExportListsModsNamesAndWhereToGetThem()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.Combine(rig.Mods, "N"));
        File.WriteAllText(Path.Combine(rig.Mods, "N", "manifest.json"), Zips.Manifest("me.N", "3.1.0", "Nexus mod", ", \"UpdateKeys\": [\"Nexus:1915\"]"));
        await rig.StartAsync();
        rig.Service.CreateProfile("Share me");

        var shared = PortableProfile.Parse(rig.Service.ExportProfile("Share me"));

        var n = shared.Mods.Single(m => m.UniqueId == "me.N");
        Assert.Equal("Nexus mod", n.Name);
        Assert.Equal("3.1.0", n.Version);
        Assert.Equal(1915, n.NexusId);
        Assert.DoesNotContain(rig.Dir.Path, rig.Service.ExportProfile("Share me")); // no folder paths leak into a shared file
    }

    [Fact]
    public async Task DeletingOrRenamingNeverTouchesTheMods()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Service.CreateProfile("Temp");
        Assert.Null(rig.Service.RenameProfile("Temp", "Better").Error);
        Assert.NotNull(rig.Service.RenameProfile("Better", "  ").Error);
        Assert.Null(rig.Service.CopyProfile("Better", "Copy").Error);
        Assert.Null(rig.Service.DeleteProfile("Better").Error);
        Assert.Equal(new[] { "A", "B", "C" }, rig.Folders());
        Assert.NotNull(rig.Service.ApplyProfile("Nope").Error);
    }
}
