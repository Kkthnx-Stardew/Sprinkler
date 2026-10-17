using Sprinkler.Core;

namespace Sprinkler.ViewModels;

// Read-only values the screens bind to, worked out from the latest snapshot.
internal sealed partial class MainViewModel
{
    private Phase CurrentPhase => Snap?.Phase ?? Phase.Starting;
    private GameInfo? Game => Snap?.Game;

    public string AppName => AppEnvironment.AppName;
    public string AppVersion => "v" + AppEnvironment.Version;
    public string RepoUrl => AppEnvironment.RepoUrl;
    public string OsName => Platform.SmapiName;
    public string DataDir => AppEnvironment.DataDir;
    public string Tagline => Content.Tagline;

    // ---- header
    public bool HasGame => Game is not null;
    public string GameChip => Game is { } g ? $"{g.Store} · game {g.GameVersion ?? "?"}" : "";
    public string GamePath => Game?.Path ?? "Not set";
    public string ModsPath => Game?.Mods ?? "Not set";
    public bool HasSmapi => Game?.Smapi is not null;
    public bool NoSmapi => Game is not null && Game.Smapi is null;
    public string SmapiChip => Game?.Smapi is { } v ? "SMAPI " + v : "SMAPI not found";
    public bool GameRunning => Game?.Running == true;
    public bool CanPlay => Game is { CanLaunch: true, Running: false };
    public bool NexusConnected => Snap?.Nexus.Connected == true;
    public bool NexusNotConnected => Snap is not null && !Snap.Nexus.Connected;
    public string NexusChip => Snap?.Nexus is { Connected: true } n ? n.User + (n.Premium ? " · Premium" : "") : "";
    public bool HasAppUpdate => Snap?.App.Update is not null;
    public string AppUpdateText => Snap?.App.Update is { } u ? "Version " + u.Version + " is out" : "";
    public bool IsBusy => CurrentPhase is Phase.Scanning or Phase.Checking or Phase.Applying or Phase.Starting;
    public bool CanCheck => HasGame && !IsBusy;

    // ---- banners
    public bool HasError => !string.IsNullOrEmpty(Snap?.Error);
    public string ErrorText => Snap?.Error ?? "";

    // ---- phases
    public bool IsStarting => CurrentPhase == Phase.Starting;
    public bool IsChoosing => CurrentPhase == Phase.ChooseGame;
    public bool IsWorking => CurrentPhase is Phase.Scanning or Phase.Checking;
    public bool IsIdle => CurrentPhase == Phase.Idle;
    public bool IsReview => CurrentPhase == Phase.Review;
    public bool IsApplying => CurrentPhase == Phase.Applying;
    public bool IsFinished => CurrentPhase == Phase.Finished;
    public bool HasSteps => Steps.Count > 0;
    public string StatusText => string.IsNullOrEmpty(Snap?.Status) ? "Working..." : Snap!.Status;
    public string FinishedText => (Snap?.Summary ?? "") + " You can undo any change from History.";

    // ---- game choice
    public bool HasCandidates => Candidates.Count > 0;
    public string ChooseTitle => HasCandidates ? "Which Stardew Valley is yours?" : "Where is Stardew Valley installed?";
    public string ChooseText => HasCandidates
        ? "I found these installs. Pick the one whose mods you want to update."
        : "I could not find it by myself. Paste the folder that contains \"Stardew Valley.dll\".";
    public string FindGameHelp => Content.FindGameHelp(OsName);
    public string GamePathExample => Content.GamePathExample(OsName);

    // ---- welcome, overview and health
    private bool PastScan => CurrentPhase is Phase.Review or Phase.Applying or Phase.Finished;
    public bool ShowWelcome => Snap is { Welcomed: false, Game: not null } && PastScan;
    public string SmapiFoundText => Game?.Smapi ?? "Not found. Install it from smapi.io, or mods will not load.";
    public bool HasSmapiUpdate => Snap?.SmapiUpdate is not null && Game is not null && PastScan;
    public string SmapiUpdateText => Snap?.SmapiUpdate is { } u ? $"SMAPI {u.Version} is out (you have {Game?.Smapi}). SMAPI has its own installer, so this app leaves it alone." : "";
    public string? SmapiUpdateUrl => Snap?.SmapiUpdate?.Url;
    public int RowCount => Snap?.Rows.Count ?? 0;
    private int ReadyCount => Items.Count;
    private int NewCount => Items.Count(i => i.IsNew);
    private int UpdateOnlyCount => ReadyCount - NewCount;

    public string HeroTitle => ReadyCount == 0
        ? "Everything is up to date"
        : string.Join(" and ", new[]
            {
                UpdateOnlyCount > 0 ? Fmt.Plural(UpdateOnlyCount, "update", "updates") : null,
                NewCount > 0 ? Fmt.Plural(NewCount, "missing mod", "missing mods") : null,
            }.Where(x => x is not null)) + " ready";

    public string HeroText => ReadyCount == 0
        ? $"{Fmt.Plural(RowCount, "mod", "mods")} checked{(Snap?.LastChecked is { } t ? " " + Fmt.Ago(t) : "")}. Nothing needs your attention."
        : "Review them below. Each mod is backed up first, and you can undo any change from History.";

    public bool HeroPlay => ReadyCount == 0 && CanPlay;
    public int CountAll => RowCount;
    public int CountUpdates => ReadyCount;
    public int CountUpToDate => Snap?.Rows.Count(r => r.State == RowState.UpToDate) ?? 0;
    public int CountUnchecked => Snap?.Rows.Count(r => r.State is RowState.NoInfo or RowState.Error) ?? 0;

    public bool HealthVisible => IsReview && Snap?.GameLog is { Dismissed: false } g && (g.Skipped.Count > 0 || g.Errors.Count > 0);
    public string HealthTitle => SkippedMods.Count switch
    {
        0 => "Some mods reported errors last time you played",
        1 => "1 mod did not load last time you played",
        var n => n + " mods did not load last time you played",
    };
    public string HealthSubtitle => Snap?.GameLog is { } g
        ? $"From SMAPI's own log of your last game session ({Fmt.Time(g.WrittenAt)}). {g.LoadedMods} mods and {g.LoadedPacks} content packs loaded."
        : "";
    public bool HasSkippedMods => SkippedMods.Count > 0;
    public bool HasLogErrors => LogErrors.Count > 0;

    public bool ShowTickPanel => IsReview && Items.Count > 0;
    public bool HasProblems => Problems.Count > 0;

    // ---- the action bar
    public int SelectedCount => Items.Count(i => i.IsReady && i.Selected && i.CanSelect);
    public bool ShowActions => IsReview && Items.Count > 0;
    public bool CanUpdate => SelectedCount > 0 && !GameRunning;
    public string UpdateButtonText => SelectedCount == 0 ? "Nothing selected" : "Update " + Fmt.Plural(SelectedCount, "mod", "mods");

    // ---- All mods
    public string ModsTitle
    {
        get
        {
            var on = Snap?.Rows.Count(r => r.State is not (RowState.Disabled or RowState.Bundled)) ?? 0;
            return $"{Fmt.Plural(RowCount, "mod", "mods")}, {on} switched on";
        }
    }
    public bool HasMods => RowCount > 0;
    public bool NoMods => RowCount == 0;
    public bool NoModMatches => RowCount > 0 && Mods.Count == 0;

    // ---- History
    public string BackupsText => Snap is null ? "" : $"Backups use {Fmt.Bytes(Snap.BackupBytes)}. The app keeps the newest {Snap.Settings.BackupsPerMod} per mod and removes older ones.";
    public bool HasHistory => History.Count > 0;
    public bool NoHistory => History.Count == 0;

    // ---- Help
    public IReadOnlyList<QA> FaqList => Content.Faq(DataDir);
    public IReadOnlyList<QA> FixList => Content.Fixes;
    public string AboutText => $"Sprinkler {AppEnvironment.Version}. Free and open source (MIT). Update checks come from the SMAPI update service; downloads come from Nexus Mods, CurseForge and GitHub.";
}
