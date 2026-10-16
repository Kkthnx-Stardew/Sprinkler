using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

public class ArchiveTests
{
    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("Mod/../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("C:\\evil.txt")]
    public void RejectsPathsThatEscape(string name)
    {
        using var d = new TempDir();
        var zip = Zips.Build(d, "bad.zip", ("Mod/manifest.json", Zips.Manifest("a.b", "1.0")), (name, "x"));
        Assert.Throws<ArchiveException>(() => ArchiveFile.Open(zip));
    }

    [Fact]
    public void FindsOutermostManifests()
    {
        using var d = new TempDir();
        var zip = Zips.Build(d, "ok.zip",
            ("Pack/A/manifest.json", Zips.Manifest("a.a", "1.0")),
            ("Pack/A/inner/manifest.json", Zips.Manifest("hidden.mod", "1.0")),
            ("Pack/B/manifest.json", Zips.Manifest("b.b", "2.0")),
            ("__MACOSX/Pack/A/._manifest.json", "junk"),
            ("readme.txt", "hi"));
        using var a = ArchiveFile.Open(zip);
        var mods = ArchiveInspector.FindMods(a);
        Assert.Equal(new[] { "a.a", "b.b" }, mods.Select(m => m.Manifest.UniqueId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void HandlesManifestAtArchiveRoot()
    {
        using var d = new TempDir();
        var zip = Zips.Build(d, "root.zip", ("manifest.json", Zips.Manifest("a.a", "1.0")), ("x.dll", "d"));
        using var a = ArchiveFile.Open(zip);
        Assert.Equal("", Assert.Single(ArchiveInspector.FindMods(a)).RootPath);
    }

    [Fact]
    public void RejectsNonArchives()
    {
        using var d = new TempDir();
        var path = d.Write("fake.zip", "this is not a zip");
        Assert.Throws<ArchiveException>(() => ArchiveFile.Open(path));
    }
}

public class InstallerTests
{
    private static (Installer Installer, HistoryStore History, SettingsStore Settings) Make(TempDir d)
    {
        var settings = new SettingsStore(d.Combine("settings.json"));
        var history = new HistoryStore(d.Combine("history.json"), d.Combine("backups"));
        return (new Installer(history, settings), history, settings);
    }

    private static InstallResult Install(Installer inst, string zip, string mods)
    {
        using var a = ArchiveFile.Open(zip);
        var plan = Installer.Plan(zip, a, mods, ModScanner.Scan(mods));
        return inst.Apply(plan, mods, "test");
    }

    [Fact]
    public void ReplacesKeepsConfigRemovesStaleCodeAndCanUndo()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        d.Write("Mods/Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0"));
        d.Write("Mods/Thing/config.json", "{\"mine\":true}");
        d.Write("Mods/Thing/Old.dll", "old");
        d.Write("Mods/Thing/Thing.dll", "old thing");
        d.Write("Mods/Thing/MyOwnNotes.txt", "keep me");
        var (inst, history, _) = Make(d);

        var zip = Zips.Build(d, "u.zip",
            ("Thing/manifest.json", Zips.Manifest("me.Thing", "1.1.0")),
            ("Thing/config.json", "{\"mine\":false}"),
            ("Thing/Thing.dll", "new thing"));
        var result = Install(inst, zip, mods);

        Assert.Equal("1.1.0", ManifestReader.ReadFile(d.Combine("Mods", "Thing", "manifest.json"), "x")!.Version);
        Assert.Equal("{\"mine\":true}", File.ReadAllText(d.Combine("Mods", "Thing", "config.json")));
        Assert.Equal("new thing", File.ReadAllText(d.Combine("Mods", "Thing", "Thing.dll")));
        Assert.False(File.Exists(d.Combine("Mods", "Thing", "Old.dll")));
        Assert.True(File.Exists(d.Combine("Mods", "Thing", "MyOwnNotes.txt")));
        Assert.NotEmpty(result.Messages);

        inst.Undo(result.HistoryId, mods);
        Assert.Equal("1.0.0", ManifestReader.ReadFile(d.Combine("Mods", "Thing", "manifest.json"), "x")!.Version);
        Assert.Equal("old", File.ReadAllText(d.Combine("Mods", "Thing", "Old.dll")));
        Assert.True(history.Find(result.HistoryId)!.Undone);
        Assert.Throws<InstallException>(() => inst.Undo(result.HistoryId, mods));
    }

    [WindowsFact]
    public void RollsBackWhenAFileIsLocked()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        d.Write("Mods/Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0"));
        var locked = d.Write("Mods/Thing/Thing.dll", "old thing");
        d.Write("Mods/Thing/a.txt", "a-old");
        var (inst, history, _) = Make(d);

        var zip = Zips.Build(d, "u.zip",
            ("Thing/manifest.json", Zips.Manifest("me.Thing", "2.0.0")),
            ("Thing/a.txt", "a-new"),
            ("Thing/Thing.dll", "new thing"));

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<InstallException>(() => Install(inst, zip, mods));
            Assert.Contains("Nothing was changed", ex.Message);
        }

        Assert.Equal("1.0.0", ManifestReader.ReadFile(d.Combine("Mods", "Thing", "manifest.json"), "x")!.Version);
        Assert.Equal("a-old", File.ReadAllText(d.Combine("Mods", "Thing", "a.txt")));
        Assert.Equal("old thing", File.ReadAllText(locked));
        Assert.Empty(history.All());
    }

    [Fact]
    public void InstallsNewModsIntoAFreshFolder()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        Directory.CreateDirectory(d.Combine("Mods", "Fresh"));
        var (inst, history, _) = Make(d);
        var zip = Zips.Build(d, "n.zip", ("Fresh/manifest.json", Zips.Manifest("new.Fresh", "1.0.0")), ("Fresh/f.dll", "x"));
        var result = Install(inst, zip, mods);

        Assert.True(File.Exists(d.Combine("Mods", "Fresh (2)", "manifest.json")));
        inst.Undo(result.HistoryId, mods);
        Assert.False(Directory.Exists(d.Combine("Mods", "Fresh (2)")));
        Assert.True(Directory.Exists(d.Combine("Mods", "Fresh")));
    }

    [Fact]
    public void UpdatesAModInNestedFolderInPlace()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        d.Write("Mods/[Pack]/Inner/manifest.json", Zips.Manifest("me.Inner", "1.0.0"));
        var (inst, _, _) = Make(d);
        var zip = Zips.Build(d, "u.zip", ("Different Name/manifest.json", Zips.Manifest("me.Inner", "1.1.0")));
        Install(inst, zip, mods);
        Assert.Equal("1.1.0", ManifestReader.ReadFile(d.Combine("Mods", "[Pack]", "Inner", "manifest.json"), "x")!.Version);
        Assert.False(Directory.Exists(d.Combine("Mods", "Different Name")));
    }

    [Fact]
    public void WarnsWhenArchiveIsOlder()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        d.Write("Mods/T/manifest.json", Zips.Manifest("me.T", "2.0.0"));
        var zip = Zips.Build(d, "o.zip", ("T/manifest.json", Zips.Manifest("me.T", "1.0.0")));
        using var a = ArchiveFile.Open(zip);
        var plan = Installer.Plan(zip, a, mods, ModScanner.Scan(mods));
        Assert.Single(plan.Folders[0].Warnings);
    }

    [Fact]
    public void PrunesOldBackups()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        d.Write("Mods/T/manifest.json", Zips.Manifest("me.T", "1.0.0"));
        var (inst, history, settings) = Make(d);
        settings.Data.BackupsPerMod = 2;
        for (var v = 1; v <= 4; v++)
        {
            var zip = Zips.Build(d, $"v{v}.zip", ("T/manifest.json", Zips.Manifest("me.T", $"1.{v}.0")));
            Install(inst, zip, mods);
            Thread.Sleep(1100);
        }
        Assert.Equal(2, Directory.GetFiles(history.BackupsDir, "*.zip").Length);
        Assert.Equal(4, history.All().Count);
    }
}

public class InstallerSafetyTests
{
    private static (Installer Installer, HistoryStore History) Make(TempDir d)
    {
        var settings = new SettingsStore(d.Combine("settings.json"));
        var history = new HistoryStore(d.Combine("history.json"), d.Combine("backups"));
        return (new Installer(history, settings), history);
    }

    /// <summary>A zip whose directory is valid but whose compressed data is damaged, so reading an entry fails late.</summary>
    internal static string DamagedZip(TempDir d)
    {
        var big = string.Join('\n', Enumerable.Range(0, 4000).Select(i => "line " + i + " of some compressible text for the mod"));
        var path = Zips.Build(d, "damaged.zip", ("Thing/manifest.json", Zips.Manifest("me.Thing", "2.0.0")), ("Thing/big.txt", big));
        var bytes = File.ReadAllBytes(path);
        for (var i = 200; i < 260; i++)
            bytes[i] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ADamagedArchiveFailsCleanlyAndChangesNothing()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        d.Write("Mods/Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0"));
        var (inst, history) = Make(d);
        var zip = DamagedZip(d);

        using (var a = ArchiveFile.Open(zip))
        {
            var plan = Installer.Plan(zip, a, mods, ModScanner.Scan(mods));
            Assert.Throws<InstallException>(() => inst.Apply(plan, mods, "test"));
        }

        Assert.Equal("1.0.0", ManifestReader.ReadFile(d.Combine("Mods", "Thing", "manifest.json"), "x")!.Version);
        Assert.Empty(history.All());
    }

    [Fact]
    public void UndoWithADamagedBackupLeavesTheCurrentModAlone()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        d.Write("Mods/Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0"));
        var (inst, history) = Make(d);
        var zip = Zips.Build(d, "u.zip", ("Thing/manifest.json", Zips.Manifest("me.Thing", "1.1.0")));
        using (var a = ArchiveFile.Open(zip))
            inst.Apply(Installer.Plan(zip, a, mods, ModScanner.Scan(mods)), mods, "test");
        var entry = history.All().Single();

        File.WriteAllText(Path.Combine(history.BackupsDir, entry.BackupFile!), "this is not a zip any more");

        Assert.Throws<InstallException>(() => inst.Undo(entry.Id, mods));
        Assert.Equal("1.1.0", ManifestReader.ReadFile(d.Combine("Mods", "Thing", "manifest.json"), "x")!.Version);
        Assert.False(history.Find(entry.Id)!.Undone);
    }

    [Fact]
    public void RefusesAnInstallThatCannotFitOnTheDrive()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        var zip = Zips.Build(d, "n.zip", ("Fresh/manifest.json", Zips.Manifest("new.Fresh", "1.0.0")), ("Fresh/data.bin", new string('x', 100_000)));
        using var a = ArchiveFile.Open(zip);
        var plan = Installer.Plan(zip, a, mods, new List<ModInfo>());

        var ex = Assert.Throws<InstallException>(() => Installer.EnsureSpace(plan, a, mods, d.Combine("b"), d.Combine("s"), _ => 1024));
        Assert.Contains("not enough free space", ex.Message);
        Installer.EnsureSpace(plan, a, mods, d.Combine("b"), d.Combine("s"), _ => long.MaxValue);
    }

    [Fact]
    public void SwitchingSourceKeepsEveryOfferAfterItInTheRetryOrder()
    {
        Assert.Equal(new[] { 0, 1, 2 }, UpdateService.NextOrder(new List<int> { 0, 1, 2 }, 0, 1));
        // waiting on offer 1 (position 1) and switching to offer 0: offer 2 must still be tried afterwards
        Assert.Equal(new[] { 0, 1, 0, 2 }, UpdateService.NextOrder(new List<int> { 0, 1, 2 }, 1, 0));
        // switching to a later offer moves it to the front of what is left
        Assert.Equal(new[] { 0, 2, 1 }, UpdateService.NextOrder(new List<int> { 0, 1, 2 }, 0, 2));
    }
}
