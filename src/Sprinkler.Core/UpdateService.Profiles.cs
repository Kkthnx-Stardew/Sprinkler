using System.Text;

namespace Sprinkler.Core;

internal sealed record ProfileResult(string? Error, string Message, List<string> Missing)
{
    public static ProfileResult Fail(string error) => new(error, "", new());

    public static ProfileResult Ok(string message, List<string>? missing = null) => new(null, message, missing ?? new());
}

// Profiles: named sets of switched-on mods. Switching uses SMAPI's own rule (a folder whose name starts with a dot is
// skipped), so a profile is just a different set of folder names. Nothing is moved out of the Mods folder, nothing
// needs special permissions, and switching back is exact.
internal sealed partial class UpdateService
{
    public ProfileStore Profiles { get; }

    private const int MaxConfigBytes = 256 * 1024;

    /// <summary>The IDs of mods that are switched on right now (SMAPI's own bundled mods are not counted).</summary>
    internal static HashSet<string> EnabledIds(IEnumerable<ModInfo> mods) =>
        mods.Where(m => !m.Disabled && !m.Bundled).Select(m => m.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> InstalledIds(IEnumerable<ModInfo> mods) =>
        mods.Where(m => !m.Bundled).Select(m => m.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsSmapiId(string id) =>
        id.StartsWith("SMAPI.", StringComparison.OrdinalIgnoreCase) || id.Equals("Pathoschild.SMAPI", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the mods switched on now differ from the active profile, so the person can be offered to save.</summary>
    private bool ProfileModifiedLocked()
    {
        var active = Profiles.Active;
        if (active is null)
            return false;
        var installed = InstalledIds(_mods);
        var wanted = active.Enabled.Where(installed.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !wanted.SetEquals(EnabledIds(_mods));
    }

    private ProfilesInfo BuildProfilesInfoLocked()
    {
        var installed = InstalledIds(_mods);
        var summaries = Profiles.All().Select(p => new ProfileSummary(
            p.Name, p.Enabled.Count, p.Enabled.Count(id => !installed.Contains(id) && !IsSmapiId(id)), p.Updated, p.Configs.Count,
            p.Name.Equals(Profiles.ActiveName, StringComparison.OrdinalIgnoreCase))).ToList();
        return new ProfilesInfo(Profiles.ActiveName, ProfileModifiedLocked(), Profiles.KeepConfigs, summaries);
    }

    // ---------------------------------------------------------------- renaming a mod's folder (switching it on or off)

    /// <summary>
    /// Switches one mod folder on or off by adding or removing the leading dot. Returns null when it worked (or there was
    /// nothing to do) and a sentence for the person otherwise.
    /// </summary>
    internal static string? RenameMod(ModInfo mod, bool enable)
    {
        var folder = mod.FolderPath.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(folder)!;
        var name = Path.GetFileName(folder);

        if (!enable)
        {
            if (name.StartsWith('.'))
                return null;
        }
        else if (!name.StartsWith('.'))
        {
            return "This mod is switched off by a parent folder whose name starts with a dot. Rename that folder in your file manager.";
        }

        var target = Path.Combine(parent, enable ? name.TrimStart('.') : "." + name);
        if (target.Length == parent.Length + 1 || Directory.Exists(target) || File.Exists(target))
            return "A folder with the new name already exists, so nothing was changed.";

        try
        {
            Directory.Move(folder, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The folder could not be renamed (" + ex.Message + "). Close anything that is using it and try again.";
        }

        Log.Write($"{(enable ? "Enabled" : "Disabled")} {mod.Name}: {name} -> {Path.GetFileName(target)}");
        return null;
    }

    // ---------------------------------------------------------------- profile operations

    private GameInstall? CurrentGame()
    {
        lock (_gate)
            return _game;
    }

    private List<ModInfo> ScanNow(GameInstall game) => ModScanner.Scan(game.ModsPath);

    /// <summary>Reads each switched-on mod's config.json (small ones only), so a profile can bring it back later.</summary>
    internal static Dictionary<string, string> SnapshotConfigs(IEnumerable<ModInfo> mods)
    {
        var configs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods.Where(m => !m.Disabled && !m.Bundled))
        {
            try
            {
                var file = Path.Combine(m.FolderPath, "config.json");
                var info = new FileInfo(file);
                if (info.Exists && info.Length <= MaxConfigBytes)
                    configs[m.UniqueId] = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not read the config of {m.Name}: {ex.Message}");
            }
        }
        return configs;
    }

    /// <summary>Writes a profile's saved configs into the mods that are switched on. Returns how many files changed.</summary>
    internal static int ApplyConfigs(IEnumerable<ModInfo> mods, Dictionary<string, string> configs)
    {
        var written = 0;
        foreach (var m in mods.Where(m => !m.Disabled && !m.Bundled))
        {
            if (!configs.TryGetValue(m.UniqueId, out var text))
                continue;
            try
            {
                var file = Path.Combine(m.FolderPath, "config.json");
                if (File.Exists(file) && File.ReadAllText(file) == text)
                    continue;
                File.WriteAllText(file, text, new UTF8Encoding(false));
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not restore the config of {m.Name}: {ex.Message}");
            }
        }
        return written;
    }

    /// <summary>Saves what is switched on right now as a new profile and makes it the active one.</summary>
    public ProfileResult CreateProfile(string name)
    {
        try
        {
            var game = CurrentGame() ?? throw new ProfileException("Choose your game folder first.");
            var mods = ScanNow(game);
            var profile = new Profile { Name = name, Enabled = EnabledIds(mods).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() };
            if (Profiles.KeepConfigs)
                profile.Configs = SnapshotConfigs(mods);
            Profiles.Add(profile, makeActive: true);
            Update(() => { });
            return ProfileResult.Ok($"Saved \"{profile.Name}\" with {Fmt(profile.Enabled.Count)}.");
        }
        catch (ProfileException ex)
        {
            return ProfileResult.Fail(ex.Message);
        }
    }

    private static string Fmt(int n) => n == 1 ? "1 mod" : n + " mods";

    /// <summary>Updates the active profile to match what is switched on now.</summary>
    public ProfileResult SaveActiveProfile()
    {
        try
        {
            var active = Profiles.Active ?? throw new ProfileException("No profile is active.");
            var game = CurrentGame() ?? throw new ProfileException("Choose your game folder first.");
            var mods = ScanNow(game);
            var enabled = EnabledIds(mods).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            // mods the profile lists but that are not installed here stay listed, so a shared profile does not lose them
            var installed = InstalledIds(mods);
            enabled.AddRange(active.Enabled.Where(id => !installed.Contains(id) && !IsSmapiId(id)));
            Profiles.Update(active.Name, p =>
            {
                p.Enabled = enabled.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (Profiles.KeepConfigs)
                    p.Configs = SnapshotConfigs(mods);
            });
            Update(() => _mods = mods);
            return ProfileResult.Ok($"Saved the changes to \"{active.Name}\".");
        }
        catch (ProfileException ex)
        {
            return ProfileResult.Fail(ex.Message);
        }
    }

    /// <summary>Switches to a profile: turns its mods on, turns the others off, and (if asked) restores each mod's settings.</summary>
    public ProfileResult ApplyProfile(string name)
    {
        var game = CurrentGame();
        if (game is null)
            return ProfileResult.Fail("Choose your game folder first.");
        if (GameLocator.IsGameRunning(fresh: true))
            return ProfileResult.Fail("Close Stardew Valley first. It keeps its mod files open while it runs.");
        var profile = Profiles.Find(name);
        if (profile is null)
            return ProfileResult.Fail("That profile no longer exists.");

        var mods = ScanNow(game);
        var live = mods.Where(m => !m.Bundled).ToList();

        // the settings of the profile we are leaving are kept, so switching back brings them back
        if (Profiles.KeepConfigs)
        {
            var leaving = Profiles.Active;
            if (leaving is null)
            {
                var safe = Profiles.CleanName("Previous setup" + (Profiles.Find("Previous setup") is null ? "" : " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm")));
                Profiles.Add(new Profile { Name = safe, Enabled = EnabledIds(mods).ToList(), Configs = SnapshotConfigs(mods) }, makeActive: false);
            }
            else if (!leaving.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = SnapshotConfigs(mods);
                Profiles.Update(leaving.Name, p => p.Configs = snapshot);
            }
        }

        // what must be on: the profile's mods that are installed, plus whatever those require
        var byId = live.GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var want = profile.Enabled.Where(byId.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(want);
        var addedForRequirements = new List<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var m in byId[id])
            {
                var needs = m.Dependencies.Where(d => d.Required).Select(d => d.UniqueId).Concat(m.ContentPackFor is null ? Array.Empty<string>() : new[] { m.ContentPackFor });
                foreach (var need in needs)
                    if (byId.ContainsKey(need) && want.Add(need))
                    {
                        queue.Enqueue(need);
                        addedForRequirements.Add(byId[need][0].Name);
                    }
            }
        }
        var missing = profile.Enabled.Where(id => !byId.ContainsKey(id) && !IsSmapiId(id)).ToList();

        var turnedOn = 0;
        var turnedOff = 0;
        var stuck = new List<string>();
        foreach (var m in live)
        {
            var shouldBeOn = want.Contains(m.UniqueId);
            if (shouldBeOn == !m.Disabled)
                continue;
            if (shouldBeOn && !Path.GetFileName(m.FolderPath.TrimEnd('\\', '/')).StartsWith('.'))
            {
                stuck.Add(m.Name); // switched off by a parent folder; a rename of this folder would not help
                continue;
            }
            var error = RenameMod(m, shouldBeOn);
            if (error is not null)
            {
                stuck.Add(m.Name);
                continue;
            }
            if (shouldBeOn) turnedOn++; else turnedOff++;
        }

        Profiles.SetActive(profile.Name);
        var after = ScanNow(game);
        var configsWritten = Profiles.KeepConfigs ? ApplyConfigs(after, profile.Configs) : 0;
        Update(() => _mods = after);
        _ = CheckAsync();

        var sb = new StringBuilder($"Switched to \"{profile.Name}\": {turnedOn} turned on, {turnedOff} turned off");
        if (configsWritten > 0)
            sb.Append($", settings restored for {Fmt(configsWritten)}");
        sb.Append('.');
        if (addedForRequirements.Count > 0)
            sb.Append(" Also turned on what the others need: " + string.Join(", ", addedForRequirements.Distinct().Take(6)) + ".");
        if (stuck.Count > 0)
            sb.Append(" These could not be changed: " + string.Join(", ", stuck.Take(6)) + ".");
        if (missing.Count > 0)
            sb.Append($" {Fmt(missing.Count)} in this profile {(missing.Count == 1 ? "is" : "are")} not installed; they appear under Updates as installs.");
        return ProfileResult.Ok(sb.ToString(), missing);
    }

    public ProfileResult RenameProfile(string name, string newName)
    {
        try
        {
            Profiles.Rename(name, newName);
            Update(() => { });
            return ProfileResult.Ok("Renamed.");
        }
        catch (ProfileException ex)
        {
            return ProfileResult.Fail(ex.Message);
        }
    }

    public ProfileResult CopyProfile(string name, string newName)
    {
        try
        {
            var source = Profiles.Find(name) ?? throw new ProfileException("That profile no longer exists.");
            Profiles.Add(source.Copy(newName), makeActive: false);
            Update(() => { });
            return ProfileResult.Ok($"Copied to \"{newName.Trim()}\".");
        }
        catch (ProfileException ex)
        {
            return ProfileResult.Fail(ex.Message);
        }
    }

    public ProfileResult DeleteProfile(string name)
    {
        try
        {
            Profiles.Delete(name);
            Update(() => { });
            return ProfileResult.Ok("Deleted. Your mods were not changed.");
        }
        catch (ProfileException ex)
        {
            return ProfileResult.Fail(ex.Message);
        }
    }

    public void SetKeepConfigs(bool on)
    {
        Profiles.KeepConfigs = on;
        if (on && Profiles.Active is not null && CurrentGame() is { } game)
        {
            var mods = ScanNow(game);
            Profiles.Update(Profiles.ActiveName!, p => p.Configs = SnapshotConfigs(mods));
        }
        Update(() => { });
    }

    /// <summary>The text of a profile file for sharing: the mods, their names, versions and where to get them. No settings, no paths.</summary>
    public string ExportProfile(string name)
    {
        var profile = Profiles.Find(name) ?? throw new ProfileException("That profile no longer exists.");
        List<ModInfo> mods;
        lock (_gate)
            mods = _mods.ToList();
        var byId = mods.GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var list = profile.Enabled.Where(id => !IsSmapiId(id)).Select(id =>
        {
            byId.TryGetValue(id, out var m);
            var keys = m?.ParsedKeys.ToList() ?? new();
            long? nexus = keys.FirstOrDefault(k => k.Site == SourceKind.Nexus && k.Id.All(char.IsDigit)) is { } n ? long.Parse(n.Id) : null;
            var github = keys.FirstOrDefault(k => k.Site == SourceKind.GitHub && k.Id.Contains('/'))?.Id;
            return new PortableMod(id, m?.Name ?? id, m?.Version, nexus, github);
        }).ToList();
        return new PortableProfile(PortableProfile.FormatName, 1, profile.Name, DateTime.UtcNow, list).ToJson();
    }

    /// <summary>Adds a shared profile. It becomes a normal profile; switching to it brings the mods that are not installed up as installs.</summary>
    public ProfileResult ImportProfile(string json)
    {
        try
        {
            var shared = PortableProfile.Parse(json);
            var name = shared.Name;
            for (var n = 2; Profiles.Find(name) is not null; n++)
                name = $"{shared.Name} ({n})";
            var profile = new Profile { Name = name, Enabled = shared.Mods.Select(m => m.UniqueId).ToList() };
            Profiles.Add(profile, makeActive: false);

            List<ModInfo> mods;
            lock (_gate)
                mods = _mods.ToList();
            var installed = InstalledIds(mods);
            var missing = profile.Enabled.Where(id => !installed.Contains(id) && !IsSmapiId(id)).ToList();
            Update(() => { });
            return ProfileResult.Ok($"Added \"{profile.Name}\" with {Fmt(profile.Enabled.Count)}" + (missing.Count > 0 ? $", {missing.Count} of them not installed here yet." : "."), missing);
        }
        catch (ProfileException ex)
        {
            return ProfileResult.Fail(ex.Message);
        }
    }

    /// <summary>Mods the active profile lists that are not installed, so the check can offer to install them.</summary>
    private Dictionary<string, List<ModInfo>> ProfileWantedMods(HashSet<string> installedIds)
    {
        var wanted = new Dictionary<string, List<ModInfo>>(StringComparer.OrdinalIgnoreCase);
        var active = Profiles.Active;
        if (active is null)
            return wanted;
        var stand = new ModInfo { UniqueId = "profile", Name = $"your {active.Name} profile" };
        foreach (var id in active.Enabled.Where(id => !installedIds.Contains(id) && !IsSmapiId(id)))
            wanted[id] = new List<ModInfo> { stand };
        return wanted;
    }
}
