using StardewModUpdater.Core;
using Xunit;

namespace StardewModUpdater.Tests;

public class ModVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("2.0.0-beta.1", "2.0.0", -1)]
    [InlineData("2.0.0-beta.2", "2.0.0-beta.10", -1)]
    [InlineData("2.0.0-alpha", "2.0.0-beta", -1)]
    [InlineData("v1.5.0", "1.5.0", 0)]
    [InlineData("1.2.3.4", "1.2.3", 1)]
    public void Compares(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(ModVersion.Parse(a)!.CompareTo(ModVersion.Parse(b))));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.x.3")]
    public void RejectsGarbage(string text) => Assert.Null(ModVersion.Parse(text));

    [Fact]
    public void EqualVersionsHashTheSame() =>
        Assert.Equal(ModVersion.Parse("1.0")!.GetHashCode(), ModVersion.Parse("1.0.0")!.GetHashCode());

    [Fact]
    public void FindsVersionsInFileNames()
    {
        var all = ModVersion.FindAll("Stardew Valley Expanded 1.15.11 for game 1.6");
        Assert.Contains(all, v => v.ToString() == "1.15.11");
        Assert.DoesNotContain(ModVersion.FindAll("MyMod"), _ => true);
        Assert.Single(ModVersion.FindAll("SimpleLocationDisplay 2.0.1.zip"));
    }
}

public class ManifestTests
{
    [Fact]
    public void ReadsCommentsAndTrailingCommas()
    {
        const string text = """
        {
          // a comment
          "Name": "Thing", "Author": "me", "Version": "1.2.3", "UniqueID": "me.Thing",
          "UpdateKeys": [ "Nexus:123@main", "GitHub:me/thing", ],
          "ContentPackFor": { "UniqueID": "Pathoschild.ContentPatcher", "MinimumVersion": "2.0" },
          "Dependencies": [ { "UniqueID": "a.b", "IsRequired": false }, { "UniqueID": "c.d" } ],
        }
        """;
        var m = ManifestReader.Parse(System.Text.Encoding.UTF8.GetBytes(text), @"C:\Mods\Thing")!;
        Assert.Equal("me.Thing", m.UniqueId);
        Assert.Equal("Pathoschild.ContentPatcher", m.ContentPackFor);
        Assert.Equal(2, m.UpdateKeys.Length);
        Assert.False(m.Dependencies[0].Required);
        Assert.True(m.Dependencies[1].Required);
        Assert.Equal("Thing", m.FolderName == "Thing" ? m.Name : "");
    }

    [Fact]
    public void ReadsByteOrderMark()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(Zips.Manifest("a.b", "1.0"))).ToArray();
        Assert.Equal("a.b", ManifestReader.Parse(bytes, "x")!.UniqueId);
    }

    [Fact]
    public void RejectsManifestWithoutId() =>
        Assert.Null(ManifestReader.Parse("""{ "Name": "x", "Version": "1" }"""u8.ToArray(), "x"));

    [Fact]
    public void RejectsBrokenJson() => Assert.Null(ManifestReader.Parse("{ nope"u8.ToArray(), "x"));

    [Theory]
    [InlineData("Nexus:1915", "Nexus", "1915", null)]
    [InlineData("nexus:1915@main", "Nexus", "1915", "main")]
    [InlineData("GitHub:Pathoschild/SMAPI", "GitHub", "Pathoschild/SMAPI", null)]
    [InlineData("CurseForge:1613049", "CurseForge", "1613049", null)]
    public void ParsesUpdateKeys(string raw, string kind, string id, string? sub)
    {
        var k = UpdateKey.Parse(raw)!;
        Assert.Equal(kind, k.Site.ToString());
        Assert.Equal(id, k.Id);
        Assert.Equal(sub, k.Subkey);
    }

    [Theory]
    [InlineData("Chucklefish:1")]
    [InlineData("Nexus")]
    [InlineData("")]
    public void IgnoresUnknownKeys(string raw) => Assert.Null(UpdateKey.Parse(raw));
}

public class ScannerTests
{
    [Fact]
    public void FindsNestedAndDisabledMods()
    {
        using var d = new TempDir();
        d.Write("A/manifest.json", Zips.Manifest("a.a", "1.0"));
        d.Write("[Pack]/B/manifest.json", Zips.Manifest("b.b", "1.0"));
        d.Write(".Off/manifest.json", Zips.Manifest("c.c", "1.0"));
        d.Write("A/Inner/manifest.json", Zips.Manifest("inner.inner", "1.0"));
        d.Write("Console/manifest.json", Zips.Manifest("SMAPI.ConsoleCommands", "4.0"));

        var mods = ModScanner.Scan(d.Path);
        Assert.Equal(new[] { "a.a", "b.b", "c.c", "SMAPI.ConsoleCommands" }, mods.Select(m => m.UniqueId).OrderBy(x => x == "SMAPI.ConsoleCommands").ThenBy(x => x).ToArray());
        Assert.DoesNotContain(mods, m => m.UniqueId == "inner.inner");
        Assert.True(mods.Single(m => m.UniqueId == "c.c").Disabled);
        Assert.True(mods.Single(m => m.UniqueId == "SMAPI.ConsoleCommands").Bundled);
    }
}

public class NexusTests
{
    private static NexusClient.FileEntry F(long id, string cat, string ver, long ts, bool primary = false, string name = "f") =>
        new() { FileId = id, CategoryName = cat, Version = ver, ModVersion = ver, UploadedTimestamp = ts, IsPrimary = primary, Name = name };

    [Fact]
    public void PicksTheFileWithTheTargetVersion()
    {
        var files = new List<NexusClient.FileEntry>
        {
            F(1, "MAIN", "1.0.0", 100, true),
            F(2, "MAIN", "1.1.0", 200),
            F(3, "OPTIONAL", "1.1.0", 300),
            F(4, "OLD_VERSION", "1.1.0", 400),
        };
        Assert.Equal(2, NexusClient.Pick(files, "1.1.0", null)!.FileId);
    }

    [Fact]
    public void SubkeyBreaksTies()
    {
        var files = new List<NexusClient.FileEntry>
        {
            F(1, "MAIN", "2.0", 100, true, "Mod (Linux)"),
            F(2, "MAIN", "2.0", 100, false, "Mod (Windows)"),
        };
        Assert.Equal(2, NexusClient.Pick(files, "2.0", "Windows")!.FileId);
    }

    [Fact]
    public void ParsesNxmLinks()
    {
        var l = NxmLink.Parse("nxm://stardewvalley/mods/1915/files/77123?key=AbC_d-1&expires=1792140000&user_id=5")!;
        Assert.Equal("1915", l.ModId);
        Assert.Equal("77123", l.FileId);
        Assert.Equal("AbC_d-1", l.Key);
        Assert.Equal("1792140000", l.Expires);
        Assert.Null(NxmLink.Parse("https://example.com/mods/1/files/2"));
        Assert.Null(NxmLink.Parse("nxm://stardewvalley/collections/abc/revisions/1"));
        Assert.Null(NxmLink.Parse("nxm://stardewvalley/mods/x/files/2"));
    }
}
