namespace Sprinkler.Core;

/// <summary>What changed in the Mods folder behind the app's back: mods that appeared, mods that went, and mods switched or updated by hand.</summary>
internal sealed record ModsDelta(int Added, int Removed, int Changed, List<string> Names);

// Keeping the screen true to the Mods folder. The app's own installs, removals and switches update the list as they
// happen. This file covers everything else: a folder deleted in Explorer, a mod copied in by hand, another tool
// switching a mod off. A watcher notices, the folder is read again, and only a real difference changes anything.
internal sealed partial class UpdateService
{
    private FileSystemWatcher? _modsWatcher;
    private Timer? _modsDebounce;
    private long _lastActivationScanTicks;
    private int _outsideScanRunning;

    /// <summary>Raised when the Mods folder was found different from what the app showed. The list is already up to date when it fires.</summary>
    public event Action<ModsDelta>? ModsChangedOutside;

    // ---------------------------------------------------------------- the list follows the folder

    /// <summary>
    /// Brings the rows and update cards in line with a fresh read of the folder at once, before the slower online check
    /// has run again: a removed mod disappears, a switched mod shows its new state, and mods that needed it now say so.
    /// Must be called inside <see cref="Update"/>.
    /// </summary>
    private void ReconcileWithMods(List<ModInfo> mods)
    {
        var oldByKey = new Dictionary<string, ModRow>();
        var oldById = new Dictionary<string, ModRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows)
        {
            oldByKey.TryAdd(RowKey(r.Folder, r.UniqueId), r);
            oldById.TryAdd(r.UniqueId, r);
        }

        var onIds = mods.Where(m => !m.Disabled).Select(m => m.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignored = new HashSet<string>(Settings.Data.Ignored, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ModRow>(mods.Count);
        foreach (var m in mods)
        {
            oldByKey.TryGetValue(RowKey(m.FolderName, m.UniqueId), out var same);
            oldById.TryGetValue(m.UniqueId, out var before);
            var unchanged = same is not null && same.Version == m.Version;

            ModRow row;
            if (m.Bundled)
                row = unchanged ? same! : new ModRow(m.Name, m.UniqueId, m.Version, RowState.Bundled, Loc.T("core.row_bundled"), m.FolderName);
            else if (m.Disabled)
                row = unchanged && same!.State == RowState.Disabled ? same : new ModRow(m.Name, m.UniqueId, m.Version, RowState.Disabled, Loc.T("core.row_off"), m.FolderName);
            else if (unchanged && same!.State != RowState.Disabled)
                row = same;
            else if (ignored.Contains(m.UniqueId))
                row = new ModRow(m.Name, m.UniqueId, m.Version, RowState.Ignored, Loc.T("core.row_ignored"), m.FolderName);
            else
                row = new ModRow(m.Name, m.UniqueId, m.Version, RowState.NoInfo, Loc.T("core.row_checking"), m.FolderName);

            string[]? needs = null;
            if (!m.Disabled && !m.Bundled)
            {
                var lacking = m.RequiredIds
                    .Where(d => !onIds.Contains(d) && !IsSmapiId(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(d => oldById.TryGetValue(d, out var known) ? known.Name : d)
                    .ToArray();
                if (lacking.Length > 0)
                    needs = lacking;
            }

            rows.Add(row with
            {
                Url = row.Url ?? before?.Url,
                Author = m.Author,
                IsContentPack = m.ContentPackFor is not null,
                ParentId = m.ContentPackFor,
                HasConfig = unchanged ? row.HasConfig : File.Exists(Path.Combine(m.FolderPath, "config.json")),
                Needs = needs,
            });
        }
        _rows = rows.OrderBy(r => r.State == RowState.Bundled).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // an update card for a mod that is gone or switched off has nothing left to update; cards being worked on stay
        var switchedOn = mods.Where(m => !m.Disabled && !m.Bundled).Select(m => m.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _items = _items.Where(i => i.IsNew
            || i.Status is ItemStatus.Working or ItemStatus.WaitingForYou or ItemStatus.Queued or ItemStatus.Done
            || i.UniqueIds.Any(switchedOn.Contains)).ToList();
    }

    private static string RowKey(string folder, string uniqueId) => folder + "|" + uniqueId;

    // ---------------------------------------------------------------- noticing changes made elsewhere

    private static string Signature(ModInfo m) => $"{m.FolderPath}|{m.UniqueId}|{m.Version}|{m.Disabled}|{string.Join(',', m.RequiredIds)}";

    /// <summary>
    /// Reads the Mods folder again and, if it differs from what the app shows, updates the list and starts a new check.
    /// Returns what changed, or null when nothing did. Safe to call often: an unchanged folder costs one quick read.
    /// </summary>
    public async Task<ModsDelta?> CheckForOutsideChangesAsync()
    {
        var game = CurrentGame();
        if (game is null || _disposed)
            return null;
        if (Interlocked.Exchange(ref _outsideScanRunning, 1) == 1)
            return null;
        try
        {
            // the app's own installs and removals update the list themselves, so wait for them rather than race them
            Phase phase;
            lock (_gate)
                phase = _phase;
            if (_installGate.CurrentCount == 0 || phase == Phase.Applying)
            {
                ScheduleOutsideScan();
                return null;
            }

            var mods = await Task.Run(() => ModScanner.Scan(game.ModsPath));
            ModsDelta? delta = null;
            lock (_gate)
            {
                // per mod ID, everything that makes the folder look different (path, version, on or off, requirements)
                static Dictionary<string, string> ById(IEnumerable<ModInfo> list) => list
                    .GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => string.Join(';', g.Select(Signature).Order(StringComparer.Ordinal)), StringComparer.OrdinalIgnoreCase);

                var before = ById(_mods);
                var now = ById(mods);
                var added = now.Keys.Where(id => !before.ContainsKey(id)).ToList();
                var removed = before.Keys.Where(id => !now.ContainsKey(id)).ToList();
                var changed = now.Where(kv => before.TryGetValue(kv.Key, out var was) && was != kv.Value).Select(kv => kv.Key).ToList();
                if (added.Count + removed.Count + changed.Count == 0)
                    return null;

                string NameOf(string id) => (mods.Concat(_mods)).First(m => m.UniqueId.Equals(id, StringComparison.OrdinalIgnoreCase)).Name;
                delta = new ModsDelta(added.Count, removed.Count, changed.Count,
                    removed.Concat(added).Concat(changed).Select(NameOf).Distinct().Take(6).ToList());
            }

            Log.Write($"The Mods folder changed outside the app: {delta.Added} added, {delta.Removed} removed, {delta.Changed} changed" +
                      (delta.Names.Count > 0 ? " (" + string.Join(", ", delta.Names) + ")" : "") + ".");
            SyncCollectionProfiles(mods);
            Update(() =>
            {
                _mods = mods;
                ReconcileWithMods(mods);
            });
            RequestRecheck();
            ModsChangedOutside?.Invoke(delta);
            return delta;
        }
        finally
        {
            Interlocked.Exchange(ref _outsideScanRunning, 0);
        }
    }

    /// <summary>Called when the window comes to the front, which is when a change made in Explorer is most likely. Reads at most once every few seconds.</summary>
    public void NoticeWindowActivated()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastActivationScanTicks);
        if (now - last < 3000 || Interlocked.CompareExchange(ref _lastActivationScanTicks, now, last) != last)
            return;
        _ = CheckForOutsideChangesAsync();
    }

    private void StartModsWatcher(string modsPath)
    {
        StopModsWatcher();
        try
        {
            var watcher = new FileSystemWatcher(modsPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, e) => OnModsFolderEvent(modsPath, e.FullPath);
            watcher.Deleted += (_, e) => OnModsFolderEvent(modsPath, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                OnModsFolderEvent(modsPath, e.OldFullPath);
                OnModsFolderEvent(modsPath, e.FullPath);
            };
            watcher.Error += (_, e) =>
            {
                Log.Write("The Mods folder watcher lost events, reading the folder again: " + e.GetException().Message);
                ScheduleOutsideScan();
            };
            watcher.EnableRaisingEvents = true;
            _modsWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // not fatal: the window coming to the front still triggers a read
            Log.Write("The Mods folder could not be watched: " + ex.Message);
        }
    }

    private void StopModsWatcher()
    {
        _modsWatcher?.Dispose();
        _modsWatcher = null;
    }

    /// <summary>True for a change that can add, remove, switch or update a mod: a top-level entry, or a manifest anywhere below.</summary>
    internal static bool IsModStructureChange(string modsPath, string changedPath)
    {
        var relative = Path.GetRelativePath(modsPath, changedPath);
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return false;
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 || (parts.Length > 1 && parts[^1].Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
    }

    private void OnModsFolderEvent(string modsPath, string changedPath)
    {
        if (!_disposed && IsModStructureChange(modsPath, changedPath))
            ScheduleOutsideScan();
    }

    /// <summary>Waits for a burst of events to settle (a folder delete is hundreds of them) and then reads once.</summary>
    private void ScheduleOutsideScan()
    {
        if (_disposed)
            return;
        _modsDebounce ??= new Timer(_ => _ = CheckForOutsideChangesAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _modsDebounce.Change(TimeSpan.FromMilliseconds(1500), Timeout.InfiniteTimeSpan);
    }
}
