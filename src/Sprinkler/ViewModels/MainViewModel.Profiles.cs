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
    public string ProfileModifiedText => Loc.T("prof.modified_text", ActiveProfileName);
    public string ActiveProfileLine => Loc.T("prof.active_line", ActiveProfileName);
    public string ProfileChipText => HasActiveProfile ? Loc.T(ProfileModified ? "prof.chip_changed" : "prof.chip", ActiveProfileName) : "";
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
        var path = await Ui.PickOpenFileAsync(Loc.T("prof.open_title"), Loc.T("prof.file_type"), new[] { "*.json" });
        if (path is null)
            return;

        string text;
        try
        {
            if (new FileInfo(path).Length > PortableProfile.MaxBytes)
            {
                Toast(Loc.T("prof.too_large"), true);
                return;
            }
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast(Loc.T("prof.read_failed", ex.Message), true);
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
        if (await AskAsync(Loc.T("prof.switch_now_title"),
            Loc.T("prof.switch_now_text", name) + (result.Missing.Count > 0 ? " " + Loc.Count("prof.switch_now_missing", result.Missing.Count) : ""),
            Loc.T("prof.switch_now_yes"), "gift"))
            await SwitchProfileAsync(name);
    }

    // ---- one profile
    public async Task SwitchProfileAsync(string name)
    {
        if (ProfileModified && !name.Equals(ActiveProfileName, StringComparison.OrdinalIgnoreCase))
        {
            var go = await AskAsync(Loc.T("prof.unsaved_title"), Loc.T("prof.unsaved_text", ActiveProfileName), Loc.T("prof.unsaved_yes"), "alert");
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
        var text = await PromptAsync(Loc.T("prof.rename_title"), "tag", Loc.T("prof.rename_prompt"), name, Loc.T("prof.rename_yes"), ProfileStore.MaxNameLength);
        if (text is null)
            return;
        var result = _svc.RenameProfile(name, text);
        Toast(result.Error ?? result.Message, result.Error is not null);
        Refresh();
    }

    public async Task CopyProfileAsync(string name)
    {
        var text = await PromptAsync(Loc.T("prof.copy_title"), "package", Loc.T("prof.copy_prompt"), Loc.T("prof.copy_default", name), Loc.T("prof.copy_yes"), ProfileStore.MaxNameLength);
        if (text is null)
            return;
        var result = _svc.CopyProfile(name, text);
        Toast(result.Error ?? result.Message, result.Error is not null);
        Refresh();
    }

    public async Task DeleteProfileAsync(string name)
    {
        if (!await AskAsync(Loc.T("prof.delete_title", name), Loc.T("prof.delete_text"), Loc.T("prof.delete_yes"), "alert"))
            return;
        var result = _svc.DeleteProfile(name);
        Toast(result.Error ?? result.Message, result.Error is not null);
        Refresh();
    }

    public async Task ExportProfileAsync(string name)
    {
        if (Ui is null)
            return;
        var path = await Ui.PickSaveFileAsync(Loc.T("prof.save_title"), name + ".sprinkler.json", Loc.T("prof.file_type"), new[] { "*.json" });
        if (path is null)
            return;
        try
        {
            await File.WriteAllTextAsync(path, _svc.ExportProfile(name));
            Toast(Loc.T("prof.saved_share"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProfileException)
        {
            Toast(Loc.T("prof.save_failed", ex.Message), true);
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
