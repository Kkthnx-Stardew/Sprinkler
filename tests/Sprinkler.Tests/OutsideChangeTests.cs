using System.Text.Json;
using Sprinkler.Core;
using Xunit;

namespace Sprinkler.Tests;

/// <summary>
/// The screen has to follow the Mods folder: after a removal, a switch or a profile change, and also after a change
/// made outside the app (a folder deleted in Explorer, a mod copied in by hand).
/// </summary>
public class OutsideChangeTests
{
    private static string[] RowIds(ProfileServiceTests.Rig rig) =>
        rig.Snapshot().GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("uniqueId").GetString()!).OrderBy(x => x).ToArray();

    private static string RowState(ProfileServiceTests.Rig rig, string id) =>
        rig.Snapshot().GetProperty("rows").EnumerateArray().First(r => r.GetProperty("uniqueId").GetString() == id).GetProperty("state").GetString()!;

    [Fact]
    public async Task ARemovedModLeavesTheListAtOnceNotAfterTheNextOnlineCheck()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        Assert.Contains("me.C", RowIds(rig));

        Assert.Null((await rig.Service.RemoveModAsync("me.C", false)).Error);

        // no waiting for the check that follows: the row is already gone
        Assert.DoesNotContain("me.C", RowIds(rig));
    }

    [Fact]
    public async Task ModsThatLostARequiredModSayWhatTheyNeedRightAway()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();

        Assert.Null((await rig.Service.RemoveModAsync("me.A", force: true)).Error);

        var beta = rig.Snapshot().GetProperty("rows").EnumerateArray().First(r => r.GetProperty("uniqueId").GetString() == "me.B");
        Assert.Equal("Alpha", beta.GetProperty("needs").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task SwitchingAModOffShowsItOffAtOnce()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();

        Assert.Null((await rig.Service.SetModEnabledAsync("me.C", false, false)).Error);

        Assert.Equal("Disabled", RowState(rig, "me.C"));
    }

    [Fact]
    public async Task ARemovalWhileACheckIsRunningStillEndsWithTheRightList()
    {
        using var rig = new ProfileServiceTests.Rig();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        rig.Web.Handler = (path, _) =>
        {
            if (path != "/smapi")
                return null;
            if (Interlocked.Increment(ref calls) == 1)
                release.Wait(TimeSpan.FromSeconds(20)); // the first check hangs on the network while the person removes a mod
            return FakeWeb.Json("[]");
        };

        var starting = rig.Service.SetGameFolderAsync(rig.Dir.Combine("game"));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Null((await rig.Service.RemoveModAsync("me.C", false)).Error);
        release.Set();
        await starting;

        // the check that was running read the old folder; a second one must follow it instead of being dropped
        var end = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < end && (calls < 2 || rig.Snapshot().GetProperty("phase").GetString() != "Review"))
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(calls >= 2, "the second check never ran");
        Assert.DoesNotContain("me.C", RowIds(rig));
    }

    [Fact]
    public async Task AFolderDeletedOutsideTheAppIsNoticedAndOnlyOnce()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        ModsDelta? seen = null;
        rig.Service.ModsChangedOutside += d => seen = d;

        Directory.Delete(Path.Combine(rig.Mods, "C"), recursive: true);
        var delta = await rig.Service.CheckForOutsideChangesAsync();

        Assert.NotNull(delta);
        Assert.Equal(1, delta!.Removed);
        Assert.Equal(0, delta.Added);
        Assert.Equal(new[] { "Gamma" }, delta.Names);
        Assert.Same(delta, seen);
        Assert.DoesNotContain("me.C", RowIds(rig));
        Assert.Null(await rig.Service.CheckForOutsideChangesAsync()); // nothing new the second time
    }

    [Fact]
    public async Task AModCopiedInByHandAppearsAndSoDoesASwitchMadeInExplorer()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();

        rig.Dir.Write("game/Mods/D/manifest.json", Zips.Manifest("me.D", "1.0.0", "Delta"));
        Directory.Move(Path.Combine(rig.Mods, "C"), Path.Combine(rig.Mods, ".C"));
        var delta = await rig.Service.CheckForOutsideChangesAsync();

        Assert.Equal(1, delta!.Added);
        Assert.Equal(1, delta.Changed);
        Assert.Contains("me.D", RowIds(rig));
        Assert.Equal("Disabled", RowState(rig, "me.C"));
    }

    [Fact]
    public async Task ARemovedModIsTakenOutOfTheActiveProfileAndUndoPutsItBack()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        rig.Service.CreateProfile("Everything");

        var (error, _, left) = await rig.Service.RemoveModAsync("me.C", false);

        Assert.Null(error);
        Assert.Equal("Everything", left);
        Assert.Equal(2, rig.Profiles().GetProperty("profiles")[0].GetProperty("modCount").GetInt32());
        Assert.Equal(0, rig.Profiles().GetProperty("profiles")[0].GetProperty("missingCount").GetInt32());
        Assert.False(rig.Profiles().GetProperty("modified").GetBoolean());

        await rig.WaitForReviewAsync();
        var id = rig.Snapshot().GetProperty("history")[0].GetProperty("id").GetString()!;
        Assert.Null(await rig.Service.UndoAsync(id));

        Assert.Equal(3, rig.Profiles().GetProperty("profiles")[0].GetProperty("modCount").GetInt32());
        Assert.False(rig.Profiles().GetProperty("modified").GetBoolean());
    }

    [Fact]
    public async Task AModNotInTheActiveProfileIsRemovedWithoutTouchingIt()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        await rig.Service.SetModEnabledAsync("me.C", false, false);
        await rig.WaitForReviewAsync();
        rig.Service.CreateProfile("Two"); // saved with C off, so C is not in its list

        var (error, _, left) = await rig.Service.RemoveModAsync("me.C", false);

        Assert.Null(error);
        Assert.Null(left);
    }

    [Fact]
    public async Task ForgettingMissingModsStopsTheProfileAskingForThem()
    {
        using var rig = new ProfileServiceTests.Rig();
        await rig.StartAsync();
        rig.Service.CreateProfile("Everything");
        // the folder disappears behind the app's back, so the profile still lists it
        Directory.Delete(Path.Combine(rig.Mods, "C"), recursive: true);
        await rig.Service.CheckForOutsideChangesAsync();
        Assert.Equal(1, rig.Profiles().GetProperty("profiles")[0].GetProperty("missingCount").GetInt32());

        var result = rig.Service.ForgetMissingInProfile("Everything");

        Assert.Null(result.Error);
        Assert.Equal(0, rig.Profiles().GetProperty("profiles")[0].GetProperty("missingCount").GetInt32());
        Assert.Equal(2, rig.Profiles().GetProperty("profiles")[0].GetProperty("modCount").GetInt32());
        Assert.Null(rig.Service.ForgetMissingInProfile("Everything").Error); // nothing left to forget is not an error
    }

    [Fact]
    public async Task TheSameModInTwoFoldersIsWarnedAboutBeforeTheGameIsStarted()
    {
        using var rig = new ProfileServiceTests.Rig();
        rig.Dir.Write("game/Mods/C copy/manifest.json", Zips.Manifest("me.C", "1.0.0", "Gamma"));
        await rig.StartAsync();

        var problems = rig.Snapshot().GetProperty("problems").EnumerateArray().ToList();
        var twin = Assert.Single(problems, p => p.GetProperty("title").GetString()!.Contains("Gamma"));
        Assert.Equal("error", twin.GetProperty("level").GetString());
        Assert.Contains("C copy", twin.GetProperty("detail").GetString());
        Assert.Contains("C, ", twin.GetProperty("detail").GetString() + ", "); // both folders are named
    }

    [Fact]
    public async Task ASwitchedOffTwinIsNotAProblem()
    {
        using var rig = new ProfileServiceTests.Rig();
        rig.Dir.Write("game/Mods/.C old/manifest.json", Zips.Manifest("me.C", "0.9.0", "Gamma"));
        await rig.StartAsync();

        Assert.DoesNotContain(rig.Snapshot().GetProperty("problems").EnumerateArray(), p => p.GetProperty("title").GetString()!.Contains("more than once"));
    }

    [Theory]
    [InlineData("C", true)] // a mod folder itself appearing or going
    [InlineData("C/manifest.json", true)] // a mod being updated by hand
    [InlineData("Category/C/manifest.json", true)]
    [InlineData("C/config.json", false)] // the game saving settings is not a change to the list
    [InlineData("C/assets/portrait.png", false)]
    public void OnlyChangesThatCanAlterTheListWakeTheScan(string relative, bool expected)
    {
        var mods = Path.Combine(Path.GetTempPath(), "Mods");
        Assert.Equal(expected, UpdateService.IsModStructureChange(mods, Path.Combine(mods, relative)));
    }
}
