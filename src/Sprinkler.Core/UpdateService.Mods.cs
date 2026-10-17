namespace Sprinkler.Core;

// Things a person can do to one installed mod beyond updating it: remove it (with Undo), keep a note about it, and
// endorse it on Nexus.
internal sealed partial class UpdateService
{
    private readonly HashSet<long> _endorsed = new();
    private DateTime _endorsedLoadedAt = DateTime.MinValue;

    // ---------------------------------------------------------------- remove

    /// <summary>
    /// Removes a mod. The folder is backed up first and the removal shows up in History with an Undo button.
    /// Like switching a mod off, it first lists the mods that need it and waits for a second go-ahead.
    /// </summary>
    public async Task<(string? Error, string[]? Dependents)> RemoveModAsync(string uniqueId, bool force)
    {
        if (GameLocator.IsGameRunning(fresh: true))
            return ("Close Stardew Valley first. It keeps its mod files open while it runs.", null);

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
            return ("That mod was not found, or it is part of SMAPI.", null);

        var dependents = all
            .Where(m => !m.Disabled && !m.UniqueId.Equals(mod.UniqueId, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(m.ContentPackFor, mod.UniqueId, StringComparison.OrdinalIgnoreCase)
                    || m.Dependencies.Any(d => d.Required && d.UniqueId.Equals(mod.UniqueId, StringComparison.OrdinalIgnoreCase))))
            .Select(m => m.Name).Distinct().ToArray();
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
            return (false, "Connect your Nexus account first (Nexus and settings).");
        try
        {
            var outcome = await _nexus.SetEndorsementAsync(nexusModId, endorse, version, CancellationToken.None);
            switch (outcome)
            {
                case EndorseOutcome.Endorsed:
                    Update(() => _endorsed.Add(nexusModId));
                    return (true, "Endorsed on Nexus. Thank you, the author sees it.");
                case EndorseOutcome.Abstained:
                    Update(() => _endorsed.Remove(nexusModId));
                    return (true, "Endorsement removed.");
                case EndorseOutcome.IsOwnMod:
                    return (false, "That is your own mod, and Nexus does not allow endorsing it.");
                case EndorseOutcome.TooSoonAfterDownload:
                    return (false, "Nexus asks you to use a mod for a while after downloading it before you endorse it. Try again later.");
                case EndorseOutcome.NotDownloaded:
                    return (false, "Nexus only lets you endorse mods you downloaded from its site with this account.");
                default:
                    return (false, "Nexus did not accept the endorsement.");
            }
        }
        catch (NexusException ex)
        {
            return (false, ex.Message);
        }
    }
}
