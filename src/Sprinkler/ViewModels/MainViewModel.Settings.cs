using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

// The "Nexus and settings" screen.
internal sealed partial class MainViewModel
{
    // ---- Nexus account
    [ObservableProperty]
    private string _nexusKeyInput = "";

    [ObservableProperty]
    private bool _nexusKeyBusy;

    [ObservableProperty]
    private string _nexusKeyError = "";

    public bool HasNexusKeyError => !string.IsNullOrEmpty(NexusKeyError);

    partial void OnNexusKeyErrorChanged(string value) => OnPropertyChanged(nameof(HasNexusKeyError));

    public string ConnectButtonText => NexusKeyBusy ? "Checking..." : "Connect";
    public bool CanConnect => !NexusKeyBusy;

    partial void OnNexusKeyBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(CanConnect));
    }

    public string NexusUser => Snap?.Nexus.User ?? "";
    public string NexusAccountKind => Snap?.Nexus.Premium == true ? " (Premium)." : " (free account).";
    public string NexusAccountNote => Snap?.Nexus.Premium == true
        ? "With Premium, the app downloads from Nexus by itself. No clicks needed."
        : "With a free account, Nexus wants one click per mod on its website. The app makes that click quick and does everything else.";
    public bool HasNexusMessage => !string.IsNullOrEmpty(Snap?.Nexus.Message);
    public string NexusMessage => Snap?.Nexus.Message ?? "";
    public string KeyStorageText => "The key stays on this computer (" + (Snap?.Nexus.KeyStorage ?? "stored privately") + ") and is only sent to Nexus. Nexus allows a personal key for your own use, so do not share it. You can skip this: the app still works, you just press Nexus download buttons yourself.";

    [RelayCommand]
    private async Task ConnectNexus()
    {
        NexusKeyBusy = true;
        NexusKeyError = "";
        try
        {
            var error = await _svc.ConnectNexusAsync(NexusKeyInput);
            NexusKeyError = error ?? "";
            if (error is null)
            {
                NexusKeyInput = "";
                Toast("Nexus connected.");
            }
        }
        finally
        {
            NexusKeyBusy = false;
        }
    }

    [RelayCommand]
    private Task DisconnectNexus() => Run(_svc.DisconnectNexusAsync);

    // ---- Nexus links
    [ObservableProperty]
    private string _nxmNote = "";

    public bool HasNxmNote => !string.IsNullOrEmpty(NxmNote);

    partial void OnNxmNoteChanged(string value) => OnPropertyChanged(nameof(HasNxmNote));

    public bool NxmUnsupported => Snap?.Nexus.NxmSupported == false;
    public bool NxmOn => Snap?.Nexus is { NxmSupported: true, Nxm: true };
    public bool NxmCanOffer => Snap?.Nexus is { NxmSupported: true, Nxm: false };
    public bool NxmNeedsKey => NxmCanOffer && !NexusConnected;
    public bool NxmOther => NxmCanOffer && Snap?.Nexus.OtherHandler == true;

    [RelayCommand]
    private void TurnNxmOn()
    {
        try
        {
            NxmNote = _svc.RegisterNxm();
        }
        catch (InvalidOperationException ex)
        {
            NxmNote = ex.Message;
        }
        Refresh();
    }

    [RelayCommand]
    private void TurnNxmOff()
    {
        _svc.UnregisterNxm();
        NxmNote = "";
        Refresh();
    }

    // ---- where to download from
    public bool PolicyNexusFirst
    {
        get => Snap?.Settings.Policy != SourcePolicy.InstantFirst;
        set
        {
            if (value)
            {
                _svc.SetPolicy(SourcePolicy.NexusFirst);
                Refresh();
            }
        }
    }

    public bool PolicyInstantFirst
    {
        get => Snap?.Settings.Policy == SourcePolicy.InstantFirst;
        set
        {
            if (value)
            {
                _svc.SetPolicy(SourcePolicy.InstantFirst);
                Refresh();
            }
        }
    }

    // ---- folders
    private string _downloadsInput = "";
    private bool _downloadsEdited;

    public string DownloadsInput
    {
        get => _downloadsInput;
        set
        {
            if (SetProperty(ref _downloadsInput, value))
                _downloadsEdited = true;
        }
    }

    [RelayCommand]
    private void SaveDownloads()
    {
        _svc.SetDownloadsFolder(DownloadsInput);
        _downloadsEdited = false;
        Toast("Saved.");
        Refresh();
    }

    [RelayCommand]
    private async Task BrowseDownloads()
    {
        if (Ui is null)
            return;
        var picked = await Ui.PickFolderAsync("Choose the folder your browser saves downloads to");
        if (picked is not null)
        {
            DownloadsInput = picked;
            SaveDownloads();
        }
    }

    // ---- look
    public bool ThemeIsAuto => (Snap?.Settings.Theme ?? "auto") == "auto";
    public bool ThemeIsLight => Snap?.Settings.Theme == "light";
    public bool ThemeIsDark => Snap?.Settings.Theme == "dark";
    public string ThemeIcon => ThemeIsDark ? "moon" : "sun";

    [RelayCommand]
    private void PickTheme(string theme) => SetTheme(theme);

    // ---- Linux application menu
    public bool ShowShortcut => Snap?.App.Shortcut is not null;
    public bool ShortcutInstalled => Snap?.App.Shortcut?.Installed == true;
    public bool ShortcutMissing => ShowShortcut && !ShortcutInstalled;

    [RelayCommand]
    private void AddShortcut()
    {
        try
        {
            LinuxShortcut.Install();
            Toast("Added to your application menu.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toast("Could not change the menu shortcut: " + ex.Message, true);
        }
        Refresh();
    }

    [RelayCommand]
    private void RemoveShortcut()
    {
        LinuxShortcut.Remove();
        Toast("Removed from the menu.");
        Refresh();
    }
}
