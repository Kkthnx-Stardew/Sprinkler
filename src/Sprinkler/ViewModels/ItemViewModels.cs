using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

/// <summary>One way to get an update, shown as a button on the update card.</summary>
internal sealed partial class OfferViewModel(ItemViewModel owner, int index, OfferInfo info) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    private OfferInfo _info = info;

    public string Label => Info.Label;
    public string Note => Info.Note;
    public bool Usable => Info.Usable;
    public bool NotUsable => !Info.Usable;
    public string Letter => Info.Source switch { SourceKind.Nexus => "N", SourceKind.CurseForge => "C", SourceKind.GitHub => "G", SourceKind.ModDrop => "M", _ => "?" };

    public IBrush BadgeBrush => Info.Source switch
    {
        SourceKind.Nexus => new SolidColorBrush(Color.Parse("#B0520D")),
        SourceKind.CurseForge => new SolidColorBrush(Color.Parse("#C24A22")),
        SourceKind.GitHub => new SolidColorBrush(Color.Parse("#3A3F4B")),
        _ => new SolidColorBrush(Color.Parse("#2E6BA8")),
    };

    public bool IsAuto => Info.Usable && Info.Mode == DeliveryMode.Auto;
    public bool IsClick => Info.Usable && Info.Mode != DeliveryMode.Auto;
    public string ModeText => Loc.T(Info.Mode switch { DeliveryMode.Auto => "mode.auto", DeliveryMode.OneClick => "mode.oneclick", _ => "mode.manual" });
    public bool IsChosen => owner.Info.Chosen == Index;

    public void Update(OfferInfo info)
    {
        Info = info;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void Choose() => owner.Choose(Index);
}

/// <summary>An update or install card with its source choices, progress and result.</summary>
internal sealed partial class ItemViewModel : ObservableObject, IModArt
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private ItemInfo _info;

    public ItemViewModel(MainViewModel main, ItemInfo info)
    {
        _main = main;
        _info = info;
        Id = info.Id;
        Refill(info);
    }

    public string Id { get; }

    public ObservableCollection<OfferViewModel> Offers { get; } = new();
    public ObservableCollection<OfferViewModel> OtherOffers { get; } = new();
    public ObservableCollection<string> Details { get; } = new();
    public ObservableCollection<string> Changelog { get; } = new();

    public string Name => Info.Name;
    public string Target => Info.Target;
    public string? Installed => Info.Installed;
    public bool HasInstalled => Info.Installed is not null;
    public bool IsNew => Info.IsNew;
    public bool HasParts => Info.Parts > 1;
    public string PartsText => Loc.Count("n.part", Info.Parts);
    public string? Why => Info.Why;
    public bool HasWhy => !string.IsNullOrEmpty(Info.Why);
    public string Message => Info.Message;
    public bool HasMessage => !string.IsNullOrEmpty(Info.Message);
    public string? PageUrl => Info.PageUrl;
    public bool HasChangelog => Info.Changelog.Count > 0;
    public bool ShowPageLink => !HasChangelog && !string.IsNullOrEmpty(Info.PageUrl);
    public string ChangelogTitle => Loc.T("card.changed_in", Info.Target);
    public bool HasDetails => Info.Details.Count > 0;
    public bool HasHistory => !string.IsNullOrEmpty(Info.HistoryId);

    public bool IsReady => Info.Status == ItemStatus.Ready;
    public bool IsQueued => Info.Status == ItemStatus.Queued;
    public bool IsWorking => Info.Status == ItemStatus.Working;
    public bool IsWaiting => Info.Status == ItemStatus.WaitingForYou;
    public bool IsDone => Info.Status == ItemStatus.Done;
    public bool IsFailed => Info.Status == ItemStatus.Failed;
    public bool IsSkipped => Info.Status == ItemStatus.Skipped;
    public bool CanEdit => Info.Status is ItemStatus.Ready or ItemStatus.Failed or ItemStatus.Skipped;
    public bool ShowSources => CanEdit && Offers.Count > 0;
    public bool ShowLookingUp => CanEdit && Offers.Count == 0 && !IsFailed;
    public string LookingUpText => string.IsNullOrEmpty(Info.Message) ? Loc.T("card.looking_up") : Info.Message;
    public bool CanSelect => Info.Offers.Any(o => o.Usable);
    public bool ShowChosenNote => ShowSources && ChosenNote.Length > 0;
    public string ChosenNote => Info.Chosen >= 0 && Info.Chosen < Info.Offers.Count ? Info.Offers[Info.Chosen].Note : "";
    public bool ShowSkipVersion => !Info.IsNew && IsReady;
    public bool ShowOtherSources => ShowSources && !Info.AlternativesLoaded;
    public bool ProgressIndeterminate => Info.Progress <= 0 || Info.Progress >= 100;
    public double ProgressValue => Math.Clamp(Info.Progress, 0, 100);
    public bool CardDone => IsDone;
    public bool CardFailed => IsFailed;

    // the mod picture
    public Bitmap? Icon => ArtImages.Get(_main.Art.PathFor(Info.ArtKey));
    public bool HasIcon => Icon is not null;
    public bool NoIcon => Icon is null;
    public string Initial => Avatar.Initial(Info.Name);
    public IBrush AvatarBrush => Avatar.Brush(Info.Name);
    public bool ShowStatusBadge => !IsReady;
    public string? Summary => _main.Art.Peek(Info.ArtKey)?.Summary;

    public string ArtKind => Info.Status switch
    {
        ItemStatus.Done => "check",
        ItemStatus.Failed => "xcircle",
        ItemStatus.Skipped => "skip",
        ItemStatus.Working => "download",
        ItemStatus.WaitingForYou => "pointer",
        ItemStatus.Queued => "clock",
        _ => Info.IsNew ? "gift" : "arrowup",
    };

    public bool Selected
    {
        get => Info.Selected;
        set
        {
            if (value != Info.Selected)
                _main.SetSelected(Id, value);
        }
    }

    public void Update(ItemInfo info)
    {
        Info = info;
        Refill(info);
        OnPropertyChanged(string.Empty);
    }

    private void Refill(ItemInfo info)
    {
        // keep the button objects when the list has the same shape, so a progress tick does not rebuild the card
        if (Offers.Count == info.Offers.Count)
        {
            for (var i = 0; i < Offers.Count; i++)
                Offers[i].Update(info.Offers[i]);
        }
        else
        {
            Offers.Clear();
            for (var i = 0; i < info.Offers.Count; i++)
                Offers.Add(new OfferViewModel(this, i, info.Offers[i]));
        }

        var others = Offers.Where(o => o.Index != info.Chosen && o.Usable).ToList();
        if (!others.SequenceEqual(OtherOffers))
        {
            OtherOffers.Clear();
            foreach (var o in others)
                OtherOffers.Add(o);
        }

        Fmt.Sync(Details, info.Details);
        Fmt.Sync(Changelog, info.Changelog);
    }

    public void Choose(int index) => _main.ChooseSource(Id, index);

    public void NotifyArt() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void SkipVersion() => _main.SkipVersion(Id);

    [RelayCommand]
    private void OpenPage() => _main.OpenUrl(Info.PageUrl);

    [RelayCommand]
    private void Reopen() => _main.Reopen(Id);

    [RelayCommand]
    private void SkipThis() => _main.SkipWaiting(Id);

    [RelayCommand]
    private Task LoadAlternatives() => _main.LoadAlternativesAsync(Id);

    [RelayCommand]
    private Task Undo() => Info.HistoryId is { } id ? _main.UndoAsync(id) : Task.CompletedTask;
}

internal enum ModChip
{
    None,
    Ok,
    Warn,
    Bad,
    Info,
}

/// <summary>One line of the All mods list.</summary>
internal sealed partial class ModRowViewModel : ObservableObject, IModArt
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private ModRow _row;

    public ModRowViewModel(MainViewModel main, ModRow row)
    {
        _main = main;
        _row = row;
    }

    public string Name => Row.Name;
    public string UniqueId => Row.UniqueId;
    public string Version => Row.Version;
    public bool IsContentPack => Row.IsContentPack;
    public string Meta => (string.IsNullOrEmpty(Row.Author) ? "" : Row.Author + " · ") + Row.Note;
    public bool HasNeeds => Row.Needs is { Length: > 0 };
    public string NeedsText => Row.Needs is { Length: > 0 } n ? Loc.T("row.needs", string.Join(", ", n)) : "";
    public bool IsOff => Row.State == RowState.Disabled;
    public bool IsLocked => Row.State == RowState.Bundled;
    public bool IsOn => !IsOff;
    public bool CanToggle => !IsLocked;
    public double RowOpacity => IsOff ? 0.6 : 1;

    // the mod picture
    private string? ArtKey => _main.ArtKeyOf(Row.UniqueId);
    public Bitmap? Icon => ArtImages.Get(_main.Art.PathFor(ArtKey));
    public bool HasIcon => Icon is not null;
    public bool NoIcon => Icon is null;
    public string Initial => Avatar.Initial(Row.Name);
    public IBrush AvatarBrush => Avatar.Brush(Row.UniqueId);
    public string? Summary => _main.Art.Peek(ArtKey)?.Summary;
    public string SwitchTip => Loc.T(IsLocked ? "row.tip_locked" : IsOff ? "row.tip_on" : "row.tip_off");

    public string StatusText
    {
        get
        {
            if (Row.State == RowState.Update)
                return Loc.T("row.update", Row.Target ?? "");
            if (!string.IsNullOrEmpty(Row.SkippedVersion))
                return Loc.T("row.skipped", Row.SkippedVersion);
            return Row.State switch
            {
                RowState.UpToDate => Loc.T("row.state.uptodate"),
                RowState.NoInfo => Loc.T("row.state.nocheck"),
                RowState.Error => Loc.T("row.state.error"),
                RowState.Disabled => Loc.T("row.state.disabled"),
                RowState.Bundled => "Part of SMAPI",
                RowState.Ignored => Loc.T("row.state.ignored"),
                RowState.Warning => Loc.T("row.state.warning"),
                _ => "",
            };
        }
    }

    private ModChip Chip =>
        Row.State == RowState.Update ? ModChip.Warn
        : !string.IsNullOrEmpty(Row.SkippedVersion) ? ModChip.None
        : Row.State switch { RowState.UpToDate => ModChip.Ok, RowState.Error => ModChip.Bad, RowState.Warning => ModChip.Warn, _ => ModChip.None };

    public bool ChipOk => Chip == ModChip.Ok;
    public bool ChipWarn => Chip == ModChip.Warn;
    public bool ChipBad => Chip == ModChip.Bad;
    public bool HasUnofficial => !string.IsNullOrEmpty(Row.Unofficial);
    public string UnofficialText => Loc.T("row.unofficial", Row.Unofficial ?? "");
    public bool HasUrl => !string.IsNullOrEmpty(Row.Url);
    public bool HasSkipped => !string.IsNullOrEmpty(Row.SkippedVersion);
    public string StopSkippingText => Loc.T("row.stop_skipping", Row.SkippedVersion ?? "");
    public bool CanIgnore => Row.State is not (RowState.Bundled or RowState.Disabled);
    public bool IsIgnored => Row.State == RowState.Ignored;
    public string IgnoreText => Loc.T(IsIgnored ? "row.ignore_off" : "row.ignore_on");

    // grouping under a parent mod
    private bool _indented;

    public bool Indented
    {
        get => _indented;
        set
        {
            if (SetProperty(ref _indented, value))
                OnPropertyChanged(nameof(Indent));
        }
    }

    public Avalonia.Thickness Indent => new(_indented ? 30 : 0, 0, 0, 0);

    // private note
    public string NoteText => _main.NoteFor(Row.UniqueId);
    public bool HasNote => NoteText.Length > 0;
    public string NoteMenuText => Loc.T(HasNote ? "row.note_edit" : "row.note_add");

    // Nexus
    public long? NexusId => ArtKey is { } k && k.StartsWith("nexus:", StringComparison.Ordinal) && long.TryParse(k[6..], out var id) ? id : null;
    public bool IsEndorsed => NexusId is { } id && _main.IsEndorsed(id);
    public bool CanEndorse => NexusId is not null && _main.NexusConnected;
    public string EndorseText => Loc.T(IsEndorsed ? "row.endorse_off" : "row.endorse_on");

    public bool HasConfig => Row.HasConfig;
    public bool CanRemove => !IsLocked;

    [RelayCommand]
    private Task EditNote() => _main.EditNoteAsync(this);

    [RelayCommand]
    private Task Remove() => _main.RemoveModAsync(this);

    [RelayCommand]
    private Task ToggleEndorsement() => _main.ToggleEndorsementAsync(this);

    [RelayCommand]
    private void OpenConfig() => _main.OpenConfig(this);

    public void NotifyArt() => OnPropertyChanged(string.Empty);

    public void Update(ModRow row)
    {
        Row = row;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task Toggle() => _main.ToggleModAsync(this, !IsOn);

    [RelayCommand]
    private void OpenPage() => _main.OpenUrl(Row.Url);

    [RelayCommand]
    private void OpenUnofficial() => _main.OpenUrl(Row.UnofficialUrl);

    [RelayCommand]
    private void ShowFolder() => _main.RevealMod(Row.UniqueId);

    [RelayCommand]
    private void StopSkipping() => _main.UnskipVersion(Row.UniqueId);

    [RelayCommand]
    private void ToggleIgnore() => _main.SetIgnored(Row.UniqueId, !IsIgnored);
}

internal sealed partial class HistoryViewModel(MainViewModel main, HistoryInfo info) : ObservableObject
{
    [ObservableProperty]
    private HistoryInfo _info = info;

    public string Name => Info.Name;
    public string ArtKind => Info.Undone ? "skip" : Info.Removed ? "trash" : Info.From is not null ? "arrowup" : "gift";
    public string Detail => Info.Removed
        ? (Info.From is not null ? Loc.T("hist.removed_ver", Info.From, Fmt.Time(Info.When)) : Loc.T("hist.removed", Fmt.Time(Info.When)))
        : (Info.From is not null ? Loc.T("hist.update", Info.From, Info.To, Info.Source, Fmt.Time(Info.When)) : Loc.T("hist.new_install", Info.To, Info.Source, Fmt.Time(Info.When)));
    public bool Undone => Info.Undone;
    public bool CanUndo => !Info.Undone && Info.CanUndo;
    public bool BackupGone => !Info.Undone && !Info.CanUndo;

    public void Update(HistoryInfo i)
    {
        Info = i;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task Undo() => main.UndoAsync(Info.Id);
}

internal sealed partial class FoundViewModel(MainViewModel main, FoundInfo info) : ObservableObject
{
    [ObservableProperty]
    private FoundInfo _info = info;

    public ObservableCollection<string> Lines { get; } = new(info.Lines);
    public string Id => Info.Id;
    public string Header => Loc.T("found.header", Info.File, Info.Origin);
    public bool HasError => !string.IsNullOrEmpty(Info.Error);
    public string Error => Info.Error ?? "";
    public bool CanInstall => !(HasError && Info.Lines.Count == 0);
    public bool Busy => Info.Busy;
    public string InstallText => Loc.T(Info.Busy ? "found.installing" : "found.install");

    public void Update(FoundInfo i)
    {
        Info = i;
        Fmt.Sync(Lines, i.Lines);
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanClick))]
    private Task Install() => main.InstallFoundAsync(Info.Id);

    private bool CanClick() => !Info.Busy;

    [RelayCommand]
    private void Dismiss() => main.DismissFound(Info.Id);
}
