using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sprinkler.Core;
using Sprinkler.ViewModels;
using Sprinkler.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Sprinkler.Tests.TestAppBuilder))]

namespace Sprinkler.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Sprinkler.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>A stand-in for the window's file pickers and clipboard.</summary>
internal sealed class FakeUi : IUiServices
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public string? Copied { get; private set; }

    public Task CopyTextAsync(string text)
    {
        Copied = text;
        return Task.CompletedTask;
    }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<string?> PickOpenFileAsync(string title, string typeName, IReadOnlyList<string> patterns) => Task.FromResult(OpenPath);

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string typeName, IReadOnlyList<string> patterns) => Task.FromResult(SavePath);
}

/// <summary>
/// The real window and view models, run without a screen. They press the same commands the buttons press and look at what the
/// person would see: dialogs, messages, folders on disk.
/// </summary>
public class UiTests
{
    private static async Task<(ProfileServiceTests.Rig Rig, MainViewModel Vm, FakeUi Ui)> StartAsync(bool extraPack = false)
    {
        var rig = new ProfileServiceTests.Rig();
        if (extraPack)
            rig.Dir.Write("game/Mods/D/manifest.json", Zips.Manifest("me.D", "1.0.0", "Delta pack", ", \"ContentPackFor\": { \"UniqueID\": \"me.A\" }"));
        await rig.StartAsync();
        var ui = new FakeUi();
        var vm = new MainViewModel(rig.Service) { Ui = ui, SuppressTour = true };
        vm.Refresh();
        return (rig, vm, ui);
    }

    private static async Task SettleAsync(MainViewModel vm, Func<bool>? until = null)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        do
        {
            Dispatcher.UIThread.RunJobs();
            vm.Refresh();
            if (until is null || until())
                return;
            await Task.Delay(50);
        }
        while (DateTime.UtcNow < end);
        Assert.True(until!(), "The screen never reached the expected state.");
    }

    [AvaloniaFact]
    public async Task EveryTabOfTheRealWindowOpensWithoutAnError()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        var window = new MainWindow();
        window.Attach(vm);
        window.Show();
        foreach (var tab in Enum.GetValues<AppTab>())
        {
            vm.Tab = tab;
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame!.PixelSize.Width > 100);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task SavingAProfileShowsItAsActiveAndNamesItInTheHeader()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        Assert.False(vm.CanCreateProfile);

        vm.NewProfileName = "Co-op farm";
        Assert.True(vm.CanCreateProfile);
        vm.CreateProfileCommand.Execute(null);
        await SettleAsync(vm);

        var profile = Assert.Single(vm.ProfileList);
        Assert.True(profile.IsActive);
        Assert.False(profile.CanSwitch);
        Assert.Equal("Profile: Co-op farm", vm.ProfileChipText);
        Assert.Equal("", vm.NewProfileName);
        Assert.Contains(vm.Toasts, t => t.Text.Contains("Co-op farm"));
    }

    [AvaloniaFact]
    public async Task ASecondProfileWithTheSameNameIsRefusedWithAMessage()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        vm.NewProfileName = "Same";
        vm.CreateProfileCommand.Execute(null);
        vm.NewProfileName = "same";
        vm.CreateProfileCommand.Execute(null);
        await SettleAsync(vm);

        Assert.Single(vm.ProfileList);
        Assert.Contains(vm.Toasts, t => t.IsBad && t.Text.Contains("already exists"));
    }

    [AvaloniaFact]
    public async Task SwitchingAfterUnsavedChangesAsksFirstAndThenSwitches()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        vm.NewProfileName = "All three";
        vm.CreateProfileCommand.Execute(null);
        rig.Service.Profiles.Add(new Profile { Name = "Just A", Enabled = { "me.A" } }, false);
        await SettleAsync(vm);

        // switch C off by hand: the active profile no longer matches
        await rig.Service.SetModEnabledAsync("me.C", false, false);
        await rig.WaitForReviewAsync();
        await SettleAsync(vm, () => vm.ProfileModified);
        Assert.Contains("(changed)", vm.ProfileChipText);

        var target = vm.ProfileList.Single(p => p.Name == "Just A");
        var switching = target.SwitchCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is ConfirmDialogViewModel);
        var dialog = Assert.IsType<ConfirmDialogViewModel>(vm.Dialog);
        Assert.Equal("Switch without saving?", dialog.Title);
        Assert.Equal(new[] { ".B", ".C", "A" }.Length, rig.Folders().Length); // nothing moved while the question is open
        Assert.DoesNotContain(".B", rig.Folders());

        dialog.YesCommand.Execute(null);
        await switching;
        await SettleAsync(vm);

        Assert.Equal(new[] { ".B", ".C", "A" }, rig.Folders());
        Assert.Equal("Just A", vm.ActiveProfileName);
        Assert.Null(vm.Dialog);
    }

    [AvaloniaFact]
    public async Task SayingNoToTheQuestionChangesNothing()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        vm.NewProfileName = "All three";
        vm.CreateProfileCommand.Execute(null);
        rig.Service.Profiles.Add(new Profile { Name = "Just A", Enabled = { "me.A" } }, false);
        await rig.Service.SetModEnabledAsync("me.C", false, false);
        await rig.WaitForReviewAsync();
        await SettleAsync(vm, () => vm.ProfileModified);

        var switching = vm.ProfileList.Single(p => p.Name == "Just A").SwitchCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is ConfirmDialogViewModel);
        ((ConfirmDialogViewModel)vm.Dialog!).NoCommand.Execute(null);
        await switching;

        Assert.Equal(new[] { ".C", "A", "B" }, rig.Folders());
        Assert.Equal("All three", vm.ActiveProfileName);
    }

    [AvaloniaFact]
    public async Task RenamingAProfileUsesATextPrompt()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        vm.NewProfileName = "Old name";
        vm.CreateProfileCommand.Execute(null);
        await SettleAsync(vm);

        var renaming = vm.ProfileList[0].RenameCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is TextPromptDialogViewModel);
        var prompt = Assert.IsType<TextPromptDialogViewModel>(vm.Dialog);
        Assert.Equal("Old name", prompt.Text);
        prompt.Text = "New name";
        prompt.OkCommand.Execute(null);
        await renaming;
        await SettleAsync(vm);

        Assert.Equal("New name", Assert.Single(vm.ProfileList).Name);
        Assert.Equal("New name", vm.ActiveProfileName);
    }

    [AvaloniaFact]
    public async Task CancellingAPromptLeavesEverythingAlone()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        vm.NewProfileName = "Keep me";
        vm.CreateProfileCommand.Execute(null);
        await SettleAsync(vm);

        var renaming = vm.ProfileList[0].RenameCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is TextPromptDialogViewModel);
        ((TextPromptDialogViewModel)vm.Dialog!).CancelCommand.Execute(null);
        await renaming;

        Assert.Equal("Keep me", vm.ProfileList[0].Name);
    }

    [AvaloniaFact]
    public async Task AProfileGoesToAFileAndComesBackThroughTheSharedFlow()
    {
        var (rig, vm, ui) = await StartAsync();
        using var _r = rig;
        vm.NewProfileName = "Friends";
        vm.CreateProfileCommand.Execute(null);
        await SettleAsync(vm);

        ui.SavePath = rig.Dir.Combine("friends.sprinkler.json");
        await vm.ProfileList[0].ExportCommand.ExecuteAsync(null);
        Assert.True(File.Exists(ui.SavePath));
        Assert.DoesNotContain(rig.Dir.Path, File.ReadAllText(ui.SavePath));

        ui.OpenPath = ui.SavePath;
        var importing = vm.ImportProfileCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is ConfirmDialogViewModel);
        ((ConfirmDialogViewModel)vm.Dialog!).NoCommand.Execute(null); // not switching now
        await importing;
        await SettleAsync(vm);

        Assert.Equal(new[] { "Friends", "Friends (2)" }.Length, vm.ProfileList.Count);
        Assert.Contains(vm.ProfileList, p => p.Name == "Friends (2)");
    }

    [AvaloniaFact]
    public async Task ImportingANonProfileFileShowsAPlainMessage()
    {
        var (rig, vm, ui) = await StartAsync();
        using var _r = rig;
        ui.OpenPath = rig.Dir.Write("junk.json", "{ \"hello\": 1 }");
        await vm.ImportProfileCommand.ExecuteAsync(null);
        Assert.Contains(vm.Toasts, t => t.IsBad && t.Text.Contains("not a Sprinkler profile"));
        Assert.Empty(vm.ProfileList);
    }

    [AvaloniaFact]
    public async Task RemovingAModAsksThenRemovesAndHistoryOffersUndo()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        var row = vm.Mods.Single(m => m.UniqueId == "me.C");

        var removing = row.RemoveCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is ConfirmDialogViewModel);
        Assert.Contains("Remove Gamma?", ((ConfirmDialogViewModel)vm.Dialog!).Title);
        Assert.True(Directory.Exists(Path.Combine(rig.Mods, "C")));
        ((ConfirmDialogViewModel)vm.Dialog).YesCommand.Execute(null);
        await removing;
        await rig.WaitForReviewAsync();
        await SettleAsync(vm, () => vm.History.Count == 1);

        Assert.False(Directory.Exists(Path.Combine(rig.Mods, "C")));
        var entry = vm.History[0];
        Assert.True(entry.CanUndo);
        Assert.Contains("Removed", entry.Detail);

        var undoing = entry.UndoCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is ConfirmDialogViewModel);
        ((ConfirmDialogViewModel)vm.Dialog!).YesCommand.Execute(null);
        await undoing;
        Assert.True(Directory.Exists(Path.Combine(rig.Mods, "C")));
    }

    [AvaloniaFact]
    public async Task RemovingAModOthersNeedAsksASecondTime()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        var removing = vm.Mods.Single(m => m.UniqueId == "me.A").RemoveCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is ConfirmDialogViewModel);
        ((ConfirmDialogViewModel)vm.Dialog!).YesCommand.Execute(null);

        await SettleAsync(vm, () => vm.Dialog is ConfirmDialogViewModel d && d.Title.Contains("anyway"));
        var second = (ConfirmDialogViewModel)vm.Dialog!;
        Assert.Contains("Beta", second.Lines[0].Text);
        Assert.True(Directory.Exists(Path.Combine(rig.Mods, "A")));
        second.NoCommand.Execute(null);
        await removing;
        Assert.True(Directory.Exists(Path.Combine(rig.Mods, "A")));
    }

    [AvaloniaFact]
    public async Task ANoteShowsUnderTheModAndCanBeCleared()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        var row = vm.Mods.Single(m => m.UniqueId == "me.B");
        Assert.False(row.HasNote);

        var editing = row.EditNoteCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is TextPromptDialogViewModel);
        var prompt = (TextPromptDialogViewModel)vm.Dialog!;
        prompt.Text = "wait for the festival patch";
        prompt.OkCommand.Execute(null);
        await editing;
        await SettleAsync(vm);

        Assert.True(row.HasNote);
        Assert.Equal("wait for the festival patch", row.NoteText);
        Assert.Equal("Edit the note", row.NoteMenuText);

        var clearing = row.EditNoteCommand.ExecuteAsync(null);
        await SettleAsync(vm, () => vm.Dialog is TextPromptDialogViewModel);
        ((TextPromptDialogViewModel)vm.Dialog!).Text = "";
        ((TextPromptDialogViewModel)vm.Dialog).OkCommand.Execute(null);
        await clearing;
        await SettleAsync(vm);
        Assert.False(row.HasNote);
    }

    [AvaloniaFact]
    public async Task ContentPacksSitUnderTheirModWhenGroupingIsOnAndInPlaceWhenOff()
    {
        var (rig, vm, _) = await StartAsync(extraPack: true);
        using var _r = rig;
        vm.ModSortIndex = 1; // by name: Alpha, Beta, Delta pack, Gamma

        Assert.True(vm.GroupPacks);
        var order = vm.Mods.Select(m => m.UniqueId).ToList();
        Assert.Equal(new[] { "me.A", "me.D", "me.B", "me.C" }, order); // the pack follows Alpha
        Assert.True(vm.Mods.Single(m => m.UniqueId == "me.D").Indented);
        Assert.False(vm.Mods.Single(m => m.UniqueId == "me.A").Indented);

        vm.GroupPacks = false;
        await SettleAsync(vm);
        Assert.Equal(new[] { "me.A", "me.B", "me.D", "me.C" }, vm.Mods.Select(m => m.UniqueId));
        Assert.All(vm.Mods, m => Assert.False(m.Indented));
    }

    [AvaloniaFact]
    public async Task ThePackStaysPutWhenItsModIsFilteredOut()
    {
        var (rig, vm, _) = await StartAsync(extraPack: true);
        using var _r = rig;
        vm.ModSearch = "Delta";
        Assert.Equal(new[] { "me.D" }, vm.Mods.Select(m => m.UniqueId));
        Assert.False(vm.Mods[0].Indented);
    }

    [AvaloniaFact]
    public async Task TheHasSettingsFilterFindsModsWithAConfigFile()
    {
        var (rig, vm, _) = await StartAsync();
        using var _r = rig;
        await SettleAsync(vm, () => vm.Mods.Any(m => m.HasConfig));
        vm.SetModFilter("config");
        Assert.Equal(new[] { "me.A" }, vm.Mods.Select(m => m.UniqueId));
    }

    [AvaloniaFact]
    public async Task EndorsingIsOnlyOfferedForNexusModsWhenConnected()
    {
        var rig = new ProfileServiceTests.Rig();
        rig.Dir.Write("game/Mods/N/manifest.json", Zips.Manifest("me.N", "2.0.0", "Nexus mod", ", \"UpdateKeys\": [\"Nexus:6\"]"));
        rig.Web.Handler = (path, ctx) => path switch
        {
            "/smapi" => FakeWeb.Json("[]"),
            "/nexus/users/validate.json" => FakeWeb.Json("""{"name":"Tester","is_premium":false}"""),
            "/nexus/user/endorsements.json" => FakeWeb.Json("[]"),
            "/nexus/games/stardewvalley/mods/6/endorse.json" => FakeWeb.Json("""{"status":"ENDORSED","message":"ok"}"""),
            _ => null,
        };
        using var _r = rig;
        await rig.StartAsync();
        var vm = new MainViewModel(rig.Service) { Ui = new FakeUi(), SuppressTour = true };
        vm.Refresh();

        var row = vm.Mods.Single(m => m.UniqueId == "me.N");
        Assert.Equal(6, row.NexusId);
        Assert.False(row.CanEndorse); // not connected yet
        Assert.False(vm.Mods.Single(m => m.UniqueId == "me.A").CanEndorse);

        Assert.Null(await rig.Service.ConnectNexusAsync(new string('k', 32)));
        await SettleAsync(vm);
        Assert.True(row.CanEndorse);
        Assert.Equal("Endorse on Nexus", row.EndorseText);

        await row.ToggleEndorsementCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        Assert.True(row.IsEndorsed);
        Assert.Equal("Remove my endorsement", row.EndorseText);
    }
}
