using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

// Adding a Nexus collection from the Profiles tab.
internal sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLookUp))]
    private string _collectionAddress = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLookUp), nameof(LookUpText))]
    private bool _lookingUp;

    public bool CanLookUp => HasGame && !LookingUp && !string.IsNullOrWhiteSpace(CollectionAddress);
    public string LookUpText => LookingUp ? "Looking it up..." : "Look it up";

    [RelayCommand]
    private async Task LookUpCollection()
    {
        LookingUp = true;
        try
        {
            CollectionRevision collection;
            try
            {
                collection = await _svc.LookUpCollectionAsync(CollectionAddress);
            }
            catch (CollectionException ex)
            {
                Toast(ex.Message, true);
                return;
            }

            var dialog = new CollectionPreviewDialogViewModel(_svc.PreviewCollection(collection));
            ShowDialog(dialog);
            var include = await dialog.Result;
            if (include is null)
                return;

            var result = _svc.AddCollection(collection, include.Value);
            Refresh();
            Toast(result.Error ?? result.Message, result.Error is not null);
            if (result.Error is null)
                CollectionAddress = "";
        }
        finally
        {
            LookingUp = false;
        }
    }

    public void StopInstallingCollection(string profileName)
    {
        var result = _svc.StopInstalling(profileName);
        Toast(result.Error ?? result.Message, result.Error is not null);
        Refresh();
    }
}
