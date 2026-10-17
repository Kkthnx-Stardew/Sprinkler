namespace Sprinkler.Core;

internal enum Phase
{
    Starting,
    ChooseGame,
    Idle,
    Scanning,
    Checking,
    Review,
    Applying,
    Finished,
}

internal enum ItemStatus
{
    Ready,
    Queued,
    Working,
    WaitingForYou,
    Done,
    Failed,
    Skipped,
}

internal enum RowState
{
    UpToDate,
    Update,
    NoInfo,
    Error,
    Disabled,
    Bundled,
    Ignored,
    Warning,
}

internal sealed class UpdateItem
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string? Installed { get; set; }
    public string Target { get; set; } = "";
    public List<string> UniqueIds { get; } = new();
    public int PartCount { get; set; } = 1;
    public bool IsNew { get; init; }

    /// <summary>For installs whose SMAPI mod ID is not known until the file is opened, such as the mods of a Nexus collection.</summary>
    public bool AcceptAnyMod { get; init; }
    public string? Why { get; set; }
    public List<SourceOffer> Offers { get; } = new();
    public int Chosen { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Ready;
    public string Message { get; set; } = "";
    public int Progress { get; set; }
    public bool Selected { get; set; } = true;
    public string? CompatNote { get; set; }
    public List<string> Changelog { get; } = new();
    public string? ModPageUrl { get; set; }
    public List<string> Details { get; } = new();
    public string? HistoryId { get; set; }

    public long? NexusId { get; set; }
    public long? CurseForgeId { get; set; }
    public string? GitHubRepo { get; set; }
    public long? ModDropId { get; set; }
    public string? NexusSubkey { get; set; }
    public bool AlternativesLoaded { get; set; }
    public bool PageOpened { get; set; }

    internal TaskCompletionSource<WaitResult>? Waiter { get; set; }
}

internal sealed record WaitResult(bool Installed, int? SwitchTo);

internal sealed record ModRow(string Name, string UniqueId, string Version, RowState State, string Note, string Folder, string? Url = null, string? Author = null, bool IsContentPack = false, string[]? Needs = null, string? Unofficial = null, string? UnofficialUrl = null, string? SkippedVersion = null, string? ParentId = null, bool HasConfig = false, string? Target = null);

internal sealed record Problem(string Level, string Title, string Detail, string? Url = null, string? UrlLabel = null);

internal sealed class FoundArchive
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Origin { get; init; } = "";
    public List<string> Lines { get; } = new();
    public bool Busy { get; set; }
    public string? Error { get; set; }
}

/// <summary>The brain of the app: scans, checks, plans and applies updates, and exposes a thread-safe state snapshot for the UI.</summary>
internal sealed partial class UpdateService : IDisposable
{
    public const int MaxOpenPages = 3;

    private readonly object _gate = new();
    private readonly SmapiClient _smapi = new();
    private readonly CurseForgeClient _curse = new();
    private readonly GitHubClient _github = new();
    private readonly NexusClient _nexus;
    private readonly Installer _installer;
    private readonly DownloadWatcher _watcher;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(2, 2);
    private readonly SemaphoreSlim _opGate = new(1, 1);
    private readonly Action<string> _openUrl;
    private CancellationTokenSource _cts = new();

    // The download of a new version of this program is not part of a check of the mods, so a new check must not cancel it.
    private readonly CancellationTokenSource _appCts = new();

    private long _version;
    private Phase _phase = Phase.Starting;
    private string _status = "";
    private GameInstall? _game;
    private List<GameInstall> _candidates = new();
    private List<ModInfo> _mods = new();
    private List<ModRow> _rows = new();
    private List<UpdateItem> _items = new();
    private List<Problem> _problems = new();
    private List<FoundArchive> _found = new();
    private (string Version, string Url)? _smapiUpdate;
    private NexusUser? _nexusUser;
    private string? _nexusMessage;
    private DateTime? _lastChecked;
    private string? _fatal;
    private string? _lastSummary;
    private volatile bool _disposed;
    private GameLog? _gameLog;

    public SettingsStore Settings { get; }
    public HistoryStore History { get; }

    /// <summary>Mod pictures and short summaries, fetched in the background and kept on disk.</summary>
    public ModArtStore Art { get; }

    /// <summary>The picture lookup key of an installed mod, or null when its manifest gives none.</summary>
    public string? ArtKeyOf(string uniqueId)
    {
        lock (_gate)
            return _mods.FirstOrDefault(m => m.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase)) is { } mod ? ModArtStore.KeyFor(mod) : null;
    }

    /// <summary>Asks for the pictures of every installed mod. Cheap when they are already cached.</summary>
    public void RequestAllArt()
    {
        List<ModInfo> mods;
        lock (_gate)
            mods = _mods.ToList();
        foreach (var key in mods.Select(ModArtStore.KeyFor).Where(k => k is not null).Distinct())
            Art.Request(key);
        _ = RefreshEndorsementsAsync();
    }

    public UpdateService(SettingsStore settings, HistoryStore history, Action<string> openUrl, ProfileStore? profiles = null)
    {
        Settings = settings;
        Profiles = profiles ?? new ProfileStore();
        History = history;
        _openUrl = openUrl;
        _installer = new Installer(history, settings);
        _nexus = new NexusClient(() => settings.NexusKey);
        Art = new ModArtStore(_nexus);
        Art.Changed += () => Changed?.Invoke();
        _watcher = new DownloadWatcher(path => HandleArchiveAsync(path, "your browser download", CancellationToken.None));
    }

    public long Version => Interlocked.Read(ref _version);

    // ---------------------------------------------------------------- startup

    public async Task InitializeAsync()
    {
        try
        {
            await InitializeCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Startup failed: " + ex);
            Update(() =>
            {
                _phase = Phase.ChooseGame;
                _fatal = Loc.T("core.startup_failed", ex.Message);
            });
        }
    }

    private async Task InitializeCoreAsync()
    {
        _ = CheckAppUpdateAsync();
        var key = Settings.NexusKey;
        var nexusCheck = string.IsNullOrEmpty(key) ? Task.CompletedTask : ValidateNexusAsync(key);
        _candidates = await Task.Run(GameLocator.FindAll);
        await nexusCheck;

        GameInstall? chosen = null;
        var savedMods = Settings.Data.ModsPath;
        if (!string.IsNullOrWhiteSpace(Settings.Data.GamePath))
            chosen = GameLocator.Inspect(Settings.Data.GamePath!);
        if (chosen is null && _candidates.Count == 1)
            chosen = _candidates[0];

        if (chosen is not null)
        {
            if (!string.IsNullOrWhiteSpace(savedMods) && Directory.Exists(savedMods))
                chosen = chosen with { ModsPath = savedMods };
            await UseGameAsync(chosen, remember: Settings.Data.GamePath is not null);
        }
        else
        {
            Update(() => _phase = Phase.ChooseGame);
        }
    }

    public async Task<string?> SetGameFolderAsync(string path)
    {
        var folder = path.Trim().Trim('"');
        var inspect = GameLocator.Inspect(folder);
        if (inspect is null)
        {
            if (Directory.Exists(Path.Combine(folder, "Mods")) && GameLocator.Inspect(Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? "") is { } parent)
                inspect = parent;
            else if (GameLocator.IsLegacyInstall(folder))
                return Loc.T("core.old_game");
            else
                return Loc.T("core.not_game");
        }
        await UseGameAsync(inspect, remember: true);
        return null;
    }

    private async Task UseGameAsync(GameInstall game, bool remember)
    {
        lock (_gate)
        {
            _game = game;
            Settings.Data.GamePath = game.GamePath;
            Settings.Data.ModsPath = game.ModsPath;
            if (remember)
                Settings.Save();
        }
        Directory.CreateDirectory(game.ModsPath);
        Update(() => _phase = Phase.Idle);
        StartWatcher();
        await CheckAsync();
    }

    public void ConfirmWelcome()
    {
        Settings.Data.Welcomed = true;
        Settings.Save();
        Update(() => { });
    }

    private void StartWatcher()
    {
        try
        {
            _watcher.Start(Settings.DownloadsPath);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Write("Watcher could not start: " + ex.Message);
        }
    }

    public void SetDownloadsFolder(string path)
    {
        Settings.Data.DownloadsPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
        Settings.Save();
        StartWatcher();
        Update(() => { });
    }

    // ---------------------------------------------------------------- scan and check

    public async Task CheckAsync()
    {
        if (!await _opGate.WaitAsync(0))
            return;
        try
        {
            var game = _game;
            if (game is null)
                return;

            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            Update(() =>
            {
                _phase = Phase.Scanning;
                _status = Loc.T("core.status_reading");
                _fatal = null;
                _items = new();
                _problems = new();
                _smapiUpdate = null;
            });

            var mods = await Task.Run(() => ModScanner.Scan(game.ModsPath), ct);
            var gameLog = await Task.Run(() => SmapiLog.Read(), ct);
            Update(() =>
            {
                _mods = mods;
                _gameLog = gameLog;
                _phase = Phase.Checking;
                _status = Loc.T("core.status_found", Loc.Count("n.mod", mods.Count));
            });

            SyncCollectionProfiles(mods);
            RequestAllArt();
            await RunCheckAsync(game, mods, ct);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            Log.Write("Check failed: " + ex);
            var timedOut = ex is OperationCanceledException;
            Update(() =>
            {
                _phase = Phase.Idle;
                _fatal = timedOut
                    ? Loc.T("core.check_timeout")
                    : Loc.T("core.check_failed", ex.Message);
            });
        }
        finally
        {
            _opGate.Release();
        }
    }

    private async Task RunCheckAsync(GameInstall game, List<ModInfo> mods, CancellationToken ct)
    {
        var settings = Settings.Data;
        var active = mods.Where(m => !m.Disabled && !m.Bundled).ToList();
        var ignored = new HashSet<string>(settings.Ignored, StringComparer.OrdinalIgnoreCase);
        var installedIds = new HashSet<string>(mods.Where(m => !m.Disabled).Select(m => m.UniqueId), StringComparer.OrdinalIgnoreCase);

        var missing = FindMissingRequirements(active, installedIds);
        foreach (var (wantedId, wantedBy) in ProfileWantedMods(InstalledIds(mods)))
        {
            if (!missing.TryGetValue(wantedId, out var list))
                missing[wantedId] = list = new();
            list.AddRange(wantedBy);
        }

        var queries = active
            .GroupBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SmapiQuery(g.Key, g.First().UpdateKeys, g.First().Version))
            .ToList();
        foreach (var id in missing.Keys.Where(id => queries.All(q => !q.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            queries.Add(new SmapiQuery(id, Array.Empty<string>(), null));
        if (game.SmapiVersion is not null)
            queries.Add(new SmapiQuery("Pathoschild.SMAPI", new[] { "GitHub:Pathoschild/SMAPI" }, game.SmapiVersion));

        Dictionary<string, SmapiResult> results;
        try
        {
            results = await _smapi.CheckAsync(queries, game.SmapiVersion, game.GameVersion, ct);
        }
        catch (HttpRequestException ex)
        {
            Update(() =>
            {
                _phase = Phase.Idle;
                _fatal = Loc.T("core.smapi_unreachable", ex.Message);
            });
            return;
        }

        var rows = new List<ModRow>();
        var problems = new List<Problem>();
        var groups = new Dictionary<string, UpdateItem>();

        foreach (var m in mods)
        {
            if (m.Bundled)
            {
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Bundled, Loc.T("core.row_bundled"), m.FolderName));
                continue;
            }
            if (m.Disabled)
            {
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Disabled, Loc.T("core.row_off"), m.FolderName));
                continue;
            }
            if (ignored.Contains(m.UniqueId))
            {
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Ignored, Loc.T("core.row_ignored"), m.FolderName));
                continue;
            }

            results.TryGetValue(m.UniqueId, out var r);
            var ident = Identify(m, r);
            var compat = CompatibilityProblem(m, r);
            if (compat is not null)
                problems.Add(compat);

            if (r?.SuggestedVersion is { } skippedTarget && settings.SkippedVersions.TryGetValue(m.UniqueId, out var skippedValue)
                && ModVersion.Parse(skippedTarget) is { } sv && ModVersion.Parse(skippedValue) is { } kv && sv == kv)
            {
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.UpToDate, Loc.T("core.row_skipped", skippedTarget), m.FolderName, SkippedVersion: skippedTarget));
            }
            else if (r?.SuggestedVersion is { } target)
            {
                var key = ident.Key ?? "id:" + m.UniqueId;
                if (!groups.TryGetValue(key, out var item))
                {
                    item = new UpdateItem
                    {
                        Id = key,
                        Name = m.Name,
                        Installed = m.Version,
                        Target = target,
                        NexusId = ident.Nexus,
                        CurseForgeId = ident.CurseForge,
                        GitHubRepo = ident.GitHub,
                        ModDropId = ident.ModDrop,
                        NexusSubkey = ident.Subkey,
                        ModPageUrl = r.SuggestedUrl,
                    };
                    groups[key] = item;
                }
                else
                {
                    item.PartCount++;
                    var newer = ModVersion.Parse(target);
                    var cur = ModVersion.Parse(item.Target);
                    if (newer is not null && (cur is null || newer > cur))
                        item.Target = target;
                }
                item.UniqueIds.Add(m.UniqueId);
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Update, Loc.T("core.row_update", target), m.FolderName, Target: target));
            }
            else if (r is not null && (r.MainVersion is not null || r.Errors.Length == 0))
            {
                var note = Loc.T("core.row_ok");
                var state = RowState.UpToDate;
                if (r.UnofficialVersion is not null && compat is null && r.UnofficialUrl is not null)
                {
                    note = Loc.T("core.row_ok_unofficial", r.UnofficialVersion);
                }
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, state, note, m.FolderName));
            }
            else if (m.UpdateKeys.Length == 0 && (r is null || r.NexusId is null))
            {
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.NoInfo,
                    Loc.T("core.row_noinfo"), m.FolderName));
            }
            else
            {
                var why = r?.Errors.FirstOrDefault() ?? Loc.T("core.row_unreadable");
                var authorProblem = why.Contains("valid", StringComparison.OrdinalIgnoreCase)
                    || why.Contains("Found no", StringComparison.OrdinalIgnoreCase);
                if (authorProblem)
                    rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.NoInfo,
                        Loc.T("core.row_badkey"), m.FolderName));
                else
                    rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Error, Loc.T("core.row_error", why), m.FolderName));
            }
        }

        rows = rows.Select(r =>
        {
            var mod = mods.FirstOrDefault(x => x.UniqueId.Equals(r.UniqueId, StringComparison.OrdinalIgnoreCase) && x.FolderName == r.Folder);
            results.TryGetValue(r.UniqueId, out var res);
            var id = Identify(mod, res);
            var url = id.Nexus is not null ? NexusClient.ModPage(id.Nexus.ToString()!)
                : id.CurseForge is not null ? CurseForgeClient.PageUrl(id.CurseForge.Value)
                : id.GitHub is not null ? $"https://github.com/{id.GitHub}"
                : null;
            string[]? needs = null;
            if (mod is not null && !mod.Disabled)
            {
                var wanted = mod.Dependencies.Where(d => d.Required).Select(d => d.UniqueId).Concat(mod.ContentPackFor is null ? Array.Empty<string>() : new[] { mod.ContentPackFor });
                var lacking = wanted.Where(d => !installedIds.Contains(d) && !d.StartsWith("SMAPI.", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(d => results.TryGetValue(d, out var dr) && !string.IsNullOrWhiteSpace(dr.Name) ? dr.Name! : d).ToArray();
                if (lacking.Length > 0)
                    needs = lacking;
            }
            var unofficial = res?.UnofficialVersion is not null && res.UnofficialUrl is not null ? res.UnofficialVersion : null;
            return r with { Url = url, Author = mod?.Author, IsContentPack = mod?.ContentPackFor is not null, ParentId = mod?.ContentPackFor, HasConfig = mod is not null && File.Exists(Path.Combine(mod.FolderPath, "config.json")), Needs = needs, Unofficial = unofficial, UnofficialUrl = unofficial is null ? null : res!.UnofficialUrl };
        }).ToList();

        // missing required mods become "install" items
        foreach (var (depId, needs) in missing)
        {
            results.TryGetValue(depId, out var r);
            var who = string.Join(", ", needs.Select(n => n.Name).Distinct().Take(4));
            var ident = Identify(null, r);
            var target = r?.MainVersion ?? r?.SuggestedVersion;
            if (r is null || ident.Key is null || target is null)
            {
                problems.Add(new Problem("error", Loc.T("core.prob_missing_title", r?.Name ?? depId),
                    Loc.T("core.prob_missing_text", who, depId),
                    $"{AppEnvironment.NexusSiteUrl}/{AppEnvironment.NexusGame}/search/?gsearch={Uri.EscapeDataString(depId)}&gsearchtype=mods", Loc.T("core.prob_search")));
                continue;
            }
            var item = new UpdateItem
            {
                Id = "new:" + ident.Key,
                Name = r.Name ?? depId,
                Installed = null,
                Target = target,
                IsNew = true,
                Why = needs.All(n => n.UniqueId == "profile")
                    ? Loc.T("core.why_part_of", who)
                    : Loc.T("core.why_required", who),
                NexusId = ident.Nexus,
                CurseForgeId = ident.CurseForge,
                GitHubRepo = ident.GitHub,
                ModDropId = ident.ModDrop,
                ModPageUrl = r.MainVersion is null ? null : NexusPage(ident.Nexus),
            };
            item.UniqueIds.Add(depId);
            groups[item.Id] = item;
        }

        // mods of a Nexus collection that is still being installed (skipping any already offered for another reason)
        var offeredNexus = groups.Values.Where(g => g.NexusId is not null).Select(g => g.NexusId!.Value).ToHashSet();
        foreach (var it in CollectionItems(InstalledNexusIds(mods), offeredNexus))
            groups[it.Id] = it;

        // an installed mod that is too old for something else that depends on it
        foreach (var m in active)
        {
            foreach (var dep in m.Dependencies.Where(d => d.Required && d.MinimumVersion is not null))
            {
                var have = active.FirstOrDefault(x => x.UniqueId.Equals(dep.UniqueId, StringComparison.OrdinalIgnoreCase));
                if (have is null)
                    continue;
                var need = ModVersion.Parse(dep.MinimumVersion);
                var haveV = ModVersion.Parse(have.Version);
                if (need is not null && haveV is not null && haveV < need)
                    problems.Add(new Problem("warn", Loc.T("core.dep_old_title", have.Name, m.Name),
                        Loc.T("core.dep_old_body", m.Name, have.Name, dep.MinimumVersion!, have.Version)));
            }
        }

        var items = groups.Values.OrderBy(i => i.IsNew ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

        (string, string)? smapiUpdate = null;
        if (results.TryGetValue("Pathoschild.SMAPI", out var sr) && sr.SuggestedVersion is not null)
            smapiUpdate = (sr.SuggestedVersion, "https://smapi.io");

        Update(() =>
        {
            _rows = rows;
            _problems = problems;
            _items = items;
            _smapiUpdate = smapiUpdate;
            _status = Loc.T(items.Count == 0 ? "core.status_current" : "core.status_offers");
        });

        // work out where each update can come from
        using var limit = new SemaphoreSlim(4);
        await Task.WhenAll(items.Select(async item =>
        {
            await limit.WaitAsync(ct);
            try
            {
                await BuildOffersAsync(item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write($"Offers failed for {item.Name}: {ex.Message}");
                Update(() => item.Message = Loc.T("core.offers_failed", ex.Message));
            }
            finally
            {
                limit.Release();
            }
        }));

        Update(() =>
        {
            _phase = Phase.Review;
            _lastChecked = DateTime.Now;
            _status = "";
        });
    }

    private static string? NexusPage(long? id) => id is null ? null : NexusClient.ModPage(id.Value.ToString());

    private static Dictionary<string, List<ModInfo>> FindMissingRequirements(List<ModInfo> active, HashSet<string> installedIds)
    {
        var missing = new Dictionary<string, List<ModInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in active)
        {
            var needs = m.Dependencies.Where(d => d.Required).Select(d => d.UniqueId).ToList();
            if (m.ContentPackFor is not null)
                needs.Add(m.ContentPackFor);

            foreach (var id in needs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (installedIds.Contains(id) || id.StartsWith("SMAPI.", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("Pathoschild.SMAPI", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!missing.TryGetValue(id, out var list))
                    missing[id] = list = new();
                list.Add(m);
            }
        }
        return missing;
    }

    private sealed record Ident(string? Key, long? Nexus, long? CurseForge, string? GitHub, long? ModDrop, string? Subkey);

    private static Ident Identify(ModInfo? m, SmapiResult? r)
    {
        long? nexus = null, cf = null, md = null;
        string? gh = null, sub = null;
        if (m is not null)
        {
            foreach (var k in m.ParsedKeys)
            {
                switch (k.Site)
                {
                    case SourceKind.Nexus when nexus is null && long.TryParse(k.Id, out var n):
                        nexus = n;
                        sub = k.Subkey;
                        break;
                    case SourceKind.CurseForge when cf is null && long.TryParse(k.Id, out var c):
                        cf = c;
                        break;
                    case SourceKind.GitHub when gh is null && k.Id.Contains('/'):
                        gh = k.Id;
                        break;
                    case SourceKind.ModDrop when md is null && long.TryParse(k.Id, out var d):
                        md = d;
                        break;
                }
            }
        }
        nexus ??= r?.NexusId;
        cf ??= r?.CurseForgeId;
        gh ??= r?.GitHubRepo;
        md ??= r?.ModDropId;

        var key = nexus is not null ? "nexus:" + nexus
            : cf is not null ? "cf:" + cf
            : gh is not null ? "gh:" + gh
            : md is not null ? "md:" + md
            : null;
        return new Ident(key, nexus, cf, gh, md, sub);
    }

    private static Problem? CompatibilityProblem(ModInfo m, SmapiResult? r)
    {
        var status = r?.CompatibilityStatus;
        if (status is null || status is "Ok" or "Unknown")
            return null;
        var summary = string.IsNullOrWhiteSpace(r!.CompatibilitySummary) ? "" : " " + r.CompatibilitySummary;
        return status switch
        {
            "Unofficial" => new Problem("warn", Loc.T("core.compat_unofficial_title", m.Name),
                Loc.T("core.compat_unofficial_text") + summary, r.UnofficialUrl, r.UnofficialUrl is null ? null : Loc.T("core.compat_open_fix")),
            "Workaround" => new Problem("warn", Loc.T("core.compat_workaround_title", m.Name), summary.Trim()),
            "Broken" => new Problem("error", Loc.T("core.compat_broken_title", m.Name), Loc.T("core.compat_broken_text") + summary),
            "Obsolete" => new Problem("warn", Loc.T("core.compat_obsolete_title", m.Name), Loc.T("core.compat_obsolete_text") + summary),
            "Abandoned" => new Problem("warn", Loc.T("core.compat_abandoned_title", m.Name), Loc.T("core.compat_abandoned_text") + summary),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- offers

    private async Task BuildOffersAsync(UpdateItem item, CancellationToken ct)
    {
        var offers = new List<SourceOffer>();
        var allowPre = ModVersion.Parse(item.Installed)?.IsPrerelease == true;

        if (item.NexusId is { } nid)
        {
            var offer = await _nexus.FindAsync(nid.ToString(), item.Target, item.NexusSubkey, _nexusUser, Settings.Data.NxmRegistered && NxmRegistration.IsRegisteredToThisApp(), ct);
            offers.Add(offer);
            if (_nexusUser is not null)
            {
                var lines = await _nexus.GetChangelogAsync(nid.ToString(), item.Target, ct);
                Update(() => { item.Changelog.Clear(); item.Changelog.AddRange(lines); });
            }
        }

        var needAlternatives = item.NexusId is null
            || offers.All(o => o.Mode != DeliveryMode.Auto)
            || Settings.Data.Policy == SourcePolicy.InstantFirst;
        if (needAlternatives)
        {
            offers.AddRange(await FindAlternativesAsync(item, allowPre, ct));
            item.AlternativesLoaded = true;
        }
        else if (item.ModDropId is { } mid)
        {
            offers.Add(ModDropOffer(mid));
        }

        Update(() =>
        {
            item.Offers.Clear();
            item.Offers.AddRange(Order(offers));
            item.Chosen = Math.Max(0, item.Offers.FindIndex(o => o.Usable));
            if (item.Offers.Count == 0 || !item.Offers.Any(o => o.Usable))
            {
                item.Message = Loc.T("core.no_source");
                item.Selected = false;
            }
        });
    }

    private async Task<List<SourceOffer>> FindAlternativesAsync(UpdateItem item, bool allowPre, CancellationToken ct)
    {
        var list = new List<SourceOffer>();
        var name = item.Name;
        var uid = item.UniqueIds.FirstOrDefault() ?? "";
        if (item.CurseForgeId is { } cf)
        {
            var o = await _curse.FindAsync(cf, item.Target, allowPre, ct);
            if (o is not null) list.Add(o);
        }
        if (item.GitHubRepo is { } gh)
        {
            var o = await _github.FindAsync(gh, item.Target, name, uid, allowPre, ct);
            if (o is not null) list.Add(o);
        }
        if (item.ModDropId is { } md)
            list.Add(ModDropOffer(md));
        return list;
    }

    private static SourceOffer ModDropOffer(long id) => new()
    {
        Kind = SourceKind.ModDrop,
        Mode = DeliveryMode.Manual,
        PageUrl = $"https://www.moddrop.com/stardew-valley/mods/{id}",
        Note = Loc.T("core.moddrop_note"),
    };

    private List<SourceOffer> Order(List<SourceOffer> offers)
    {
        int Rank(SourceOffer o)
        {
            var site = o.Kind switch { SourceKind.Nexus => 0, SourceKind.CurseForge => 1, SourceKind.GitHub => 2, _ => 3 };
            if (!o.Usable) return 100 + site;
            if (Settings.Data.Policy == SourcePolicy.InstantFirst && o.Mode == DeliveryMode.Auto)
                return site;
            return Settings.Data.Policy == SourcePolicy.InstantFirst ? 10 + site : site;
        }
        return offers.OrderBy(Rank).ToList();
    }

    public void ChooseSource(string itemId, int index)
    {
        Update(() =>
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item is null || index < 0 || index >= item.Offers.Count || !item.Offers[index].Usable)
                return;
            item.Chosen = index;
            if (item.Status == ItemStatus.WaitingForYou)
                item.Waiter?.TrySetResult(new WaitResult(false, index));
        });
    }

    public async Task LoadAlternativesAsync(string itemId)
    {
        UpdateItem? item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item is null || item.AlternativesLoaded)
                return;
            item.AlternativesLoaded = true;
        }

        var more = await FindAlternativesAsync(item, ModVersion.Parse(item.Installed)?.IsPrerelease == true, CancellationToken.None);
        Update(() =>
        {
            var current = item.Offers.ElementAtOrDefault(item.Chosen);
            var all = item.Offers.Concat(more).ToList();
            item.Offers.Clear();
            item.Offers.AddRange(Order(all));
            item.Chosen = Math.Max(0, current is null ? 0 : item.Offers.IndexOf(current));
        });
    }

    public void SetSelected(string itemId, bool selected) =>
        Update(() =>
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item is not null && item.Status is ItemStatus.Ready or ItemStatus.Failed or ItemStatus.Skipped)
                item.Selected = selected;
        });

    public void SetAllSelected(bool selected) =>
        Update(() =>
        {
            foreach (var i in _items.Where(i => i.Status is ItemStatus.Ready or ItemStatus.Failed or ItemStatus.Skipped && i.Offers.Any(o => o.Usable)))
                i.Selected = selected;
        });

    public void SetIgnored(string uniqueId, bool ignore)
    {
        lock (_gate)
        {
            Settings.Data.Ignored.RemoveAll(x => x.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
            if (ignore)
                Settings.Data.Ignored.Add(uniqueId);
            Settings.Save();
        }
        _ = CheckAsync();
    }

    public void SetPolicy(SourcePolicy policy)
    {
        Settings.Data.Policy = policy;
        Settings.Save();
        Update(() =>
        {
            foreach (var i in _items.Where(i => i.Status == ItemStatus.Ready))
            {
                var ordered = Order(i.Offers.ToList());
                i.Offers.Clear();
                i.Offers.AddRange(ordered);
                i.Chosen = Math.Max(0, i.Offers.FindIndex(o => o.Usable));
            }
        });
    }

    // ---------------------------------------------------------------- Nexus account

    public async Task<string?> ConnectNexusAsync(string key)
    {
        key = key.Trim();
        if (key.Length < 20)
            return Loc.T("core.key_shape");
        try
        {
            var user = await _nexus.ValidateAsync(key, CancellationToken.None);
            Settings.NexusKey = key;
            Update(() =>
            {
                _nexusUser = user;
                _nexusMessage = null;
            });
            await RefreshOffersAsync();
            RequestAllArt();
            return null;
        }
        catch (NexusException ex)
        {
            return ex.Error == NexusError.InvalidKey
                ? Loc.T("core.key_rejected")
                : ex.Message;
        }
    }

    public async Task DisconnectNexusAsync()
    {
        Settings.NexusKey = null;
        Update(() => { _nexusUser = null; _nexusMessage = null; });
        await RefreshOffersAsync();
    }

    private async Task ValidateNexusAsync(string key)
    {
        try
        {
            var user = await _nexus.ValidateAsync(key, CancellationToken.None);
            Update(() => _nexusUser = user);
        }
        catch (NexusException ex)
        {
            Update(() => _nexusMessage = ex.Error == NexusError.InvalidKey
                ? Loc.T("core.key_saved_rejected")
                : Loc.T("core.key_check_failed", ex.Message));
        }
    }

    public string RegisterNxm()
    {
        string? previous;
        try
        {
            previous = NxmRegistration.Register();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        Settings.Data.NxmRegistered = true;
        if (previous is not null)
            Settings.Data.NxmPreviousHandler = previous;
        Settings.Save();
        _ = RefreshOffersAsync();
        return Loc.T(previous is null ? "core.nxm_done" : "core.nxm_done_previous");
    }

    public void UnregisterNxm()
    {
        NxmRegistration.Unregister(Settings.Data.NxmPreviousHandler);
        Settings.Data.NxmRegistered = false;
        Settings.Save();
        _ = RefreshOffersAsync();
    }

    private async Task RefreshOffersAsync()
    {
        List<UpdateItem> items;
        lock (_gate)
            items = _items.Where(i => i.Status == ItemStatus.Ready).ToList();
        foreach (var item in items)
        {
            try
            {
                await BuildOffersAsync(item, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Write("Refresh offers failed: " + ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- applying

    public async Task ApplyAsync(IEnumerable<string> itemIds)
    {
        var ids = itemIds.ToHashSet();
        if (GameLocator.IsGameRunning(fresh: true))
        {
            Update(() => _fatal = Loc.T("core.close_game_update"));
            return;
        }

        if (!await _opGate.WaitAsync(0))
            return;
        try
        {
            var ct = _cts.Token;
            List<UpdateItem> chosen;
            lock (_gate)
            {
                chosen = _items.Where(i => ids.Contains(i.Id) && i.Offers.Any(o => o.Usable)).ToList();
                _fatal = null;
            }
            if (chosen.Count == 0)
                return;

            Update(() =>
            {
                _phase = Phase.Applying;
                foreach (var i in chosen)
                {
                    i.Status = ItemStatus.Queued;
                    i.Message = Loc.T("core.msg_waiting");
                    i.Progress = 0;
                    i.Details.Clear();
                }
            });

            try
            {
                await Task.WhenAll(chosen.Select(i => RunItemAsync(i, ct)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write("Apply crashed: " + ex);
                Update(() =>
                {
                    foreach (var i in chosen.Where(i => i.Status is ItemStatus.Working or ItemStatus.Queued or ItemStatus.WaitingForYou))
                    {
                        i.Status = ItemStatus.Failed;
                        i.Message = Loc.T("core.msg_unexpected");
                    }
                });
            }
            await RescanQuietlyAsync();

            Update(() =>
            {
                RefreshRowsAfterInstall(chosen);
                _phase = Phase.Finished;
                var done = chosen.Count(i => i.Status == ItemStatus.Done);
                var failed = chosen.Count(i => i.Status == ItemStatus.Failed);
                var skipped = chosen.Count(i => i.Status == ItemStatus.Skipped);
                var parts = new List<string> { Loc.T("core.sum_done", done) };
                if (failed > 0)
                    parts.Add(Loc.T("core.sum_failed", failed));
                if (skipped > 0)
                    parts.Add(Loc.T("core.sum_skipped", skipped));
                _lastSummary = string.Join(Loc.T("core.sum_join"), parts) + Loc.T("core.sum_end");
            });
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>The order sources are tried in after the user switches to another one: everything already tried stays, the new choice goes next.</summary>
    internal static List<int> NextOrder(List<int> order, int pos, int to) =>
        order.Take(pos + 1).Concat(new[] { to }).Concat(order.Skip(pos + 1).Where(i => i != to)).ToList();

    private async Task RunItemAsync(UpdateItem item, CancellationToken ct)
    {
        var start = item.Chosen;
        var order = Enumerable.Range(start, item.Offers.Count - start)
            .Concat(Enumerable.Range(0, start))
            .ToList();
        var failures = new List<string>();

        for (var pos = 0; pos < order.Count; pos++)
        {
            var idx = order[pos];
            if (idx >= item.Offers.Count || !item.Offers[idx].Usable)
                continue;
            var offer = item.Offers[idx];

            try
            {
                if (offer.Mode == DeliveryMode.Auto)
                {
                    await RunAutoAsync(item, offer, ct);
                    return;
                }

                var wait = await WaitForUserAsync(item, offer, ct);
                if (wait.Installed)
                    return;
                if (wait.SwitchTo is { } to)
                {
                    order = NextOrder(order, pos, to);
                    continue;
                }
                Update(() =>
                {
                    item.Status = ItemStatus.Skipped;
                    item.Message = Loc.T("core.msg_skipped");
                });
                return;
            }
            catch (OperationCanceledException)
            {
                Update(() => { item.Status = ItemStatus.Skipped; item.Message = Loc.T("core.cancelled"); });
                return;
            }
            catch (Exception ex) when (ex is DownloadException or InstallException or ArchiveException or NexusException)
            {
                failures.Add($"{offer.Label}: {ex.Message}");
                Log.Write($"{item.Name} via {offer.Label} failed: {ex.Message}");
                Update(() => item.Details.Add(Loc.T("core.offer_failed", offer.Label, ex.Message)));
                if (ex is InstallException and not WrongFileException)
                {
                    // the folder was rolled back; a different source will not fix a locked file
                    Update(() => { item.Status = ItemStatus.Failed; item.Message = ex.Message; });
                    return;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // anything unexpected must end this item, never leave it spinning
                Log.Write($"{item.Name} via {offer.Label} crashed: {ex}");
                Update(() => { item.Status = ItemStatus.Failed; item.Message = Loc.T("core.msg_unexpected_ex", ex.Message); });
                return;
            }
        }

        if (!item.AlternativesLoaded)
        {
            var more = await FindAlternativesAsync(item, false, ct);
            if (more.Any(o => o.Usable && o.Mode == DeliveryMode.Auto))
            {
                Update(() =>
                {
                    item.AlternativesLoaded = true;
                    item.Offers.AddRange(more);
                });
                var extra = more.First(o => o.Usable && o.Mode == DeliveryMode.Auto);
                try
                {
                    await RunAutoAsync(item, extra, ct);
                    return;
                }
                catch (Exception ex) when (ex is DownloadException or InstallException or ArchiveException)
                {
                    failures.Add($"{extra.Label}: {ex.Message}");
                }
            }
        }

        Update(() =>
        {
            item.Status = ItemStatus.Failed;
            item.Message = failures.Count > 0
                ? Loc.T("core.msg_no_source_worked", failures.Last())
                : Loc.T("core.msg_no_source_update");
        });
    }

    private async Task RunAutoAsync(UpdateItem item, SourceOffer offer, CancellationToken ct)
    {
        Update(() =>
        {
            item.Status = ItemStatus.Working;
            item.Message = Loc.T("core.msg_downloading", offer.Label);
            item.Progress = 0;
        });

        string archive;
        await _downloadGate.WaitAsync(ct);
        try
        {
            var url = offer.DownloadUrl;
            if (url is null && offer.Kind == SourceKind.Nexus)
                url = await _nexus.GetDownloadUrlAsync(offer.NexusModId!, offer.NexusFileId!, null, null, ct);
            if (url is null)
                throw new DownloadException(Loc.T("core.no_link"));
            archive = await Downloader.FetchAsync(url, offer.FileName, p => Update(() => item.Progress = p), ct);
        }
        finally
        {
            _downloadGate.Release();
        }

        try
        {
            await InstallForItemsAsync(new[] { item }, archive, offer.Label, requireNewer: true, ct);
        }
        finally
        {
            Downloader.Cleanup(archive);
        }
    }

    private async Task<WaitResult> WaitForUserAsync(UpdateItem item, SourceOffer offer, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<WaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        Update(() =>
        {
            item.Waiter = tcs;
            item.Status = ItemStatus.WaitingForYou;
            item.PageOpened = false;
            item.Message = Loc.T("core.msg_queued");
        });
        PumpPages();
        try
        {
            return await tcs.Task;
        }
        finally
        {
            Update(() => item.Waiter = null);
            PumpPages();
        }
    }

    private void PumpPages()
    {
        List<(UpdateItem Item, string Url, string Message)> toOpen = new();
        lock (_gate)
        {
            var open = _items.Count(i => i.Status == ItemStatus.WaitingForYou && i.PageOpened && i.Waiter is not null);
            foreach (var i in _items.Where(i => i.Status == ItemStatus.WaitingForYou && !i.PageOpened && i.Waiter is not null))
            {
                if (open >= MaxOpenPages)
                    break;
                var offer = i.Offers[Math.Clamp(i.Chosen, 0, i.Offers.Count - 1)];
                i.PageOpened = true;
                open++;
                toOpen.Add((i, offer.PageUrl, HowTo(offer)));
            }
        }
        foreach (var (item, url, message) in toOpen)
        {
            Update(() => item.Message = message);
            try { _openUrl(url); } catch (Exception ex) { Log.Write("Could not open browser: " + ex.Message); }
        }
    }

    private static string HowTo(SourceOffer offer) => offer.Mode switch
    {
        DeliveryMode.OneClick => Loc.T("core.howto_oneclick"),
        _ when offer.Kind == SourceKind.Nexus => Loc.T("core.howto_nexus"),
        _ => Loc.T("core.howto_other", offer.Label),
    };

    public void SkipWaiting(string itemId) =>
        Update(() =>
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId);
            item?.Waiter?.TrySetResult(new WaitResult(false, null));
        });

    public void ReopenPage(string itemId)
    {
        string? url = null;
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item is not null && item.Offers.Count > 0)
                url = item.Offers[Math.Clamp(item.Chosen, 0, item.Offers.Count - 1)].PageUrl;
        }
        if (url is not null)
            _openUrl(url);
    }

    // ---------------------------------------------------------------- archives

    /// <summary>Called when an archive appears (browser download, dropped file, nxm download).</summary>
    public async Task HandleArchiveAsync(string path, string origin, CancellationToken ct, bool explicitFile = false)
    {
        List<ArchiveMod> mods;
        try
        {
            using var archive = ArchiveFile.Open(path);
            mods = ArchiveInspector.FindMods(archive);
        }
        catch (ArchiveException ex)
        {
            if (explicitFile)
                AddFound(path, origin, error: ex.Message);
            return;
        }
        catch (IOException)
        {
            return;
        }
        if (mods.Count == 0)
        {
            if (explicitFile)
                AddFound(path, origin, error: Loc.T("core.no_manifest"));
            return;
        }

        var ids = mods.Select(m => m.Manifest.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<UpdateItem> waiting;
        lock (_gate)
            waiting = _items.Where(i => i.Status == ItemStatus.WaitingForYou && i.Waiter is not null
                && (i.UniqueIds.Any(ids.Contains) || (i.AcceptAnyMod && i.NexusId is { } nid && NexusFileModId(path) == nid))).ToList();

        if (waiting.Count > 0)
        {
            try
            {
                await InstallForItemsAsync(waiting, path, origin, requireNewer: true, ct);
                foreach (var w in waiting)
                    w.Waiter?.TrySetResult(new WaitResult(true, null));
            }
            catch (Exception ex) when (ex is InstallException or ArchiveException)
            {
                Update(() =>
                {
                    foreach (var w in waiting)
                        w.Message = ex.Message + " The app keeps waiting for the right file.";
                });
            }
            return;
        }

        var installed = _mods;
        var interesting = mods.Any(m =>
        {
            var have = installed.FirstOrDefault(x => x.UniqueId.Equals(m.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase));
            if (have is null)
                return explicitFile;
            var a = ModVersion.Parse(m.Manifest.Version);
            var b = ModVersion.Parse(have.Version);
            return explicitFile || (a is not null && b is not null && a > b);
        });
        if (interesting)
            AddFound(path, origin, error: null, mods);
    }

    private void AddFound(string path, string origin, string? error, List<ArchiveMod>? mods = null)
    {
        var f = new FoundArchive { Path = path, FileName = Path.GetFileName(path), Origin = origin, Error = error };
        if (mods is not null)
        {
            foreach (var m in mods)
            {
                var have = _mods.FirstOrDefault(x => x.UniqueId.Equals(m.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase));
                f.Lines.Add(have is null
                    ? Loc.T("core.found_new", m.Manifest.Name, m.Manifest.Version)
                    : Loc.T("core.found_update", m.Manifest.Name, have.Version, m.Manifest.Version));
            }
        }
        Update(() =>
        {
            _found.RemoveAll(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            _found.Add(f);
        });
    }

    public async Task InstallFoundAsync(string id)
    {
        FoundArchive? f;
        lock (_gate)
            f = _found.FirstOrDefault(x => x.Id == id);
        if (f is null || f.Busy)
            return;
        Update(() => { f.Busy = true; f.Error = null; });
        try
        {
            var label = "a file you provided";
            await _installGate.WaitAsync();
            try
            {
                using var archive = ArchiveFile.Open(f.Path);
                var plan = Installer.Plan(f.Path, archive, _game!.ModsPath, _mods);
                await Task.Run(() => _installer.Apply(plan, _game.ModsPath, label));
            }
            finally
            {
                _installGate.Release();
            }
            await RescanQuietlyAsync();
            Update(() => _found.RemoveAll(x => x.Id == id));
            if (f.Path.StartsWith(AppEnvironment.StagingDir, StringComparison.OrdinalIgnoreCase))
                Downloader.Cleanup(f.Path);
            _ = CheckAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (InstallException or ArchiveException or IOException))
                Log.Write("Installing a found file crashed: " + ex);
            Update(() => { f.Busy = false; f.Error = ex is InstallException or ArchiveException or IOException ? ex.Message : Loc.T("core.install_failed", ex.Message); });
        }
    }

    public void DismissFound(string id) => Update(() => _found.RemoveAll(x => x.Id == id));

    /// <summary>Saves an uploaded (dragged in) file to staging and offers it for install.</summary>
    public async Task AddUploadedFileAsync(string stagedPath, string originalName) =>
        await HandleArchiveAsync(stagedPath, "the file you dropped in", CancellationToken.None, explicitFile: true);

    private async Task InstallForItemsAsync(IReadOnlyList<UpdateItem> items, string archivePath, string origin, bool requireNewer, CancellationToken ct)
    {
        await _installGate.WaitAsync(ct);
        var before = items.Select(i => (i.Status, i.Message, i.Progress)).ToList();
        try
        {
            Update(() =>
            {
                foreach (var i in items)
                {
                    i.Status = ItemStatus.Working;
                    i.Message = "Installing";
                    i.Progress = 100;
                }
            });

            var game = _game ?? throw new InstallException(Loc.T("core.no_game"));
            using var archive = ArchiveFile.Open(archivePath);
            var plan = Installer.Plan(archivePath, archive, game.ModsPath, _mods);

            var wanted = items.Any(i => i.AcceptAnyMod)
                ? plan.Folders.Select(f => f.Source.Manifest.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : items.SelectMany(i => i.UniqueIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matching = plan.Folders.Where(f => wanted.Contains(f.Source.Manifest.UniqueId)).ToList();
            if (matching.Count == 0)
                throw new WrongFileException(Loc.T("core.wrong_mod"));

            if (requireNewer)
            {
                foreach (var f in matching.Where(f => f.Replaces is not null))
                {
                    var a = ModVersion.Parse(f.Source.Manifest.Version);
                    var b = ModVersion.Parse(f.Replaces!.Version);
                    if (a is not null && b is not null && a <= b)
                        throw new WrongFileException(Loc.T("core.not_newer", f.Source.Manifest.Name, f.Source.Manifest.Version, f.Replaces.Version));
                }
            }

            InstallResult result = await Task.Run(() => _installer.Apply(plan, game.ModsPath, origin), ct);

            Update(() =>
            {
                foreach (var i in items)
                {
                    i.Status = ItemStatus.Done;
                    i.HistoryId = result.HistoryId;
                    i.Message = Loc.T("core.msg_installed", origin);
                    if (i.AcceptAnyMod)
                        foreach (var id in wanted.Where(id => !i.UniqueIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
                            i.UniqueIds.Add(id);
                    i.Details.AddRange(result.Messages);
                    i.Details.AddRange(plan.Folders.SelectMany(f => f.Warnings));
                    var extras = plan.Folders.Where(f => !wanted.Contains(f.Source.Manifest.UniqueId)).ToList();
                    if (extras.Count > 0)
                        i.Details.Add(Loc.T("core.also_installed", string.Join(", ", extras.Select(e => e.Source.Manifest.Name))));
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Update(() =>
            {
                for (var n = 0; n < items.Count; n++)
                    (items[n].Status, items[n].Message, items[n].Progress) = before[n];
            });
            if (ex is InstallException or ArchiveException)
                throw;
            Log.Write("Install crashed: " + ex);
            throw new InstallException(Loc.T("core.install_failed", ex.Message));
        }
        finally
        {
            _installGate.Release();
        }
        await RescanQuietlyAsync();
    }

    private void RefreshRowsAfterInstall(IEnumerable<UpdateItem> items)
    {
        var rows = _rows.ToList();
        foreach (var uid in items.Where(i => i.Status == ItemStatus.Done).SelectMany(i => i.UniqueIds))
        {
            var mod = _mods.FirstOrDefault(m => m.UniqueId.Equals(uid, StringComparison.OrdinalIgnoreCase));
            if (mod is null)
                continue;
            var old = rows.FirstOrDefault(r => r.UniqueId.Equals(uid, StringComparison.OrdinalIgnoreCase));
            rows.RemoveAll(r => r.UniqueId.Equals(uid, StringComparison.OrdinalIgnoreCase));
            rows.Add(new ModRow(mod.Name, mod.UniqueId, mod.Version, RowState.UpToDate, Loc.T("core.row_updated"), mod.FolderName, old?.Url, mod.Author, mod.ContentPackFor is not null, ParentId: mod.ContentPackFor, HasConfig: File.Exists(Path.Combine(mod.FolderPath, "config.json"))));
        }
        _rows = rows.OrderBy(r => r.State == RowState.Bundled).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task RescanQuietlyAsync()
    {
        var game = _game;
        if (game is null)
            return;
        var mods = await Task.Run(() => ModScanner.Scan(game.ModsPath));
        SyncCollectionProfiles(mods);
        Update(() => _mods = mods);
    }

    // ---------------------------------------------------------------- nxm links

    public async Task HandleNxmAsync(string raw)
    {
        var link = NxmLink.Parse(raw);
        if (link is null)
        {
            Update(() => _fatal = Loc.T("core.nxm_unreadable"));
            return;
        }
        if (!link.Game.Equals(AppEnvironment.NexusGame, StringComparison.OrdinalIgnoreCase))
        {
            Update(() => _fatal = Loc.T("core.nxm_other_game"));
            return;
        }
        if (string.IsNullOrEmpty(Settings.NexusKey))
        {
            Update(() => _fatal = Loc.T("core.nxm_no_key"));
            return;
        }

        UpdateItem? item;
        lock (_gate)
            item = _items.FirstOrDefault(i => i.Status == ItemStatus.WaitingForYou && i.Waiter is not null && i.NexusId?.ToString() == link.ModId);

        try
        {
            if (item is not null)
                Update(() => { item.Status = ItemStatus.Working; item.Message = Loc.T("core.msg_downloading", "Nexus"); item.Progress = 0; });

            var url = await _nexus.GetDownloadUrlAsync(link.ModId, link.FileId, link.Key, link.Expires, CancellationToken.None);
            var path = await Downloader.FetchAsync(url, null, p => { if (item is not null) Update(() => item.Progress = p); }, CancellationToken.None);

            if (item is not null)
            {
                try
                {
                    await InstallForItemsAsync(new[] { item }, path, "Nexus Mods", requireNewer: true, CancellationToken.None);
                    item.Waiter?.TrySetResult(new WaitResult(true, null));
                }
                finally
                {
                    Downloader.Cleanup(path);
                }
            }
            else
            {
                await HandleArchiveAsync(path, "a Nexus download", CancellationToken.None, explicitFile: true);
            }
        }
        catch (Exception ex) when (ex is NexusException or DownloadException or InstallException or ArchiveException)
        {
            Update(() =>
            {
                if (item is not null)
                {
                    item.Status = ItemStatus.WaitingForYou;
                    item.Message = ex.Message + " Try the download button on Nexus again.";
                }
                else
                {
                    _fatal = ex.Message;
                }
            });
        }
    }

    // ---------------------------------------------------------------- undo

    public async Task<string?> UndoAsync(string historyId)
    {
        var game = _game;
        if (game is null)
            return Loc.T("core.no_game");
        await _installGate.WaitAsync();
        try
        {
            await Task.Run(() => _installer.Undo(historyId, game.ModsPath));
        }
        catch (InstallException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "A file is in use, so the undo stopped: " + ex.Message;
        }
        finally
        {
            _installGate.Release();
        }
        await RescanQuietlyAsync();
        _ = CheckAsync();
        return null;
    }

    /// <summary>Plain-text details for a bug report. Contains no keys, and user folders are masked.</summary>
    public string Diagnostics()
    {
        var game = _game;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{AppEnvironment.AppName} {AppEnvironment.Version}");
        sb.AppendLine("System: " + Platform.Description);
        sb.AppendLine($"Game: {game?.GameVersion ?? "?"} ({game?.Store ?? "not set"}), SMAPI {game?.SmapiVersion ?? "not found"}");
        lock (_gate)
        {
            sb.AppendLine($"Mods: {_mods.Count}, updates found: {_items.Count(i => !i.IsNew)}, missing required: {_items.Count(i => i.IsNew)}");
            sb.AppendLine($"Nexus: {(_nexusUser is null ? "not connected" : _nexusUser.IsPremium ? "premium" : "free")}, links handled: {Settings.Data.NxmRegistered}");
            sb.AppendLine($"Policy: {Settings.Data.Policy}, window: {(Settings.Data.AppWindow ? "app" : "browser")}");
            if (_fatal is not null)
                sb.AppendLine("Last error: " + _fatal);
        }
        sb.AppendLine();
        sb.AppendLine("Log (last lines):");
        try
        {
            var log = Path.Combine(AppEnvironment.LogsDir, "app.log");
            if (File.Exists(log))
                foreach (var line in File.ReadLines(log).TakeLast(40))
                    sb.AppendLine(line);
        }
        catch (IOException)
        {
        }
        var text = sb.ToString();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? text : text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Switches a mod on or off the way SMAPI documents: a folder whose name starts with a dot is skipped.
    /// Turning off a mod that others need asks for confirmation first.
    /// </summary>
    public async Task<(string? Error, string[]? Dependents)> SetModEnabledAsync(string uniqueId, bool enable, bool force)
    {
        if (GameLocator.IsGameRunning(fresh: true))
            return (Loc.T("core.close_game_first"), null);

        ModInfo? mod;
        List<ModInfo> all;
        lock (_gate)
        {
            all = _mods.ToList();
            mod = all.FirstOrDefault(m => m.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase) && m.Disabled == enable)
                ?? all.FirstOrDefault(m => m.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
        }
        if (mod is null || mod.Bundled)
            return (Loc.T("core.mod_not_found"), null);

        var folder = mod.FolderPath.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(folder)!;
        var name = Path.GetFileName(folder);

        if (!enable)
        {
            if (name.StartsWith('.'))
                return (null, null);
            var dependents = all
                .Where(m => !m.Disabled && !m.UniqueId.Equals(mod.UniqueId, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(m.ContentPackFor, mod.UniqueId, StringComparison.OrdinalIgnoreCase)
                        || m.Dependencies.Any(d => d.Required && d.UniqueId.Equals(mod.UniqueId, StringComparison.OrdinalIgnoreCase))))
                .Select(m => m.Name).Distinct().ToArray();
            if (dependents.Length > 0 && !force)
                return (null, dependents);
        }
        else if (!name.StartsWith('.'))
        {
            return (Loc.T("core.parent_off"), null);
        }

        var renameError = RenameMod(mod, enable);
        if (renameError is not null)
            return (renameError, null);

        await RescanQuietlyAsync();
        _ = CheckAsync();
        return (null, null);
    }

    public void SetPrefs(string? theme, bool? tourSeen)
    {
        lock (_gate)
        {
            if (theme is "auto" or "light" or "dark")
                Settings.Data.Theme = theme;
            if (tourSeen is { } seen)
                Settings.Data.TourSeen = seen;
            Settings.Save();
            Update(() => { });
        }
    }

    /// <summary>Saves the language ("auto" follows the system) and switches the words of the app to it.</summary>
    public void SetLanguage(string code)
    {
        var known = code.Equals("auto", StringComparison.OrdinalIgnoreCase) || Loc.All.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (!known)
            return;
        lock (_gate)
        {
            Settings.Data.Language = code.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : Loc.All.First(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Code;
            Settings.Save();
        }
        Loc.Instance.SetLanguage(Settings.Data.Language);
        Update(() => { });
        // notes the last check wrote down (row notes, offers, problems) are in the old language; read them again
        Phase phase;
        lock (_gate)
            phase = _phase;
        if (phase is Phase.Idle or Phase.Review)
            _ = CheckAsync();
    }

    /// <summary>Hides one specific version of a mod's update. A later version shows up again.</summary>
    public void SkipVersion(string itemId)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item is null || item.IsNew || item.Status is ItemStatus.Working or ItemStatus.WaitingForYou or ItemStatus.Queued)
                return;
            foreach (var uid in item.UniqueIds)
                Settings.Data.SkippedVersions[uid] = item.Target;
            Settings.Save();
            _items = _items.Where(i => i.Id != itemId).ToList();
            var ids = new HashSet<string>(item.UniqueIds, StringComparer.OrdinalIgnoreCase);
            _rows = _rows.Select(r => ids.Contains(r.UniqueId) && r.State == RowState.Update
                ? r with { State = RowState.UpToDate, Note = Loc.T("core.row_skipped", item.Target), SkippedVersion = item.Target }
                : r).ToList();
            Update(() => { });
        }
    }

    public void UnskipVersion(string uniqueId)
    {
        lock (_gate)
        {
            if (Settings.Data.SkippedVersions.Remove(uniqueId))
                Settings.Save();
        }
        _ = CheckAsync();
    }

    public void DismissLog()
    {
        lock (_gate)
        {
            if (_gameLog is not null)
            {
                Settings.Data.LogDismissedTicks = _gameLog.WrittenAt.Ticks;
                Settings.Save();
            }
            Update(() => { });
        }
    }

    public string? LaunchGame()
    {
        var game = _game;
        return game is null ? Loc.T("core.no_game") : GameProcess.Launch(game);
    }

    public string? ModFolder(string uniqueId)
    {
        lock (_gate)
            return _mods.FirstOrDefault(m => m.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase))?.FolderPath;
    }

    public void Dismiss() => Update(() => { _fatal = null; if (_phase == Phase.Finished) _phase = Phase.Review; });

    public void BackToReview() => Update(() =>
    {
        _phase = Phase.Review;
        _items = _items.Where(i => i.Status != ItemStatus.Done).ToList();
        foreach (var i in _items.Where(i => i.Status is ItemStatus.Skipped or ItemStatus.Failed))
        {
            i.Status = ItemStatus.Ready;
            i.Message = "";
        }
    });

    // ---------------------------------------------------------------- state

    private void Update(Action change)
    {
        lock (_gate)
        {
            change();
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
                return _phase is Phase.Scanning or Phase.Checking or Phase.Applying
                    || _items.Any(i => i.Status is ItemStatus.Working or ItemStatus.WaitingForYou);
        }
    }

    /// <summary>Raised after any state change, from whatever thread made it. Listeners should coalesce and hop to their UI thread.</summary>
    public event Action? Changed;

    public StateSnapshot Snapshot()
    {
        lock (_gate)
        {
            var game = _game;
            var running = game is not null && GameLocator.IsGameRunning();
            var nxmStatus = NxmRegistration.Status();
            return new StateSnapshot(
                Version: _version,
                Phase: _phase,
                Status: _status,
                Error: _fatal,
                Summary: _lastSummary,
                App: new AppInfo(
                    AppEnvironment.AppName, AppEnvironment.Version, AppEnvironment.RepoUrl, Platform.SmapiName, AppEnvironment.DataDir,
                    Platform.IsLinux ? new ShortcutInfo(LinuxShortcut.IsInstalled()) : null,
                    AppUpdateNotice()),
                Game: game is null ? null : new GameInfo(
                    game.GamePath, game.ModsPath, game.Store, game.GameVersion, game.SmapiVersion, running, GameProcess.CanLaunch(game), Platform.SmapiName),
                Candidates: _candidates.Select(c => new CandidateInfo(c.GamePath, c.Store, c.SmapiVersion)).ToList(),
                Welcomed: Settings.Data.Welcomed,
                SmapiUpdate: _smapiUpdate is null ? null : new UpdateNotice(_smapiUpdate.Value.Version, _smapiUpdate.Value.Url),
                LastChecked: _lastChecked,
                Nexus: new NexusInfo(
                    _nexusUser is not null, _nexusUser?.Name, _nexusUser?.IsPremium ?? false, _nexusMessage,
                    Settings.Data.NxmRegistered && nxmStatus.Ours, nxmStatus.Other, NxmRegistration.Supported, Settings.KeyStorage),
                Settings: new SettingsInfo(
                    Settings.Data.Policy, Settings.Data.AppWindow, Settings.Data.Theme, Settings.Data.TourSeen, Settings.DownloadsPath,
                    _watcher.Folder is not null, Settings.Data.BackupsPerMod, Settings.Data.Ignored.ToList(), Settings.Data.GroupContentPacks, Settings.Data.Language, Settings.Data.CheckAppUpdates),
                Items: _items.Select(i => new ItemInfo(
                    i.Id, i.Name, i.Installed, i.Target, i.PartCount, i.IsNew, i.Why, i.Status, i.Message, i.Progress, i.Selected, i.Chosen,
                    i.AlternativesLoaded, i.Changelog.ToList(), i.ModPageUrl, i.Details.ToList(), i.HistoryId,
                    i.Offers.Select(o => new OfferInfo(o.Kind, o.Label, o.Mode, o.Version, o.Usable, o.Note)).ToList(), ModArtStore.KeyFor(i.NexusId, i.GitHubRepo))).ToList(),
                Rows: _rows.ToList(),
                Problems: _problems.ToList(),
                Found: _found.Select(f => new FoundInfo(f.Id, f.FileName, f.Origin, f.Lines.ToList(), f.Busy, f.Error)).ToList(),
                History: History.All().Take(40).Select(h => new HistoryInfo(
                    h.Id, h.When, h.Name, h.FromVersion, h.ToVersion, h.Source, h.Undone,
                    !h.Undone && (h.BackupFile is not null || h.Folders.All(f => f.WasNew)), h.Note == "Removed")).ToList(),
                GameLog: _gameLog is null ? null : new GameLogInfo(
                    _gameLog.WrittenAt, _gameLog.LoadedMods, _gameLog.LoadedContentPacks,
                    _gameLog.Skipped.Select(s => new SkippedInfo(s.Name, s.Version, s.Reason, s.Kind, s.Hint)).ToList(),
                    _gameLog.Errors, _gameLog.WrittenAt.Ticks <= Settings.Data.LogDismissedTicks),
                BackupBytes: History.BackupBytes(),
                Profiles: BuildProfilesInfoLocked(),
                ModNotes: new Dictionary<string, string>(Settings.Data.ModNotes, StringComparer.OrdinalIgnoreCase),
                Endorsed: _endorsed.ToList());
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        _appCts.Cancel();
        _watcher.Dispose();
    }
}
