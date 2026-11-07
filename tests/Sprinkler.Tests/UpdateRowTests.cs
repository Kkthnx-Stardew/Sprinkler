using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sprinkler.Core;
using Sprinkler.ViewModels;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>An update is one line until the person opens it, except when it failed and a choice is waiting.</summary>
public class UpdateRowTests
{
    private static ItemInfo Info(ItemStatus status, string message = "", bool usable = true) => new(
        "nexus:5", "Thing", "1.0.0", "1.1.0", 1, false, null, status, message, 0, true, 0, false,
        new List<string> { "Fixed a crash" }, "https://example.test/mod", new List<string>(), null,
        new List<OfferInfo> { new(SourceKind.Nexus, "Nexus Mods", DeliveryMode.Manual, "1.1.0", usable, "needs a login") }, null);

    private static async Task<(ProfileServiceTests.Rig Rig, MainViewModel Vm)> StartAsync()
    {
        var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        return (rig, new MainViewModel(rig.Service) { Ui = new FakeUi(), SuppressTour = true });
    }

    [AvaloniaFact]
    public async Task AReadyUpdateIsOneLineWithItsSourceAndOpensInPlace()
    {
        var (rig, vm) = await StartAsync();
        using var _r = rig;
        var row = new ItemViewModel(vm, Info(ItemStatus.Ready));

        Assert.False(row.IsExpanded);
        Assert.True(row.ShowSummaryLine);
        Assert.Equal("Nexus Mods · You download", row.SourceSummary);
        Assert.False(row.ShowSources);
        Assert.False(row.ShowChangelog);
        Assert.False(row.ShowSkipVersion);
        Assert.Equal("Show details", row.ExpandText);

        row.ToggleExpandedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(row.IsExpanded);
        Assert.False(row.ShowSummaryLine);
        Assert.True(row.ShowSources);
        Assert.True(row.ShowChangelog);
        Assert.True(row.ShowSkipVersion);
        Assert.Equal("Hide details", row.ExpandText);
    }

    [AvaloniaFact]
    public async Task AFailedUpdateOpensByItselfBecauseAChoiceIsWaiting()
    {
        var (rig, vm) = await StartAsync();
        using var _r = rig;
        var row = new ItemViewModel(vm, Info(ItemStatus.Failed, "The file was damaged."));

        Assert.True(row.ShowDetails);
        Assert.True(row.ShowSources);
        Assert.False(row.ShowChevron);
        Assert.False(row.ShowSummaryLine);
    }

    [AvaloniaFact]
    public async Task WithNoUsableSourceTheLineSaysWhyInsteadOfNamingOne()
    {
        var (rig, vm) = await StartAsync();
        using var _r = rig;
        var row = new ItemViewModel(vm, Info(ItemStatus.Ready, "No site has this file.", usable: false));

        Assert.Equal("No site has this file.", row.SourceSummary);
        Assert.True(row.ShowSummaryLine);
    }

    [AvaloniaFact]
    public async Task TheOpenStateSurvivesARefreshFromTheService()
    {
        var (rig, vm) = await StartAsync();
        using var _r = rig;
        var row = new ItemViewModel(vm, Info(ItemStatus.Ready));
        row.ToggleExpandedCommand.Execute(null);

        row.Update(Info(ItemStatus.Ready) with { Target = "1.2.0" });

        Assert.True(row.IsExpanded);
        Assert.Equal("1.2.0", row.Target);
    }
}
