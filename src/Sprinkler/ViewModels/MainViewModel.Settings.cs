using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;
using Avalonia.Threading;

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

    public string ConnectButtonText => Loc.T(NexusKeyBusy ? "set.checking" : "set.connect");
    public bool CanConnect => !NexusKeyBusy;

    partial void OnNexusKeyBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(CanConnect));
    }

    public string NexusUser => Snap?.Nexus.User ?? "";
    public string NexusConnectedLine => Loc.T("set.connected_as", NexusUser, Loc.T(Snap?.Nexus.Premium == true ? "set.kind_premium" : "set.kind_free"));
    public string NexusAccountNote => Snap?.Nexus.Premium == true
        ? Loc.T("set.premium_note")
        : Loc.T("set.free_note");
    public bool HasNexusMessage => !string.IsNullOrEmpty(Snap?.Nexus.Message);
    public string NexusMessage => Snap?.Nexus.Message ?? "";
    public string KeyStorageText => Loc.T("set.key_storage", Snap?.Nexus.KeyStorage ?? Loc.T("set.key_private"));

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
                Toast(Loc.T("set.connected"));
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
        Toast(Loc.T("set.saved"));
        Refresh();
    }

    [RelayCommand]
    private async Task BrowseDownloads()
    {
        if (Ui is null)
            return;
        var picked = await Ui.PickFolderAsync(Loc.T("set.pick_downloads"));
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

    // ---- language
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; private set; } = BuildLanguageChoices();

    private static List<LanguageChoice> BuildLanguageChoices() =>
        new[] { new LanguageChoice("auto", Loc.T("set.lang_auto")) }.Concat(Loc.All.Select(l => new LanguageChoice(l.Code, l.NativeName))).ToList();

    public LanguageChoice? SelectedLanguage
    {
        get
        {
            var code = Snap?.Settings.Language ?? "auto";
            return LanguageChoices.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        }
        set
        {
            if (value is not null && !value.Code.Equals(Snap?.Settings.Language ?? "auto", StringComparison.OrdinalIgnoreCase))
                _svc.SetLanguage(value.Code);
        }
    }

    /// <summary>The words changed: everything built from text is built again, so no screen keeps the old language.</summary>
    private void OnLanguageChanged()
    {
        if (_disposed)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            _itemMap.Clear();
            _rowMap.Clear();
            _historyMap.Clear();
            _foundMap.Clear();
            _profileMap.Clear();
            LanguageChoices = BuildLanguageChoices();
            Refresh();
            OnPropertyChanged(nameof(LanguageChoices));
            OnPropertyChanged(nameof(SelectedLanguage));
        });
    }

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
            Toast(Loc.T("set.menu_added"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toast(Loc.T("set.menu_failed", ex.Message), true);
        }
        Refresh();
    }

    [RelayCommand]
    private void RemoveShortcut()
    {
        LinuxShortcut.Remove();
        Toast(Loc.T("set.menu_removed"));
        Refresh();
    }
}
