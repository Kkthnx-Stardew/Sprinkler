using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sprinkler.Core;
using Sprinkler.ViewModels;
using Sprinkler.Views;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>Things the hands-on test of 1.3.0 found, each pinned so it stays fixed.</summary>
public class LiveTestFindingsTests
{
    [Fact]
    public async Task AFolderWithAManifestSmapiCannotReadIsNamedBeforeTheGameIsStarted()
    {
        using var rig = new ProfileServiceTests.Rig();
        rig.Dir.Write("game/Mods/BrokenJson/manifest.json", "{ \"Name\": \"Broken\", \"UniqueID\": ");
        rig.Dir.Write("game/Mods/NoId/manifest.json", "{ \"Name\": \"No ID\", \"Version\": \"1.0.0\" }");
        rig.Dir.Write("game/Mods/.SwitchedOffBroken/manifest.json", "garbage");
        await rig.StartAsync();

        var problems = rig.Snapshot().GetProperty("problems").EnumerateArray().ToList();
        var p = Assert.Single(problems, x => x.GetProperty("title").GetString()!.Contains("cannot be read"));
        var detail = p.GetProperty("detail").GetString()!;
        Assert.Equal("warn", p.GetProperty("level").GetString());
        Assert.Contains("BrokenJson", detail);
        Assert.Contains("NoId", detail);
        Assert.DoesNotContain("SwitchedOffBroken", detail); // SMAPI never looks at a switched-off folder
    }

    [Fact]
    public async Task HealthyFoldersRaiseNoUnreadableWarning()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();

        Assert.DoesNotContain(rig.Snapshot().GetProperty("problems").EnumerateArray(), x => x.GetProperty("title").GetString()!.Contains("cannot be read"));
    }

    [Fact]
    public void TheScannerReportsUnreadableFoldersOnlyWhenAskedAndKeepsReadingTheRest()
    {
        using var d = new TempDir();
        d.Write("Mods/Good/manifest.json", Zips.Manifest("me.Good", "1.0.0", "Good"));
        d.Write("Mods/Empty/manifest.json", "");
        var unreadable = new List<string>();

        var mods = ModScanner.Scan(d.Combine("Mods"), unreadable);

        Assert.Equal("me.Good", Assert.Single(mods).UniqueId);
        Assert.Equal(new[] { "Empty" }, unreadable);
        Assert.Single(ModScanner.Scan(d.Combine("Mods"))); // the old overload still works
    }

    [AvaloniaFact]
    public async Task ARepeatedVersionInHistoryReadsAsAReinstallNotAnUpdate()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        var vm = new MainViewModel(rig.Service) { Ui = new FakeUi(), SuppressTour = true };
        var when = new DateTime(2026, 11, 5, 22, 58, 0);

        var same = new HistoryViewModel(vm, new HistoryInfo("a", when, "ValleyFixes", "0.9.0-beta.2", "0.9.0-beta.2", "a file you provided", false, true, false));
        var newer = new HistoryViewModel(vm, new HistoryInfo("b", when, "ValleyFixes", "0.2.0", "0.9.0", "a file you provided", false, true, false));

        Assert.Contains("reinstalled", same.Detail);
        Assert.DoesNotContain("→", same.Detail);
        Assert.Contains("→", newer.Detail);
    }

    [Fact]
    public void AWindowsPathComesBackInTheCapitalsTheFileSystemUses()
    {
        using var d = new TempDir();
        var real = d.Combine("Program Files (x86)", "Steam Library");
        Directory.CreateDirectory(real);

        var shouted = real.ToLowerInvariant();
        var fixedPath = Platform.TrueCase(shouted);

        if (Platform.IsWindows)
            Assert.Equal(Path.GetFullPath(real), fixedPath, ignoreCase: false);
        else
            Assert.Equal(shouted, fixedPath); // case sensitive systems keep the path as given
        Assert.Equal("", Platform.TrueCase(""));
    }

    [AvaloniaFact]
    public async Task TheCountChipsOnUpdatesAreNamedWithTheirLabelsNotJustTheNumber()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        rig.Service.ConfirmWelcome(); // past the first-run panel, so the headline and its chips show
        var vm = new MainViewModel(rig.Service) { Ui = new FakeUi(), SuppressTour = true };
        vm.Refresh();
        var window = new MainWindow();
        window.Attach(vm);
        window.Show();
        vm.Tab = AppTab.Updates;
        Dispatcher.UIThread.RunJobs();

        var names = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Where(b => b.IsEffectivelyVisible)
            .Select(b => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(b).GetName())
            .ToList();
        // each chip reads as its count and its label together, so a screen reader never says a bare number
        Assert.True(names.Any(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+ mods in your folder$")), string.Join(" | ", names));
        Assert.Contains(names, n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+ up to date$"));
        Assert.DoesNotContain(names, n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+$"));
        window.Close();
    }
}
