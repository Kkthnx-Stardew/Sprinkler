using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

// The Profiles tab: named sets of switched-on mods that can be saved, switched, shared and brought in.
internal sealed partial class MainViewModel
{
    private readonly Dictionary<string, ProfileViewModel> _profileMap = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ProfileViewModel> ProfileList { get; } = new();

    private void SyncProfiles(StateSnapshot s)
    {
        var order = new List<ProfileViewModel>();
        foreach (var p in s.Profiles.Profiles)
        {
            if (_profileMap.TryGetValue(p.Name, out var vm))
                vm.Update(p);
            else
                _profileMap[p.Name] = vm = new ProfileViewModel(this, p);
            order.Add(vm);
        }
        foreach (var gone in _profileMap.Keys.Where(k => s.Profiles.Profiles.All(p => !p.Name.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            _profileMap.Remove(gone);
        SyncList(ProfileList, order.OrderByDescending(p => p.IsActive).ToList()); // the active one first, the rest by name
    }

    public bool HasProfiles => ProfileList.Count > 0;
    public bool NoProfiles => ProfileList.Count == 0;
    public string ActiveProfileName => Snap?.Profiles.Active ?? "";
    public bool HasActiveProfile => !string.IsNullOrEmpty(Snap?.Profiles.Active);
    public bool NoActiveProfile => HasProfiles && !HasActiveProfile;
    public bool ProfileModified => HasActiveProfile && Snap?.Profiles.Modified == true;
    public string ProfileModifiedText => $"You changed which mods are switched on since you loaded \"{ActiveProfileName}\". Save the changes to keep them in the profile, or switch to another profile to leave them behind.";
    public string ProfileChipText => HasActiveProfile ? "Profile: " + ActiveProfileName + (ProfileModified ? " (changed)" : "") : "";
    public bool CanCreateProfile => HasGame && !string.IsNullOrWhiteSpace(NewProfileName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateProfile))]
    private string _newProfileName = "";

    public bool KeepConfigs
    {
        get => Snap?.Profiles.KeepConfigs == true;
        set
        {
            if (value == KeepConfigs)
                return;
            _svc.SetKeepConfigs(value);
            Refresh();
        }
    }

    // ---- creating, saving, importing
    [RelayCommand]
    private void CreateProfile()
    {
        var result = _svc.CreateProfile(NewProfileName);
        if (result.Error is not null)
        {
            Toast(result.Error, true);
            return;
        }
        NewProfileName = "";
        Toast(result.Message);
        Refresh();
    }

    [RelayCommand]
    private void SaveProfileChanges()
    {
        var result = _svc.SaveActiveProfile();
        Toast(result.Error ?? result.Message, result.Error is not null);
        Refresh();
    }

    [RelayCommand]
    private async Task ImportProfile()
    {
        if (Ui is null)
            return;
        var path = await Ui.PickOpenFileAsync("Open a Sprinkler profile", "Sprinkler profile", new[] { "*.json" });
        if (path is null)
            return;

        string text;
        try
        {
            if (new FileInfo(path).Length > PortableProfile.MaxBytes)
            {
                Toast("That file is too large to be a profile.", true);
                return;
            }
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast("Could not read that file: " + ex.Message, true);
            return;
        }

        var result = _svc.ImportProfile(text);
        Refresh();
        if (result.Error is not null)
        {
            Toast(result.Error, true);
            return;
        }
        Toast(result.Message);

        var name = _svc.Profiles.All().OrderByDescending(p => p.Created).First().Name;
        if (await AskAsync("Switch to it now?", $"\"{name}\" is saved. Switching turns its mods on and the rest off." +
            (result.Missing.Count > 0 ? $" {Fmt.Plural(result.Missing.Count, "mod", "mods")} in it {(result.Missing.Count == 1 ? "is" : "are")} not installed here; they will appear under Updates as installs." : ""),
            "Switch now", "gift"))
            await SwitchProfileAsync(name);
    }

    // ---- one profile
    public async Task SwitchProfileAsync(string name)
    {
        if (ProfileModified && !name.Equals(ActiveProfileName, StringComparison.OrdinalIgnoreCase))
        {
            var go = await AskAsync("Switch without saving?",
                $"You changed which mods are switched on since you loaded \"{ActiveProfileName}\". Those changes are not kept unless you save them first.",
                "Switch anyway", "alert");
            if (!go)
                return;
        }

        var result = await Task.Run(() => _svc.ApplyProfile(name));
        Refresh();
        Toast(result.Error ?? result.Message, result.Error is not null);
        if (result.Error is null && result.Missing.Count > 0)
            Tab = AppTab.Updates;
    }

    public async Task RenameProfileAsync(string name)
    {
        var text = await PromptAsync("Rename profile", "tag", "New name", name, "Rename", ProfileStore.MaxNameLength);
        if (text is null)
            return;
        var result = _svc.RenameProfile(name, text);
        Toast(result.Error ?? result.Message, result.Error is not null);
        Refresh();
    }

    public async Task CopyProfileAsync(string name)
    {
        var text = await PromptAsync("Copy profile", "package", "Name for the copy", name + " copy", "Copy", ProfileStore.MaxNameLength);
        if (text is null)
            return;
        var result = _svc.CopyProfile(name, text);
        Toast(result.Error ?? result.Message, result.Error is not null);
        Refresh();
    }

    public async Task DeleteProfileAsync(string name)
    {
        if (!await AskAsync($"Delete \"{name}\"?", "Only the saved list is deleted. Your mods and their settings stay exactly as they are.", "Delete profile", "alert"))
            return;
        var result = _svc.DeleteProfile(name);
        Toast(result.Error ?? result.Message, result.Error is not null);
        Refresh();
    }

    public async Task ExportProfileAsync(string name)
    {
        if (Ui is null)
            return;
        var path = await Ui.PickSaveFileAsync("Save the profile to share", name + ".sprinkler.json", "Sprinkler profile", new[] { "*.json" });
        if (path is null)
            return;
        try
        {
            await File.WriteAllTextAsync(path, _svc.ExportProfile(name));
            Toast("Saved. Send that file to a friend; it lists mods, never your settings or folders.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfileException)
        {
            Toast("Could not save the file: " + ex.Message, true);
        }
    }

    // ---- text prompts
    public async Task<string?> PromptAsync(string title, string icon, string prompt, string initial, string yes, int maxLength)
    {
        var dialog = new TextPromptDialogViewModel(title, icon, prompt, initial, yes, maxLength);
        ShowDialog(dialog);
        return await dialog.Result;
    }
}
