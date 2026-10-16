using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

public class ManifestCaseTests
{
    [Fact]
    public void ScannerFindsAManifestWrittenWithOtherCapitalization()
    {
        using var d = new TempDir();
        d.Write("Mods/Thing/Manifest.json", Zips.Manifest("me.Thing", "1.0.0"));
        var mod = Assert.Single(ModScanner.Scan(d.Combine("Mods")));
        Assert.Equal("me.Thing", mod.UniqueId);
    }

    [Fact]
    public void ScannerSkipsFoldersSmapiNeverTreatsAsMods()
    {
        using var d = new TempDir();
        d.Write("Mods/__folder_managed_by_vortex/Thing/manifest.json", Zips.Manifest("me.Hidden", "1.0.0"));
        d.Write("Mods/__MACOSX/Thing/manifest.json", Zips.Manifest("me.Junk", "1.0.0"));
        d.Write("Mods/._Thing/manifest.json", Zips.Manifest("me.Resource", "1.0.0"));
        d.Write("Mods/Real/manifest.json", Zips.Manifest("me.Real", "1.0.0"));
        Assert.Equal(new[] { "me.Real" }, ModScanner.Scan(d.Combine("Mods")).Select(m => m.UniqueId));
    }

    [Fact]
    public void FindManifestPrefersTheExactName()
    {
        using var d = new TempDir();
        d.Write("a/manifest.json", "{}");
        Assert.Equal(d.Combine("a", "manifest.json"), ManifestReader.FindManifest(d.Combine("a")));
        Assert.Null(ManifestReader.FindManifest(d.Combine("missing")));
    }

    [Fact]
    public void UpdatingWithADifferentlyCasedManifestLeavesOneCurrentManifest()
    {
        using var d = new TempDir();
        var mods = d.Combine("Mods");
        d.Write("Mods/Thing/manifest.json", Zips.Manifest("me.Thing", "1.0.0"));
        var zip = Zips.Build(d, "thing.zip", ("Thing/Manifest.json", Zips.Manifest("me.Thing", "2.0.0")));
        var inst = new Installer(new HistoryStore(d.Combine("history.json"), d.Combine("backups")), new SettingsStore(d.Combine("settings.json")));

        using (var a = ArchiveFile.Open(zip))
            inst.Apply(Installer.Plan(zip, a, mods, ModScanner.Scan(mods)), mods, "test");

        var files = Directory.GetFiles(d.Combine("Mods", "Thing")).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "manifest.json" }, files);
        Assert.Equal("2.0.0", ManifestReader.ReadFile(d.Combine("Mods", "Thing", "manifest.json"), "x")!.Version);
    }
}

public class ShortcutTests
{
    [Fact]
    public void TheDesktopEntryFollowsTheFreedesktopRules()
    {
        var text = LinuxShortcut.DesktopEntryText("/home/me/Games & Tools/StardewModUpdater");
        Assert.StartsWith("[Desktop Entry]", text);
        Assert.Contains("Type=Application", text);
        Assert.Contains("Exec=\"/home/me/Games & Tools/StardewModUpdater\"", text);
        Assert.Contains("Icon=sprinkler", text);
        Assert.Contains("Categories=Game;Utility;", text);
        Assert.Contains("StartupWMClass=Sprinkler", text);
        Assert.Contains("Terminal=false", text);
    }

    [Fact]
    public void ExecQuotingEscapesWhatTheSpecSaysToEscape()
    {
        Assert.Equal("\"/a b/c\"", LinuxShortcut.QuoteExec("/a b/c"));
        Assert.Equal("\"/a\\\\b\\\"c\\$d\\`e%%\"", LinuxShortcut.QuoteExec("/a\\b\"c$d`e%"));
    }

    [Fact]
    public void InstallWritesTheEntryAndIconAndRemoveDeletesThem()
    {
        using var d = new TempDir();
        var icon = LinuxShortcut.EmbeddedIcon();
        Assert.True(icon.Length > 100);
        Assert.Equal(0x89, icon[0]); // PNG signature

        LinuxShortcut.Install(d.Path, "/opt/smu/StardewModUpdater", icon);
        var entry = d.Combine("applications", "sprinkler.desktop");
        var iconFile = d.Combine("icons", "hicolor", "256x256", "apps", "sprinkler.png");
        Assert.Contains("Exec=\"/opt/smu/StardewModUpdater\"", File.ReadAllText(entry));
        Assert.Equal(icon, File.ReadAllBytes(iconFile));

        LinuxShortcut.Remove(d.Path);
        Assert.False(File.Exists(entry));
        Assert.False(File.Exists(iconFile));
        LinuxShortcut.Remove(d.Path); // removing twice is harmless
    }

    [Fact]
    public void TheNxmEntryStillUsesTheSharedQuoting()
    {
        Assert.Contains("Exec=\"/opt/100%%/app\" %u", NxmRegistration.DesktopFileText("/opt/100%/app"));
    }
}
