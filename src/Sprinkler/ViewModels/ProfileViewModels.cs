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
    public string CollectionText => Info.Curator is null ? "Nexus collection" : "Nexus collection by " + Info.Curator;
    public bool Pending => Info.Pending && Info.ToInstall > 0;

    public string Detail
    {
        get
        {
            var parts = new List<string> { Fmt.Plural(Info.ModCount, "mod", "mods") };
            if (Info.MissingCount > 0)
                parts.Add($"{Info.MissingCount} not installed here");
            if (Info.ConfigCount > 0)
                parts.Add($"keeps settings for {Info.ConfigCount}");
            if (Info.ToInstall > 0 && Info.Pending)
                parts.Add($"{Info.ToInstall} to install under Updates");
            parts.Add("saved " + Fmt.Ago(Info.Updated));
            return string.Join(" · ", parts);
        }
    }

    public void Update(ProfileSummary i)
    {
        Info = i;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task Switch() => main.SwitchProfileAsync(Info.Name);

    [RelayCommand]
    private Task Rename() => main.RenameProfileAsync(Info.Name);

    [RelayCommand]
    private Task Copy() => main.CopyProfileAsync(Info.Name);

    [RelayCommand]
    private Task Export() => main.ExportProfileAsync(Info.Name);

    [RelayCommand]
    private Task Delete() => main.DeleteProfileAsync(Info.Name);

    [RelayCommand]
    private void StopInstalling() => main.StopInstallingCollection(Info.Name);
}
