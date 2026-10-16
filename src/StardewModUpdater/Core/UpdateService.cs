namespace StardewModUpdater.Core;

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

internal sealed record ModRow(string Name, string UniqueId, string Version, RowState State, string Note, string Folder, string? Url = null, string? Author = null, bool IsContentPack = false);

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
internal sealed class UpdateService : IDisposable
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
    private (string Version, string Url)? _appUpdate;
    private GameLog? _gameLog;

    public SettingsStore Settings { get; }
    public HistoryStore History { get; }

    public UpdateService(SettingsStore settings, HistoryStore history, Action<string> openUrl)
    {
        Settings = settings;
        History = history;
        _openUrl = openUrl;
        _installer = new Installer(history, settings);
        _nexus = new NexusClient(() => settings.NexusKey);
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
                _fatal = "Something went wrong while starting up: " + ex.Message;
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
                return "This looks like Stardew Valley from before version 1.6. Current SMAPI and mods need game version 1.6 or newer, so update the game first.";
            else
                return "That folder does not look like a Stardew Valley install. It should contain \"Stardew Valley.dll\" (the game's main file).";
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
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            Update(() =>
            {
                _phase = Phase.Scanning;
                _status = "Reading your Mods folder";
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
                _status = $"Found {mods.Count} mods. Asking SMAPI which ones have updates";
            });

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
                    ? "The update check took too long and was stopped. Check your internet connection and try again."
                    : "The update check failed: " + ex.Message;
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
                _fatal = "Could not reach smapi.io, the service SMAPI itself uses to find updates (" + ex.Message + "). Check your internet connection and try again.";
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
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Bundled, "Comes with SMAPI. It updates when SMAPI does.", m.FolderName));
                continue;
            }
            if (m.Disabled)
            {
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Disabled, "Switched off (the folder name starts with a dot). Not checked.", m.FolderName));
                continue;
            }
            if (ignored.Contains(m.UniqueId))
            {
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Ignored, "You told the app to skip this mod.", m.FolderName));
                continue;
            }

            results.TryGetValue(m.UniqueId, out var r);
            var ident = Identify(m, r);
            var compat = CompatibilityProblem(m, r);
            if (compat is not null)
                problems.Add(compat);

            if (r?.SuggestedVersion is { } target)
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
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Update, $"Update available: {target}", m.FolderName));
            }
            else if (r is not null && (r.MainVersion is not null || r.Errors.Length == 0))
            {
                var note = "Up to date.";
                var state = RowState.UpToDate;
                if (r.UnofficialVersion is not null && compat is null && r.UnofficialUrl is not null)
                {
                    note = $"Up to date. A community-patched version ({r.UnofficialVersion}) also exists.";
                }
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, state, note, m.FolderName));
            }
            else if (m.UpdateKeys.Length == 0 && (r is null || r.NexusId is null))
            {
                rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.NoInfo,
                    "The author did not add update information, so no site can be checked for this mod.", m.FolderName));
            }
            else
            {
                var why = r?.Errors.FirstOrDefault() ?? "The mod's page could not be read.";
                var authorProblem = why.Contains("valid", StringComparison.OrdinalIgnoreCase)
                    || why.Contains("Found no", StringComparison.OrdinalIgnoreCase);
                if (authorProblem)
                    rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.NoInfo,
                        "Its update key points nowhere (a placeholder or a deleted page). Common for the extra pieces of a big mod; the main mod is checked on its own.", m.FolderName));
                else
                    rows.Add(new ModRow(m.Name, m.UniqueId, m.Version, RowState.Error, "Could not check: " + why, m.FolderName));
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
            return r with { Url = url, Author = mod?.Author, IsContentPack = mod?.ContentPackFor is not null };
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
                problems.Add(new Problem("error", $"Missing required mod: {r?.Name ?? depId}",
                    $"{who} needs {depId}, but SMAPI's list has no download page for it. Search for it on Nexus Mods and install it by hand, or SMAPI will skip {who}.",
                    $"{AppEnvironment.NexusSiteUrl}/{AppEnvironment.NexusGame}/search/?gsearch={Uri.EscapeDataString(depId)}&gsearchtype=mods", "Search Nexus"));
                continue;
            }
            var item = new UpdateItem
            {
                Id = "new:" + ident.Key,
                Name = r.Name ?? depId,
                Installed = null,
                Target = target,
                IsNew = true,
                Why = $"Required by {who}. SMAPI skips those mods until this is installed.",
                NexusId = ident.Nexus,
                CurseForgeId = ident.CurseForge,
                GitHubRepo = ident.GitHub,
                ModDropId = ident.ModDrop,
                ModPageUrl = r.MainVersion is null ? null : NexusPage(ident.Nexus),
            };
            item.UniqueIds.Add(depId);
            groups[item.Id] = item;
        }

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
                    problems.Add(new Problem("warn", $"{have.Name} is too old for {m.Name}",
                        $"{m.Name} needs {have.Name} {dep.MinimumVersion} or newer, and {have.Version} is installed. Updating {have.Name} fixes this."));
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
            _status = items.Count == 0 ? "Everything is up to date" : "Looking up download options";
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
                Update(() => item.Message = "Could not look up download options: " + ex.Message);
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
            "Unofficial" => new Problem("warn", $"{m.Name} needs a community fix",
                "The official version does not work with your game version, but an unofficial patch exists." + summary, r.UnofficialUrl, r.UnofficialUrl is null ? null : "Open the fix"),
            "Workaround" => new Problem("warn", $"{m.Name} has a known workaround", summary.Trim()),
            "Broken" => new Problem("error", $"{m.Name} is broken in this game version", "SMAPI's compatibility list marks it broken." + summary),
            "Obsolete" => new Problem("warn", $"{m.Name} is obsolete", "The mod is no longer needed or was replaced." + summary),
            "Abandoned" => new Problem("warn", $"{m.Name} looks abandoned", "The author stopped maintaining it." + summary),
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
                item.Message = "No download source could be found. Open the mod page and install it by hand, or drop the file into this window.";
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
        Note = "ModDrop does not allow automatic downloads. The app opens the page and installs the file when it lands in your Downloads folder.",
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
            return "That does not look like a Nexus key. It is a long string of letters and numbers from your Nexus account page.";
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
            return null;
        }
        catch (NexusException ex)
        {
            return ex.Error == NexusError.InvalidKey
                ? "Nexus did not accept that key. Copy it again from nexusmods.com/settings/api-keys (the \"Personal API Key\" at the bottom)."
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
                ? "Your saved Nexus key was rejected. Add it again to get automatic downloads."
                : "Could not check your Nexus key right now: " + ex.Message);
        }
    }

    public string RegisterNxm()
    {
        var previous = NxmRegistration.Register();
        Settings.Data.NxmRegistered = true;
        if (previous is not null)
            Settings.Data.NxmPreviousHandler = previous;
        Settings.Save();
        _ = RefreshOffersAsync();
        return previous is null ? "Done." : "Done. The app that handled Nexus links before (probably Vortex or Mod Organizer) can be put back from this page.";
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
            Update(() => _fatal = "Stardew Valley or SMAPI is running. Close it completely, then press Update again. Mods cannot be swapped safely while the game has them open.");
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
                    i.Message = "Waiting its turn";
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
                        i.Message = "Something unexpected went wrong. Nothing was changed. The log has details.";
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
                _lastSummary = $"{done} done" + (failed > 0 ? $", {failed} failed" : "") + (skipped > 0 ? $", {skipped} skipped" : "") + ".";
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
                    item.Message = "Skipped. Nothing was changed.";
                });
                return;
            }
            catch (OperationCanceledException)
            {
                Update(() => { item.Status = ItemStatus.Skipped; item.Message = "Cancelled."; });
                return;
            }
            catch (Exception ex) when (ex is DownloadException or InstallException or ArchiveException or NexusException)
            {
                failures.Add($"{offer.Label}: {ex.Message}");
                Log.Write($"{item.Name} via {offer.Label} failed: {ex.Message}");
                Update(() => item.Details.Add($"{offer.Label} did not work: {ex.Message}"));
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
                Update(() => { item.Status = ItemStatus.Failed; item.Message = "Something unexpected went wrong (" + ex.Message + "). Nothing was changed. The log has details."; });
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
                ? "No source worked. " + failures.Last()
                : "No source could supply this update.";
        });
    }

    private async Task RunAutoAsync(UpdateItem item, SourceOffer offer, CancellationToken ct)
    {
        Update(() =>
        {
            item.Status = ItemStatus.Working;
            item.Message = $"Downloading from {offer.Label}";
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
                throw new DownloadException("There is no download link.");
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
            item.Message = "Queued. The page opens after the ones before it are done.";
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
        DeliveryMode.OneClick => "Your browser opened the Nexus page. Click \"Mod Manager Download\" and the app does the rest.",
        _ when offer.Kind == SourceKind.Nexus => "Your browser opened the Nexus page. Log in if asked, click \"Manual Download\" then \"Slow Download\". The app installs the file when it lands in your Downloads folder.",
        _ => $"Your browser opened the {offer.Label} page. Download the file; the app installs it when it lands in your Downloads folder.",
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
                AddFound(path, origin, error: "This archive has no manifest.json, so it is not a SMAPI mod.");
            return;
        }

        var ids = mods.Select(m => m.Manifest.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<UpdateItem> waiting;
        lock (_gate)
            waiting = _items.Where(i => i.Status == ItemStatus.WaitingForYou && i.Waiter is not null && i.UniqueIds.Any(ids.Contains)).ToList();

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
                    ? $"{m.Manifest.Name} {m.Manifest.Version} (new install)"
                    : $"{m.Manifest.Name}: {have.Version} to {m.Manifest.Version}");
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
            Update(() => { f.Busy = false; f.Error = ex is InstallException or ArchiveException or IOException ? ex.Message : "The file could not be installed (" + ex.Message + "). Nothing was changed."; });
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

            var game = _game ?? throw new InstallException("No game folder is set.");
            using var archive = ArchiveFile.Open(archivePath);
            var plan = Installer.Plan(archivePath, archive, game.ModsPath, _mods);

            var wanted = items.SelectMany(i => i.UniqueIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matching = plan.Folders.Where(f => wanted.Contains(f.Source.Manifest.UniqueId)).ToList();
            if (matching.Count == 0)
                throw new WrongFileException("The file is for a different mod than expected, so it was not installed.");

            if (requireNewer)
            {
                foreach (var f in matching.Where(f => f.Replaces is not null))
                {
                    var a = ModVersion.Parse(f.Source.Manifest.Version);
                    var b = ModVersion.Parse(f.Replaces!.Version);
                    if (a is not null && b is not null && a <= b)
                        throw new WrongFileException($"The file holds {f.Source.Manifest.Name} {f.Source.Manifest.Version}, which is not newer than the installed {f.Replaces.Version}.");
                }
            }

            InstallResult result = await Task.Run(() => _installer.Apply(plan, game.ModsPath, origin), ct);

            Update(() =>
            {
                foreach (var i in items)
                {
                    i.Status = ItemStatus.Done;
                    i.HistoryId = result.HistoryId;
                    i.Message = $"Installed from {origin}.";
                    i.Details.AddRange(result.Messages);
                    i.Details.AddRange(plan.Folders.SelectMany(f => f.Warnings));
                    var extras = plan.Folders.Where(f => !wanted.Contains(f.Source.Manifest.UniqueId)).ToList();
                    if (extras.Count > 0)
                        i.Details.Add("The download also contained " + string.Join(", ", extras.Select(e => e.Source.Manifest.Name)) + ", which are now installed too.");
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
            throw new InstallException("The file could not be installed (" + ex.Message + "). Nothing was changed.");
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
            rows.Add(new ModRow(mod.Name, mod.UniqueId, mod.Version, RowState.UpToDate, "Updated just now.", mod.FolderName, old?.Url, mod.Author, mod.ContentPackFor is not null));
        }
        _rows = rows.OrderBy(r => r.State == RowState.Bundled).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task RescanQuietlyAsync()
    {
        var game = _game;
        if (game is null)
            return;
        var mods = await Task.Run(() => ModScanner.Scan(game.ModsPath));
        Update(() => _mods = mods);
    }

    // ---------------------------------------------------------------- nxm links

    public async Task HandleNxmAsync(string raw)
    {
        var link = NxmLink.Parse(raw);
        if (link is null)
        {
            Update(() => _fatal = "The app received a Nexus link it could not read.");
            return;
        }
        if (!link.Game.Equals(AppEnvironment.NexusGame, StringComparison.OrdinalIgnoreCase))
        {
            Update(() => _fatal = "That Nexus link is for a different game than Stardew Valley, so it was ignored.");
            return;
        }
        if (string.IsNullOrEmpty(Settings.NexusKey))
        {
            Update(() => _fatal = "A Nexus link arrived, but no Nexus key is saved yet. Add your key under Nexus account, then click the download button again.");
            return;
        }

        UpdateItem? item;
        lock (_gate)
            item = _items.FirstOrDefault(i => i.Status == ItemStatus.WaitingForYou && i.Waiter is not null && i.NexusId?.ToString() == link.ModId);

        try
        {
            if (item is not null)
                Update(() => { item.Status = ItemStatus.Working; item.Message = "Downloading from Nexus"; item.Progress = 0; });

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
            return "No game folder is set.";
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

    /// <summary>Looks for a newer release of this app on GitHub. Silent when offline or when no release exists yet.</summary>
    public async Task CheckAppUpdateAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AppEnvironment.GitHubApiUrl}/repos/{AppEnvironment.RepoSlug}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await AppEnvironment.Http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return;
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            var latest = ModVersion.Parse(tag);
            var current = ModVersion.Parse(AppEnvironment.Version);
            if (latest is not null && current is not null && latest > current && url is not null && WebHostUrlOk(url))
                Update(() => _appUpdate = (latest.ToString().TrimStart((char)118, (char)86), url));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
        }
    }

    private static bool WebHostUrlOk(string url) => url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain-text details for a bug report. Contains no keys, and user folders are masked.</summary>
    public string Diagnostics()
    {
        var game = _game;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{AppEnvironment.AppName} {AppEnvironment.Version}");
        sb.AppendLine("Windows: " + Environment.OSVersion.VersionString);
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
            return ("Close Stardew Valley first. It keeps its mod files open while it runs.", null);

        ModInfo? mod;
        List<ModInfo> all;
        lock (_gate)
        {
            all = _mods.ToList();
            mod = all.FirstOrDefault(m => m.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase) && m.Disabled == enable)
                ?? all.FirstOrDefault(m => m.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
        }
        if (mod is null || mod.Bundled)
            return ("That mod was not found, or it is part of SMAPI.", null);

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
            return ("This mod is switched off by a parent folder whose name starts with a dot. Rename that folder in File Explorer.", null);
        }

        var target = Path.Combine(parent, enable ? name.TrimStart('.') : "." + name);
        if (target.Length == parent.Length + 1 || Directory.Exists(target) || File.Exists(target))
            return ("A folder with the new name already exists, so nothing was changed.", null);

        try
        {
            Directory.Move(folder, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("The folder could not be renamed (" + ex.Message + "). Close anything that is using it and try again.", null);
        }

        Log.Write($"{(enable ? "Enabled" : "Disabled")} {mod.Name}: {name} -> {Path.GetFileName(target)}");
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
        if (game is null)
            return "No game folder is set.";
        if (GameLocator.IsGameRunning(fresh: true))
            return "The game is already running.";
        var exe = Path.Combine(game.GamePath, "StardewModdingAPI.exe");
        if (!File.Exists(exe))
            return "SMAPI is not installed in this game folder. Get it from smapi.io first.";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = game.GamePath, UseShellExecute = true })?.Dispose();
        return null;
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

    public object Snapshot()
    {
        lock (_gate)
        {
            var game = _game;
            var running = game is not null && GameLocator.IsGameRunning();
            var nxmStatus = NxmRegistration.Status();
            return new
            {
                version = _version,
                phase = _phase,
                status = _status,
                error = _fatal,
                summary = _lastSummary,
                app = new
                {
                    name = AppEnvironment.AppName,
                    version = AppEnvironment.Version,
                    repo = AppEnvironment.RepoUrl,
                    update = _appUpdate is null ? null : new { version = _appUpdate.Value.Version, url = _appUpdate.Value.Url },
                },
                game = game is null ? null : new
                {
                    path = game.GamePath,
                    mods = game.ModsPath,
                    store = game.Store,
                    gameVersion = game.GameVersion,
                    smapi = game.SmapiVersion,
                    running,
                    canLaunch = game.HasSmapi,
                },
                candidates = _candidates.Select(c => new { path = c.GamePath, store = c.Store, smapi = c.SmapiVersion }),
                welcomed = Settings.Data.Welcomed,
                smapiUpdate = _smapiUpdate is null ? null : new { version = _smapiUpdate.Value.Version, url = _smapiUpdate.Value.Url },
                lastChecked = _lastChecked,
                nexus = new
                {
                    connected = _nexusUser is not null,
                    user = _nexusUser?.Name,
                    premium = _nexusUser?.IsPremium ?? false,
                    message = _nexusMessage,
                    nxm = Settings.Data.NxmRegistered && nxmStatus.Ours,
                    otherHandler = nxmStatus.Other,
                },
                settings = new
                {
                    policy = Settings.Data.Policy,
                    appWindow = Settings.Data.AppWindow,
                    theme = Settings.Data.Theme,
                    tourSeen = Settings.Data.TourSeen,
                    downloads = Settings.DownloadsPath,
                    watching = _watcher.Folder is not null,
                    backupsPerMod = Settings.Data.BackupsPerMod,
                    ignored = Settings.Data.Ignored,
                },
                items = _items.Select(i => new
                {
                    id = i.Id,
                    name = i.Name,
                    installed = i.Installed,
                    target = i.Target,
                    parts = i.PartCount,
                    isNew = i.IsNew,
                    why = i.Why,
                    status = i.Status,
                    message = i.Message,
                    progress = i.Progress,
                    selected = i.Selected,
                    chosen = i.Chosen,
                    alternativesLoaded = i.AlternativesLoaded,
                    changelog = i.Changelog,
                    pageUrl = i.ModPageUrl,
                    details = i.Details,
                    historyId = i.HistoryId,
                    offers = i.Offers.Select(o => new
                    {
                        source = o.Kind,
                        label = o.Label,
                        mode = o.Mode,
                        version = o.Version,
                        usable = o.Usable,
                        note = o.Note,
                    }),
                }),
                rows = _rows,
                problems = _problems,
                found = _found.Select(f => new { id = f.Id, file = f.FileName, origin = f.Origin, lines = f.Lines, busy = f.Busy, error = f.Error }),
                history = History.All().Take(40).Select(h => new
                {
                    id = h.Id,
                    when = h.When,
                    name = h.Name,
                    from = h.FromVersion,
                    to = h.ToVersion,
                    source = h.Source,
                    undone = h.Undone,
                    canUndo = !h.Undone && (h.BackupFile is not null || h.Folders.All(f => f.WasNew)),
                }),
                gameLog = _gameLog is null ? null : new
                {
                    writtenAt = _gameLog.WrittenAt,
                    loadedMods = _gameLog.LoadedMods,
                    loadedPacks = _gameLog.LoadedContentPacks,
                    skipped = _gameLog.Skipped.Select(s => new { name = s.Name, version = s.Version, reason = s.Reason, kind = s.Kind, hint = s.Hint }),
                    errors = _gameLog.Errors,
                    dismissed = _gameLog.WrittenAt.Ticks <= Settings.Data.LogDismissedTicks,
                },
                backupBytes = History.BackupBytes(),
            };
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        _watcher.Dispose();
    }
}
