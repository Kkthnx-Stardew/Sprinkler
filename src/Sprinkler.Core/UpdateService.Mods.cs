namespace Sprinkler.Core;

// Things a person can do to one installed mod beyond updating it: remove it (with Undo), keep a note about it, and
// endorse it on Nexus.
internal sealed partial class UpdateService
{
    private readonly HashSet<long> _endorsed = new();
    private DateTime _endorsedLoadedAt = DateTime.MinValue;

    /// <summary>The names of the switched-on mods that need <paramref name="mod"/>, so switching it off or removing it can warn first.</summary>
    internal static string[] DependentsOf(ModInfo mod, IEnumerable<ModInfo> all) =>
        all.Where(m => !m.Disabled && !m.UniqueId.Equals(mod.UniqueId, StringComparison.OrdinalIgnoreCase)
                && m.RequiredIds.Any(id => id.Equals(mod.UniqueId, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Name).Distinct().ToArray();

    // ---------------------------------------------------------------- switch many on or off

    /// <summary>The mods among <paramref name="folders"/> that a bulk switch would change, with the folder names of the others skipped.</summary>
    private static List<ModInfo> BulkTargets(IEnumerable<ModInfo> all, IReadOnlyCollection<string> folders, bool enable)
    {
        var wanted = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
        return all.Where(m => !m.Bundled && m.Disabled == enable && wanted.Contains(m.FolderName)).ToList();
    }

    /// <summary>
    /// What switching these folders on or off would do: how many mods change, and (when switching off) the names of
    /// switched-on mods outside the group that need one of them and would stop loading.
    /// </summary>
    public (int Count, string[] LeftBehind) PlanBulkSwitch(IReadOnlyCollection<string> folders, bool enable)
    {
        List<ModInfo> all;
        lock (_gate)
            all = _mods.ToList();
        var targets = BulkTargets(all, folders, enable);
        if (enable || targets.Count == 0)
            return (targets.Count, Array.Empty<string>());

        var ids = new HashSet<string>(targets.Select(m => m.UniqueId), StringComparer.OrdinalIgnoreCase);
        var leaving = new HashSet<ModInfo>(targets);
        var left = all.Where(m => !m.Disabled && !leaving.Contains(m) && m.RequiredIds.Any(ids.Contains))
            .Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (targets.Count, left);
    }

    /// <summary>Switches every one of these mod folders on or off the way <see cref="SetModEnabledAsync"/> does, and reports how many worked.</summary>
    public async Task<(int Changed, int Failed, string? Error)> SetModsEnabledAsync(IReadOnlyCollection<string> folders, bool enable)
    {
        if (GameLocator.IsGameRunning(fresh: true))
            return (0, 0, Loc.T("core.close_game_first"));

        List<ModInfo> targets;
        lock (_gate)
            targets = BulkTargets(_mods, folders, enable);

        int changed = 0, failed = 0;
        foreach (var mod in targets)
        {
            if (RenameMod(mod, enable) is null)
                changed++;
            else
                failed++;
        }
        Log.Write($"{(enable ? "Switched on" : "Switched off")} {changed} mods in one go, {failed} failed.");

        if (changed > 0)
        {
            await RescanQuietlyAsync();
            _ = CheckAsync();
        }
        return (changed, failed, null);
    }

    // ---------------------------------------------------------------- remove

    /// <summary>
    /// Removes a mod. The folder is backed up first and the removal shows up in History with an Undo button.
    /// Like switching a mod off, it first lists the mods that need it and waits for a second go-ahead.
    /// </summary>
    public async Task<(string? Error, string[]? Dependents)> RemoveModAsync(string uniqueId, bool force)
    {
        if (GameLocator.IsGameRunning(fresh: true))
            return (Loc.T("core.close_game_first"), null);

        GameInstall? game;
        ModInfo? mod;
        List<ModInfo> all;
        lock (_gate)
        {
            game = _game;
            all = _mods.ToList();
            mod = all.FirstOrDefault(m => m.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
        }
        if (game is null || mod is null || mod.Bundled)
            return (Loc.T("core.mod_not_found"), null);

        var dependents = DependentsOf(mod, all);
        if (dependents.Length > 0 && !force)
            return (null, dependents);

        await _installGate.WaitAsync();
        try
        {
            await Task.Run(() => _installer.Remove(mod.FolderPath, game.ModsPath, mod.UniqueId, mod.Name, mod.Version));
        }
        catch (InstallException ex)
        {
            return (ex.Message, null);
        }
        finally
        {
            _installGate.Release();
        }

        Log.Write($"Removed {mod.Name} {mod.Version}");
        await RescanQuietlyAsync();
        _ = CheckAsync();
        return (null, null);
    }

    /// <summary>Remembers that the person folded or unfolded the content packs under one mod.</summary>
    public void SetPackGroupFolded(string uniqueId, bool folded)
    {
        lock (_gate)
            Settings.Data.PackGroupFolds[uniqueId] = folded;
        Settings.Save();
    }

    /// <summary>Whether content packs are listed under the mod they belong to in the All mods list.</summary>
    public void SetGroupContentPacks(bool on)
    {
        lock (_gate)
            Settings.Data.GroupContentPacks = on;
        Settings.Save();
        Update(() => { });
    }

    // ---------------------------------------------------------------- notes

    /// <summary>Keeps a short private note about a mod ("needs patch X", "do not update before the festival"). An empty note removes it.</summary>
    public void SetModNote(string uniqueId, string? note)
    {
        var text = (note ?? "").Trim();
        if (text.Length > 300)
            text = text[..300];
        lock (_gate)
        {
            var notes = Settings.Data.ModNotes;
            var existing = notes.Keys.FirstOrDefault(k => k.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                notes.Remove(existing);
            if (text.Length > 0)
                notes[uniqueId] = text;
        }
        Settings.Save();
        Update(() => { });
    }

    // ---------------------------------------------------------------- endorse

    /// <summary>Loads which mods the person has endorsed. Cheap: one request, repeated at most every ten minutes.</summary>
    public async Task RefreshEndorsementsAsync()
    {
        if (_nexusUser is null || DateTime.UtcNow - _endorsedLoadedAt < TimeSpan.FromMinutes(10))
            return;
        _endorsedLoadedAt = DateTime.UtcNow;
        try
        {
            var set = await _nexus.GetEndorsedAsync(CancellationToken.None);
            Update(() =>
            {
                _endorsed.Clear();
                foreach (var id in set)
                    _endorsed.Add(id);
            });
        }
        catch (NexusException ex)
        {
            Log.Write("Could not load endorsements: " + ex.Message);
        }
    }

    /// <summary>Endorses a Nexus mod or takes the endorsement back. Returns a message for the person, and whether it worked.</summary>
    public async Task<(bool Ok, string Message)> SetEndorsementAsync(long nexusModId, bool endorse, string version)
    {
        if (_nexusUser is null)
            return (false, Loc.T("core.endorse_connect"));
        try
        {
            var outcome = await _nexus.SetEndorsementAsync(nexusModId, endorse, version, CancellationToken.None);
            switch (outcome)
            {
                case EndorseOutcome.Endorsed:
                    Update(() => _endorsed.Add(nexusModId));
                    return (true, Loc.T("core.endorsed"));
                case EndorseOutcome.Abstained:
                    Update(() => _endorsed.Remove(nexusModId));
                    return (true, Loc.T("core.endorse_removed"));
                case EndorseOutcome.IsOwnMod:
                    return (false, Loc.T("core.endorse_own"));
                case EndorseOutcome.TooSoonAfterDownload:
                    return (false, Loc.T("core.endorse_soon"));
                case EndorseOutcome.NotDownloaded:
                    return (false, Loc.T("core.endorse_not_downloaded"));
                default:
                    return (false, Loc.T("core.endorse_refused"));
            }
        }
        catch (NexusException ex)
        {
            return (false, ex.Message);
        }
    }
}
