using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprinkler.Core;

namespace Sprinkler.ViewModels;

// Everything the person can do. Each action calls the service and lets the next snapshot show the result.
internal sealed partial class MainViewModel
{
    // ---- top bar and general
    [RelayCommand]
    private Task Check() => Run(_svc.CheckAsync);

    [RelayCommand]
    private void Launch()
    {
        var error = _svc.LaunchGame();
        if (error is null)
            Toast("Starting Stardew Valley with SMAPI...");
        else
            Toast(error, true);
    }

    [RelayCommand]
    private void ShowTourFromStart() => ShowTour(0);

    [RelayCommand]
    private void GoTo(AppTab tab) => Tab = tab;

    [RelayCommand]
    private void CycleTheme()
    {
        var current = Snap?.Settings.Theme ?? "auto";
        SetTheme(current == "auto" ? "light" : current == "light" ? "dark" : "auto");
        Toast("Look: " + (Snap?.Settings.Theme ?? "auto") switch { "light" => "light", "dark" => "dark", _ => "follows your system" });
    }

    public void SetTheme(string theme)
    {
        _svc.SetPrefs(theme, null);
        App.ApplyTheme(theme);
        Refresh();
    }

    [RelayCommand]
    private void DismissError() => _svc.Dismiss();

    [RelayCommand]
    private void OpenSmapiUpdate() => OpenUrl(SmapiUpdateUrl);

    [RelayCommand]
    private void OpenAppUpdate() => OpenUrl(Snap?.App.Update?.Url);

    [RelayCommand]
    private void OpenRepo() => OpenUrl(RepoUrl);

    [RelayCommand]
    private void OpenNexusKeyPage() => OpenUrl("https://www.nexusmods.com/settings/api-keys");

    public void OpenUrl(string? url)
    {
        if (!UrlPolicy.Open(url))
            Toast("That address is not one the app opens.", true);
    }

    [RelayCommand]
    private async Task ReportProblem()
    {
        var text = _svc.Diagnostics();
        var copied = false;
        if (Ui is not null)
        {
            try
            {
                await Ui.CopyTextAsync(text);
                copied = true;
            }
            catch (Exception ex)
            {
                Log.Write("Could not copy the report: " + ex.Message);
            }
        }
        Toast(copied ? "Details copied. Paste them into the new issue." : "Opening the issue page.");
        OpenUrl(RepoUrl + "/issues/new");
    }

    [RelayCommand]
    private void Reveal(string what)
    {
        var path = what switch
        {
            "backups" => AppEnvironment.BackupsDir,
            "logs" => AppEnvironment.LogsDir,
            "mods" => _svc.Settings.Data.ModsPath,
            "downloads" => _svc.Settings.DownloadsPath,
            _ => null,
        };
        if (path is not null && Directory.Exists(path))
            Platform.OpenFolder(path);
        else
            Toast("That folder does not exist yet.", true);
    }

    public void RevealMod(string uniqueId)
    {
        var path = _svc.ModFolder(uniqueId);
        if (path is not null && Directory.Exists(path))
            Platform.OpenFolder(path);
    }

    // ---- choose the game
    [ObservableProperty]
    private string _gamePathInput = "";

    [ObservableProperty]
    private string _gamePathError = "";

    public bool HasGamePathError => !string.IsNullOrEmpty(GamePathError);

    partial void OnGamePathErrorChanged(string value) => OnPropertyChanged(nameof(HasGamePathError));

    [RelayCommand]
    private Task UseTypedGamePath() => SetGameFolderAsync(GamePathInput);

    public async Task SetGameFolderAsync(string path)
    {
        var error = await _svc.SetGameFolderAsync(path);
        GamePathError = error ?? "";
        if (error is null)
            GamePathInput = "";
    }

    [RelayCommand]
    private async Task BrowseGameFolder()
    {
        if (Ui is null)
            return;
        var picked = await Ui.PickFolderAsync("Choose your Stardew Valley folder");
        if (picked is not null)
            await SetGameFolderAsync(picked);
    }

    [RelayCommand]
    private void ConfirmWelcome() => _svc.ConfirmWelcome();

    // ---- update cards
    public void SetSelected(string id, bool selected) => _svc.SetSelected(id, selected);

    public void ChooseSource(string id, int index) => _svc.ChooseSource(id, index);

    public void SkipVersion(string id) => _svc.SkipVersion(id);

    public void UnskipVersion(string uniqueId) => _svc.UnskipVersion(uniqueId);

    public void Reopen(string id) => _svc.ReopenPage(id);

    public void SkipWaiting(string id) => _svc.SkipWaiting(id);

    public Task LoadAlternativesAsync(string id) => Run(() => _svc.LoadAlternativesAsync(id));

    [RelayCommand]
    private void SelectAll() => _svc.SetAllSelected(true);

    [RelayCommand]
    private void SelectNone() => _svc.SetAllSelected(false);

    [RelayCommand]
    private void BackToReview() => _svc.BackToReview();

    [RelayCommand]
    private void DismissLog() => _svc.DismissLog();

    [RelayCommand]
    private async Task UpdateSelected()
    {
        var chosen = Items.Where(i => i.IsReady && i.Selected && i.CanSelect).ToList();
        if (chosen.Count == 0)
            return;

        OfferInfo OfferOf(ItemViewModel i) => i.Info.Offers[Math.Clamp(i.Info.Chosen, 0, i.Info.Offers.Count - 1)];
        var auto = chosen.Where(i => OfferOf(i).Mode == DeliveryMode.Auto).ToList();
        var hand = chosen.Where(i => OfferOf(i).Mode != DeliveryMode.Auto).ToList();

        var lines = new List<DialogLine>();
        if (auto.Count > 0)
            lines.Add(new DialogLine(
                Fmt.Plural(auto.Count, "mod", "mods") + " will download and install by themselves:",
                string.Join(", ", auto.Select(i => $"{i.Name} (from {OfferOf(i).Label})"))));
        if (hand.Count > 0)
            lines.Add(new DialogLine(
                Fmt.Plural(hand.Count, "mod", "mods") + " need a click on a website. I open the pages three at a time and install each file the moment it lands in your Downloads folder:",
                string.Join(", ", hand.Select(i => $"{i.Name} ({OfferOf(i).Label})"))));

        var dialog = new ConfirmDialogViewModel(
            $"Update {Fmt.Plural(chosen.Count, "mod", "mods")}?", "arrowup", "Yes, update", "Not yet", lines,
            new[]
            {
                "Each mod folder is backed up first. Your config.json files are kept.",
                "If anything goes wrong while copying, that mod is put back exactly as it was.",
                "Make sure Stardew Valley is closed.",
            });
        ShowDialog(dialog);
        if (!await dialog.Result)
            return;
        Tab = AppTab.Updates;
        var ids = chosen.Select(i => i.Id).ToList();
        _ = Run(() => _svc.ApplyAsync(ids));
    }

    public async Task UndoAsync(string historyId)
    {
        if (!await AskAsync("Put the previous version back?", "Your current version of this mod will be replaced by the backup.", "Yes, undo it", "shield"))
            return;
        var error = await _svc.UndoAsync(historyId);
        if (error is null)
            Toast("Undone. The old version is back.");
        else
            Toast(error, true);
    }

    public Task InstallFoundAsync(string id) => Run(() => _svc.InstallFoundAsync(id));

    public void DismissFound(string id) => _svc.DismissFound(id);

    // ---- dropped files
    public async Task HandleDroppedAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!new[] { ".zip", ".7z", ".rar" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                Toast(name + " is not a .zip, .7z or .rar file.", true);
                continue;
            }
            Toast("Reading " + name + "...");
            try
            {
                // work on a copy, so the file the person dropped is never moved or deleted
                Directory.CreateDirectory(AppEnvironment.StagingDir);
                var staged = Path.Combine(AppEnvironment.StagingDir, Guid.NewGuid().ToString("N")[..8] + "-" + name);
                File.Copy(path, staged, overwrite: true);
                await _svc.AddUploadedFileAsync(staged, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Toast("Could not read " + name + ": " + ex.Message, true);
            }
        }
        Tab = AppTab.Updates;
    }

    /// <summary>A Nexus "Mod Manager Download" link, sent by another copy of the app or given on the command line.</summary>
    public Task HandleNxmAsync(string url) => Run(() => _svc.HandleNxmAsync(url));

    // ---- All mods
    private string _modFilter = "all";

    [ObservableProperty]
    private string _modSearch = "";

    [ObservableProperty]
    private int _modSortIndex;

    partial void OnModSearchChanged(string value) => RebuildMods();

    partial void OnModSortIndexChanged(int value) => RebuildMods();

    public void SetModFilter(string key)
    {
        _modFilter = key;
        RebuildMods();
    }

    private static readonly Dictionary<RowState, int> StateRank = new()
    {
        [RowState.Update] = 0, [RowState.Error] = 1, [RowState.NoInfo] = 2, [RowState.Warning] = 3,
        [RowState.UpToDate] = 4, [RowState.Ignored] = 5, [RowState.Disabled] = 6, [RowState.Bundled] = 7,
    };

    private static bool FilterMatches(string key, ModRow r) => key switch
    {
        "update" => r.State == RowState.Update,
        "ok" => r.State == RowState.UpToDate,
        "needs" => r.Needs is { Length: > 0 },
        "nocheck" => r.State is RowState.NoInfo or RowState.Error,
        "off" => r.State is RowState.Disabled or RowState.Ignored,
        "config" => r.HasConfig,
        _ => true,
    };

    private void RebuildMods()
    {
        var rows = _rowMap.Values.ToList();
        foreach (var chip in ModChips)
        {
            var count = rows.Count(v => FilterMatches(chip.Key, v.Row));
            chip.Text = $"{ChipLabel(chip.Key)} {count}";
            chip.IsOn = chip.Key == _modFilter;
        }

        var term = ModSearch.Trim();
        var shown = rows
            .Where(v => FilterMatches(_modFilter, v.Row))
            .Where(v => term.Length == 0 || (v.Row.Name + " " + v.Row.Author + " " + v.Row.UniqueId).Contains(term, StringComparison.OrdinalIgnoreCase));

        IOrderedEnumerable<ModRowViewModel> ordered = ModSortIndex switch
        {
            2 => shown.OrderBy(v => v.Row.Author ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            1 => shown.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => shown.OrderBy(v => StateRank.GetValueOrDefault(v.Row.State, 9)).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var list = ordered.ToList();
        if (GroupPacks)
            list = GroupUnderParents(list);
        else
            foreach (var v in list)
                v.Indented = false;
        SyncList(Mods, list);
        OnPropertyChanged(nameof(NoModMatches));
    }

    /// <summary>
    /// Puts each content pack right under the mod it belongs to, indented, when that mod is also in the list.
    /// A pack whose mod is missing, filtered out or itself a pack stays where the sort put it.
    /// </summary>
    internal static List<ModRowViewModel> GroupUnderParents(List<ModRowViewModel> sorted)
    {
        var shownIds = sorted.Select(v => v.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool UnderParent(ModRowViewModel v) =>
            v.Row.ParentId is { } parent && !parent.Equals(v.UniqueId, StringComparison.OrdinalIgnoreCase) && shownIds.Contains(parent)
            && sorted.Any(p => p.UniqueId.Equals(parent, StringComparison.OrdinalIgnoreCase) && p.Row.ParentId is null);

        var packsByParent = sorted.Where(UnderParent).ToLookup(v => v.Row.ParentId!, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModRowViewModel>();
        foreach (var v in sorted)
        {
            if (UnderParent(v))
                continue;
            v.Indented = false;
            result.Add(v);
            foreach (var pack in packsByParent[v.UniqueId])
            {
                pack.Indented = true;
                result.Add(pack);
            }
        }
        return result;
    }

    public bool GroupPacks
    {
        get => Snap?.Settings.GroupContentPacks ?? true;
        set
        {
            if (value == GroupPacks)
                return;
            _svc.SetGroupContentPacks(value);
            Refresh();
        }
    }

    // ---- actions on one mod
    public string NoteFor(string uniqueId) => Snap?.ModNotes.TryGetValue(uniqueId, out var note) == true ? note : "";

    public bool IsEndorsed(long nexusId) => Snap?.Endorsed.Contains(nexusId) == true;

    public async Task EditNoteAsync(ModRowViewModel row)
    {
        var text = await PromptAsync("Note about " + row.Name, "book", "Only you see this. Leave it empty to remove it.", NoteFor(row.UniqueId), "Save note", 300);
        if (text is null)
            return;
        _svc.SetModNote(row.UniqueId, text);
        Refresh();
    }

    public void OpenConfig(ModRowViewModel row)
    {
        var folder = _svc.ModFolder(row.UniqueId);
        var file = folder is null ? null : Path.Combine(folder, "config.json");
        if (file is null || !File.Exists(file))
        {
            Toast("This mod has no settings file.", true);
            return;
        }
        try
        {
            Platform.OpenFile(file);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Toast("Could not open the settings file: " + ex.Message, true);
        }
    }

    public async Task RemoveModAsync(ModRowViewModel row, bool force = false)
    {
        if (!force && !await AskAsync($"Remove {row.Name}?",
            "The mod folder is backed up first and the removal appears in History with an Undo button, so you can bring it back with its settings.",
            "Remove it", "alert"))
            return;

        var (error, dependents) = await _svc.RemoveModAsync(row.UniqueId, force);
        Refresh();
        if (error is not null)
        {
            Toast(error, true);
            return;
        }
        if (dependents is { Length: > 0 })
        {
            if (await AskAsync($"Remove {row.Name} anyway?", "These mods need it and will stop loading: " + string.Join(", ", dependents) + ".", "Remove anyway", "alert"))
                await RemoveModAsync(row, force: true);
            return;
        }
        Toast($"{row.Name} was removed. Undo it from History if you change your mind.");
    }

    public async Task ToggleEndorsementAsync(ModRowViewModel row)
    {
        if (row.NexusId is not { } id)
            return;
        var (ok, message) = await _svc.SetEndorsementAsync(id, !IsEndorsed(id), row.Version);
        Refresh();
        Toast(message, !ok);
    }

    private static string ChipLabel(string key) => key switch
    {
        "update" => "Updates",
        "ok" => "Up to date",
        "needs" => "Missing requirements",
        "nocheck" => "Cannot check",
        "off" => "Off or skipped",
        "config" => "Has settings",
        _ => "All",
    };

    [RelayCommand]
    private void ShowFilter(string key)
    {
        SetModFilter(key);
        Tab = AppTab.Mods;
    }

    public async Task ToggleModAsync(ModRowViewModel row, bool enable, bool force = false)
    {
        var (error, dependents) = await _svc.SetModEnabledAsync(row.UniqueId, enable, force);
        Refresh();
        if (error is not null)
        {
            Toast(error, true);
            return;
        }
        if (dependents is { Length: > 0 })
        {
            var ok = await AskAsync("Turn off " + row.Name + "?", "These mods need it and will stop loading: " + string.Join(", ", dependents) + ".", "Turn it off anyway", "alert");
            if (ok)
                await ToggleModAsync(row, enable, force: true);
            return;
        }
        Toast(enable ? row.Name + " is on again." : row.Name + " is off. SMAPI skips it next time you play.");
    }

    public void SetIgnored(string uniqueId, bool ignore) => _svc.SetIgnored(uniqueId, ignore);

    // ---- keep a few values fresh without fighting the person's typing
    private void SyncSettingsFields(StateSnapshot s)
    {
        if (!_downloadsEdited)
            _downloadsInput = s.Settings.Downloads;
    }
}
