using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

/// <summary>One saved profile in the list.</summary>
internal sealed partial class ProfileViewModel(MainViewModel main, ProfileSummary info) : ObservableObject
{
    [ObservableProperty]
    private ProfileSummary _info = info;

    public string Name => Info.Name;
    public bool IsActive => Info.IsActive;
    public bool CanSwitch => !Info.IsActive;
    public bool IsCollection => Info.CollectionName is not null;
    public string CollectionText => Info.Curator is null ? Loc.T("prof.collection") : Loc.T("prof.collection_by", Info.Curator);
    public bool Pending => Info.Pending && Info.ToInstall > 0;
    public bool HasMissing => Info.MissingCount > 0;
    public string MissingChipText => Loc.T("prof.detail_missing", Info.MissingCount);

    public string Detail
    {
        get
        {
            var parts = new List<string> { Loc.Count("n.mod", Info.ModCount) };
            if (Info.ConfigCount > 0)
                parts.Add(Loc.T("prof.detail_configs", Info.ConfigCount));
            if (Info.ToInstall > 0 && Info.Pending)
                parts.Add(Loc.T("prof.detail_to_install", Info.ToInstall));
            parts.Add(Loc.T("prof.detail_saved", Fmt.Ago(Info.Updated)));
            return string.Join(" · ", parts);
        }
    }

    public void Update(ProfileSummary i)
    {
        Info = i;
        OnPropertyChanged(string.Empty);
    }

    public bool CanPlay => main.CanPlay;
    public bool CanSwitchAndPlay => CanSwitch && main.CanPlay;
    public bool CanPlayActive => IsActive && main.CanPlay;

    [RelayCommand]
    private Task Switch() => main.SwitchProfileAsync(Info.Name);

    [RelayCommand]
    private Task SwitchAndPlay() => main.SwitchAndPlayAsync(Info.Name);

    [RelayCommand]
    private void Play() => main.PlayNow();

    [RelayCommand]
    private Task Rename() => main.RenameProfileAsync(Info.Name);

    [RelayCommand]
    private Task Copy() => main.CopyProfileAsync(Info.Name);

    [RelayCommand]
    private Task Export() => main.ExportProfileAsync(Info.Name);

    [RelayCommand]
    private Task Delete() => main.DeleteProfileAsync(Info.Name);

    [RelayCommand]
    private Task ForgetMissing() => main.ForgetMissingAsync(Info.Name);

    [RelayCommand]
    private void StopInstalling() => main.StopInstallingCollection(Info.Name);
}
