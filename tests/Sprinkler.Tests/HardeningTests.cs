using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>Files and answers that are damaged, odd or hostile must end as a message, never as a crash or a lost setting.</summary>
public class HardeningTests
{
    [Theory]
    [InlineData("notes.7z", "this is plain text that only has an archive name")]
    [InlineData("empty.7z", "")]
    [InlineData("broken.rar", "Rar!\u001a\u0007\u0001\u0000 cut off")]
    public void AMislabelledArchiveIsReportedAsUnreadable(string name, string content)
    {
        using var d = new TempDir();
        var path = d.Write(name, content);
        Assert.Throws<ArchiveException>(() => ArchiveFile.Open(path));
    }

    [Fact]
    public void AnOversizedManifestIsIgnoredInsteadOfReadIntoMemory()
    {
        using var d = new TempDir();
        var zip = Zips.Build(d, "big.zip",
            ("Mod/manifest.json", new string(' ', 2 * 1024 * 1024) + Zips.Manifest("a.b", "1.0.0")),
            ("Other/manifest.json", Zips.Manifest("c.d", "2.0.0")));
        using var archive = ArchiveFile.Open(zip);
        var found = Assert.Single(ArchiveInspector.FindMods(archive));
        Assert.Equal("c.d", found.Manifest.UniqueId);
    }

    [Fact]
    public void ReadAllStopsAtTheLimitEvenWhenTheHeaderSaysLess()
    {
        using var d = new TempDir();
        var zip = Zips.Build(d, "x.zip", ("a.txt", new string('x', 5000)));
        using var archive = ArchiveFile.Open(zip);
        Assert.Throws<ArchiveException>(() => archive.ReadAll("a.txt", maxBytes: 1000));
        Assert.Equal(5000, archive.ReadAll("a.txt").Length);
    }

    [Fact]
    public void ADamagedSettingsFileIsKeptBesideTheNewOne()
    {
        using var d = new TempDir();
        var file = d.Write("settings.json", "{ this is not json");
        var store = new SettingsStore(file);

        Assert.True(store.Data.CheckAppUpdates);
        Assert.True(File.Exists(file + ".unreadable"));
        Assert.Equal("{ this is not json", File.ReadAllText(file + ".unreadable"));
    }

    [Fact]
    public void ADamagedHistoryFileIsKeptBesideTheNewOne()
    {
        using var d = new TempDir();
        var file = d.Write("history.json", "[ nope");
        var history = new HistoryStore(file, d.Combine("backups"));

        Assert.Empty(history.All());
        Assert.True(File.Exists(file + ".unreadable"));
    }

    [Fact]
    public void TheNexusKeyIsReadFromTheStoreOnceNotForEveryRequest()
    {
        using var d = new TempDir();
        var store = new SettingsStore(d.Combine("settings.json"));
        store.NexusKey = "a-test-key-that-is-long-enough";

        Assert.Equal("a-test-key-that-is-long-enough", store.NexusKey);
        // changing it through the store is seen at once, and clearing it too
        store.NexusKey = "another-test-key-long-enough";
        Assert.Equal("another-test-key-long-enough", store.NexusKey);
        store.NexusKey = null;
        Assert.Null(store.NexusKey);
    }

    [Fact]
    public void OldLeftoversInAScratchFolderAreRemovedAndRecentOnesStay()
    {
        using var d = new TempDir();
        var old = d.Write("staging/old.zip", "x");
        var fresh = d.Write("staging/fresh.zip", "x");
        d.Write("staging/dl-1234/inner.zip", "x");
        var oldTime = DateTime.UtcNow.AddDays(-2);
        foreach (var path in new[] { old, d.Combine("staging", "dl-1234", "inner.zip") })
        {
            File.SetCreationTimeUtc(path, oldTime);
            File.SetLastWriteTimeUtc(path, oldTime);
        }
        Directory.SetCreationTimeUtc(d.Combine("staging", "dl-1234"), oldTime);
        Directory.SetLastWriteTimeUtc(d.Combine("staging", "dl-1234"), oldTime);

        var removed = Cleanup.PurgeOlderThan(d.Combine("staging"), TimeSpan.FromHours(6));

        Assert.Equal(2, removed);
        Assert.False(File.Exists(old));
        Assert.False(Directory.Exists(d.Combine("staging", "dl-1234")));
        Assert.True(File.Exists(fresh));
        Assert.Equal(0, Cleanup.PurgeOlderThan(d.Combine("nothing-here"), TimeSpan.Zero));
    }

    [Fact]
    public async Task ASecondCopyOfTheAppHandsAnNxmLinkToTheRunningOne()
    {
        var name = "Sprinkler-test-" + Guid.NewGuid().ToString("N")[..8];
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var channel = new Sprinkler.InstanceChannel(name);
        channel.MessageReceived += m => received.TrySetResult(m);
        channel.Start();

        const string link = "nxm://stardewvalley/mods/17272/files/123?key=abc&expires=1&user_id=5";
        var sent = false;
        for (var i = 0; i < 20 && !sent; i++)
            sent = await Sprinkler.InstanceChannel.SendAsync("nxm " + link, 500, name);

        Assert.True(sent);
        Assert.Equal("nxm " + link, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await Sprinkler.InstanceChannel.SendAsync("activate", 200, name + "-nobody-listens"));
    }

    [Fact]
    public void ModsThatNeedAnotherModAreListedByName()
    {
        var core = new ModInfo { UniqueId = "me.Core", Name = "Core" };
        var needy = new ModInfo { UniqueId = "me.Needy", Name = "Needy", Dependencies = new[] { new ModDependency("me.Core", null, true) } };
        var pack = new ModInfo { UniqueId = "me.Pack", Name = "Pack", ContentPackFor = "me.Core" };
        var optional = new ModInfo { UniqueId = "me.Opt", Name = "Opt", Dependencies = new[] { new ModDependency("me.Core", null, false) } };
        var off = new ModInfo { UniqueId = "me.Off", Name = "Off", Disabled = true, ContentPackFor = "me.Core" };

        var names = UpdateService.DependentsOf(core, new[] { core, needy, pack, optional, off });

        Assert.Equal(new[] { "Needy", "Pack" }, names.OrderBy(x => x).ToArray());
    }
}
