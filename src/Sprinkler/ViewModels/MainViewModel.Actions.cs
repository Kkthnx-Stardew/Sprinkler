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
            Toast(Loc.T("act.starting_game"));
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
        Toast(Loc.T("act.theme_" + (Snap?.Settings.Theme ?? "auto")));
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
    private async Task OpenAppUpdate()
    {
        var update = Snap?.App.Update;
        if (update is null || update.Busy)
            return;
        if (!update.CanInstall)
        {
            OpenUrl(update.Url);
            return;
        }

        var dialog = new ConfirmDialogViewModel(Loc.T("upd.confirm_title", update.Version), "gift", Loc.T("upd.yes"), Loc.T("ui.cancel"),
            new[] { new DialogLine("", Loc.T("upd.confirm_text")) },
            new[] { Loc.T("upd.b1"), Loc.T("upd.b2"), Loc.T("upd.b3") });
        ShowDialog(dialog);
        if (!await dialog.Result)
            return;

        var error = await _svc.InstallAppUpdateAsync();
        if (error is not null)
        {
            Toast(error, true);
            return;
        }
        if (!_svc.RestartIntoUpdate())
        {
            Toast(Loc.T("upd.restart_failed"), true);
            return;
        }
        Ui?.Exit();
    }

    /// <summary>Whether the program looks for a newer version of itself when it starts.</summary>
    public bool CheckAppUpdates
    {
        get => Snap?.Settings.CheckAppUpdates ?? true;
        set
        {
            if (value == CheckAppUpdates)
                return;
            _svc.SetCheckAppUpdates(value);
            Refresh();
        }
    }

    [RelayCommand]
    private void OpenRepo() => OpenUrl(RepoUrl);

    [RelayCommand]
    private void OpenKofi() => OpenUrl(AppEnvironment.KofiUrl);

    [RelayCommand]
    private void OpenBuyMeACoffee() => OpenUrl(AppEnvironment.BuyMeACoffeeUrl);

    [RelayCommand]
    private void OpenPayPal() => OpenUrl(AppEnvironment.PayPalUrl);

    [RelayCommand]
    private void OpenNexusKeyPage() => OpenUrl("https://www.nexusmods.com/settings/api-keys");

    public void OpenUrl(string? url)
    {
        if (!UrlPolicy.Open(url))
            Toast(Loc.T("act.bad_address"), true);
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
        Toast(Loc.T(copied ? "act.report_copied" : "act.report_opening"));
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
            Toast(Loc.T("act.no_folder"), true);
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
        var picked = await Ui.PickFolderAsync(Loc.T("act.pick_game"));
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
                Loc.T("act.update_auto", Loc.Count("n.mod", auto.Count)),
                string.Join(", ", auto.Select(i => Loc.T("act.update_from", i.Name, OfferOf(i).Label)))));
        if (hand.Count > 0)
            lines.Add(new DialogLine(
                Loc.T("act.update_hand", Loc.Count("n.mod", hand.Count)),
                string.Join(", ", hand.Select(i => $"{i.Name} ({OfferOf(i).Label})"))));

        var dialog = new ConfirmDialogViewModel(
            Loc.T("act.update_title", Loc.Count("n.mod", chosen.Count)), "arrowup", Loc.T("act.yes_update"), Loc.T("act.not_yet"), lines,
            new[]
            {
                Loc.T("act.update_b1"),
                Loc.T("act.update_b2"),
                Loc.T("act.update_b3"),
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
        if (!await AskAsync(Loc.T("act.undo_title"), Loc.T("act.undo_text"), Loc.T("act.undo_yes"), "shield"))
            return;
        var error = await _svc.UndoAsync(historyId);
        if (error is null)
            Toast(Loc.T("act.undone"));
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
                Toast(Loc.T("act.not_archive", name), true);
                continue;
            }
            Toast(Loc.T("act.reading", name));
            try
            {
                // work on a copy, so the file the person dropped is never moved or deleted
                Directory.CreateDirectory(AppEnvironment.StagingDir);
                var staged = Path.Combine(AppEnvironment.StagingDir, Guid.NewGuid().ToString("N")[..8] + "-" + name);
                File.Copy(path, staged, overwrite: true);
                await _svc.AddUploadedFileAsync(staged, name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (ex is not (IOException or UnauthorizedAccessException))
                    Log.Write($"Reading the dropped file {name} failed: {ex}");
                Toast(Loc.T("act.read_failed", name, ex.Message), true);
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

    /// <summary>The rows the current chip and search box pick, before sorting and grouping. A bulk switch acts on exactly these.</summary>
    private IEnumerable<ModRowViewModel> MatchingRows(IEnumerable<ModRowViewModel> rows)
    {
        var term = ModSearch.Trim();
        return rows
            .Where(v => FilterMatches(_modFilter, v.Row))
            .Where(v => term.Length == 0 || (v.Row.Name + " " + v.Row.Author + " " + v.Row.UniqueId).Contains(term, StringComparison.OrdinalIgnoreCase));
    }

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
        var shown = MatchingRows(rows);

        IOrderedEnumerable<ModRowViewModel> ordered = ModSortIndex switch
        {
            2 => shown.OrderBy(v => v.Row.Author ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            1 => shown.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => shown.OrderBy(v => StateRank.GetValueOrDefault(v.Row.State, 9)).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var list = ordered.ToList();
        if (GroupPacks)
            // while searching, every match stays visible even inside a folded group
            list = GroupUnderParents(list, term.Length > 0 ? null : IsGroupFolded);
        else
            foreach (var v in list)
            {
                v.Indented = false;
                v.PackCount = 0;
                v.Collapsed = false;
            }
        SyncList(Mods, list);
        OnPropertyChanged(nameof(NoModMatches));
    }

    /// <summary>
    /// Puts each content pack right under the mod it belongs to, indented, when that mod is also in the list.
    /// A pack whose mod is missing, filtered out or itself a pack stays where the sort put it.
    /// </summary>
    /// <summary>A mod with more content packs than this starts folded, so one big mod does not push everything else off the screen.</summary>
    internal const int FoldAbove = 10;

    internal static bool FoldsByDefault(int packCount) => packCount > FoldAbove;

    /// <summary>What the person chose for this mod before, or the default for a mod with this many packs.</summary>
    private bool IsGroupFolded(string uniqueId, int packCount) =>
        _svc.Settings.Data.PackGroupFolds.TryGetValue(uniqueId, out var folded) ? folded : FoldsByDefault(packCount);

    /// <summary>Folds or unfolds the content packs under one mod, and remembers the choice.</summary>
    public void ToggleGroup(string uniqueId)
    {
        var row = _rowMap.Values.FirstOrDefault(v => v.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
        _svc.SetPackGroupFolded(uniqueId, !(row?.Collapsed ?? false));
        RebuildMods();
    }

    internal static List<ModRowViewModel> GroupUnderParents(List<ModRowViewModel> sorted, Func<string, int, bool>? isFolded = null)
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
            var packs = packsByParent[v.UniqueId].ToList();
            v.PackCount = packs.Count;
            v.Collapsed = packs.Count > 0 && isFolded?.Invoke(v.UniqueId, packs.Count) == true;
            result.Add(v);
            if (v.Collapsed)
                continue;
            foreach (var pack in packs)
            {
                pack.Indented = true;
                pack.PackCount = 0;
                pack.Collapsed = false;
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
        var text = await PromptAsync(Loc.T("act.note_title", row.Name), "book", Loc.T("act.note_prompt"), NoteFor(row.UniqueId), Loc.T("act.note_save"), 300);
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
            Toast(Loc.T("act.no_settings_file"), true);
            return;
        }
        try
        {
            Platform.OpenFile(file);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Toast(Loc.T("act.settings_file_failed", ex.Message), true);
        }
    }

    public async Task RemoveModAsync(ModRowViewModel row, bool force = false)
    {
        if (!force && !await AskAsync(Loc.T("act.remove_title", row.Name),
            Loc.T("act.remove_text"),
            Loc.T("act.remove_yes"), "alert"))
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
            if (await AskAsync(Loc.T("act.remove_anyway_title", row.Name), Loc.T("act.needed_by", string.Join(", ", dependents)), Loc.T("act.remove_anyway"), "alert"))
                await RemoveModAsync(row, force: true);
            return;
        }
        Toast(Loc.T("act.removed", row.Name));
    }

    public async Task ToggleEndorsementAsync(ModRowViewModel row)
    {
        if (row.NexusId is not { } id)
            return;
        var (ok, message) = await _svc.SetEndorsementAsync(id, !IsEndorsed(id), row.Version);
        Refresh();
        Toast(message, !ok);
    }

    private static string ChipLabel(string key) => Loc.T("chip." + key);

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
            var ok = await AskAsync(Loc.T("act.off_title", row.Name), Loc.T("act.needed_by", string.Join(", ", dependents)), Loc.T("act.off_anyway"), "alert");
            if (ok)
                await ToggleModAsync(row, enable, force: true);
            return;
        }
        Toast(Loc.T(enable ? "act.is_on" : "act.is_off", row.Name));
    }

    [RelayCommand]
    private Task SwitchShownOn() => SwitchShownAsync(true);

    [RelayCommand]
    private Task SwitchShownOff() => SwitchShownAsync(false);

    /// <summary>Switches every mod the list currently matches on or off, after one confirmation that says how many and what would break.</summary>
    private async Task SwitchShownAsync(bool enable)
    {
        var folders = MatchingRows(_rowMap.Values).Select(v => v.Row.Folder).ToList();
        var (count, leftBehind) = _svc.PlanBulkSwitch(folders, enable);
        if (count == 0)
        {
            Toast(Loc.T("act.bulk_none"));
            return;
        }

        var many = Loc.Count("n.mod", count);
        var text = Loc.T(enable ? "act.bulk_on_text" : "act.bulk_off_text");
        if (leftBehind.Length > 0)
            text += " " + Loc.T("act.bulk_needed_by", string.Join(", ", leftBehind.Take(4)) + (leftBehind.Length > 4 ? ", …" : ""));
        if (!await AskAsync(Loc.T(enable ? "act.bulk_on_title" : "act.bulk_off_title", many), text, Loc.T(enable ? "act.bulk_on_button" : "act.bulk_off_button"), leftBehind.Length > 0 ? "alert" : "layers"))
            return;

        var (changed, failed, error) = await _svc.SetModsEnabledAsync(folders, enable);
        Refresh();
        if (error is not null)
        {
            Toast(error, true);
            return;
        }
        Toast(Loc.T(enable ? "act.bulk_done_on" : "act.bulk_done_off", Loc.Count("n.mod", changed)) + (failed > 0 ? " " + Loc.T("act.bulk_failed", Loc.Count("n.mod", failed)) : ""), failed > 0);
    }

    public void SetIgnored(string uniqueId, bool ignore) => _svc.SetIgnored(uniqueId, ignore);

    // ---- keep a few values fresh without fighting the person's typing
    private void SyncSettingsFields(StateSnapshot s)
    {
        if (!_downloadsEdited)
            _downloadsInput = s.Settings.Downloads;
    }
}
