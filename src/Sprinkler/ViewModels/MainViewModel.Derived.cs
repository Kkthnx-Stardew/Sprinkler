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
    public string GameChip => Game is { } g ? Loc.T("vm.game_chip", g.Store, g.GameVersion ?? "?") : "";
    public string GamePath => Game?.Path ?? Loc.T("vm.not_set");
    public string ModsPath => Game?.Mods ?? Loc.T("vm.not_set");
    public bool HasSmapi => Game?.Smapi is not null;
    public bool NoSmapi => Game is not null && Game.Smapi is null;
    public string SmapiChip => Game?.Smapi is { } v ? Loc.T("vm.smapi_chip", v) : Loc.T("vm.smapi_not_found");
    public bool GameRunning => Game?.Running == true;
    public bool CanPlay => Game is { CanLaunch: true, Running: false };
    public bool NexusConnected => Snap?.Nexus.Connected == true;
    public bool NexusNotConnected => Snap is not null && !Snap.Nexus.Connected;
    public string NexusChip => Snap?.Nexus is { Connected: true } n ? (n.Premium ? Loc.T("vm.nexus_chip_premium", n.User ?? "") : n.User ?? "") : "";
    public bool HasAppUpdate => Snap?.App.Update is not null;
    public string AppUpdateText => Snap?.App.Update is { } u ? Loc.T("vm.app_update", u.Version) : "";
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
    public string StatusText => string.IsNullOrEmpty(Snap?.Status) ? Loc.T("vm.working") : Snap!.Status;
    public string FinishedText => Loc.T("vm.finished_text", Snap?.Summary ?? "");

    // ---- game choice
    public bool HasCandidates => Candidates.Count > 0;
    public string ChooseTitle => Loc.T(HasCandidates ? "vm.choose_title_found" : "vm.choose_title_none");
    public string ChooseText => Loc.T(HasCandidates ? "vm.choose_text_found" : "vm.choose_text_none");
    public string FindGameHelp => Content.FindGameHelp(OsName);
    public string GamePathExample => Content.GamePathExample(OsName);

    // ---- welcome, overview and health
    private bool PastScan => CurrentPhase is Phase.Review or Phase.Applying or Phase.Finished;
    public bool ShowWelcome => Snap is { Welcomed: false, Game: not null } && PastScan;
    public string SmapiFoundText => Game?.Smapi ?? Loc.T("vm.smapi_missing");
    public bool HasSmapiUpdate => Snap?.SmapiUpdate is not null && Game is not null && PastScan;
    public string SmapiUpdateText => Snap?.SmapiUpdate is { } u ? Loc.T("vm.smapi_update", u.Version, Game?.Smapi ?? "?") : "";
    public string? SmapiUpdateUrl => Snap?.SmapiUpdate?.Url;
    public int RowCount => Snap?.Rows.Count ?? 0;
    private int ReadyCount => Items.Count;
    private int NewCount => Items.Count(i => i.IsNew);
    private int UpdateOnlyCount => ReadyCount - NewCount;

    public string HeroTitle
    {
        get
        {
            if (ReadyCount == 0)
                return Loc.T("vm.hero_current");
            if (UpdateOnlyCount > 0 && NewCount > 0)
                return Loc.T("vm.hero_both", Loc.Count("n.update", UpdateOnlyCount), Loc.Count("n.missing_mod", NewCount));
            return UpdateOnlyCount > 0
                ? Loc.T("vm.hero_ready", Loc.Count("n.update", UpdateOnlyCount))
                : Loc.T("vm.hero_ready", Loc.Count("n.missing_mod", NewCount));
        }
    }

    public string HeroText
    {
        get
        {
            if (ReadyCount > 0)
                return Loc.T("vm.hero_text_review");
            return Snap?.LastChecked is { } t
                ? Loc.T("vm.hero_text_checked_ago", Loc.Count("n.mod", RowCount), Fmt.Ago(t))
                : Loc.T("vm.hero_text_checked", Loc.Count("n.mod", RowCount));
        }
    }

    public bool HeroPlay => ReadyCount == 0 && CanPlay;
    public int CountAll => RowCount;
    public int CountUpdates => ReadyCount;
    public int CountUpToDate => Snap?.Rows.Count(r => r.State == RowState.UpToDate) ?? 0;
    public int CountUnchecked => Snap?.Rows.Count(r => r.State is RowState.NoInfo or RowState.Error) ?? 0;

    public bool HealthVisible => IsReview && Snap?.GameLog is { Dismissed: false } g && (g.Skipped.Count > 0 || g.Errors.Count > 0);
    public string HealthTitle => SkippedMods.Count == 0 ? Loc.T("vm.health_errors") : Loc.Count("vm.health_skipped", SkippedMods.Count);
    public string HealthSubtitle => Snap?.GameLog is { } g
        ? Loc.T("vm.health_sub", Fmt.Time(g.WrittenAt), g.LoadedMods, g.LoadedPacks)
        : "";
    public bool HasSkippedMods => SkippedMods.Count > 0;
    public bool HasLogErrors => LogErrors.Count > 0;

    public bool ShowTickPanel => IsReview && Items.Count > 0;
    public bool HasProblems => Problems.Count > 0;

    // ---- the action bar
    public int SelectedCount => Items.Count(i => i.IsReady && i.Selected && i.CanSelect);
    public bool ShowActions => IsReview && Items.Count > 0;
    public bool CanUpdate => SelectedCount > 0 && !GameRunning;
    public string UpdateButtonText => SelectedCount == 0 ? Loc.T("vm.update_none") : Loc.T("vm.update_n", Loc.Count("n.mod", SelectedCount));

    // ---- All mods
    public string ModsTitle
    {
        get
        {
            var on = Snap?.Rows.Count(r => r.State is not (RowState.Disabled or RowState.Bundled)) ?? 0;
            return Loc.T("vm.mods_title", Loc.Count("n.mod", RowCount), on);
        }
    }
    public bool HasMods => RowCount > 0;
    public bool NoMods => RowCount == 0;
    public bool NoModMatches => RowCount > 0 && Mods.Count == 0;

    // ---- History
    public string BackupsText => Snap is null ? "" : Loc.T("vm.backups_text", Fmt.Bytes(Snap.BackupBytes), Snap.Settings.BackupsPerMod);
    public bool HasHistory => History.Count > 0;
    public bool NoHistory => History.Count == 0;

    // ---- Help
    public IReadOnlyList<QA> FaqList => Content.Faq(DataDir);
    public IReadOnlyList<QA> FixList => Content.Fixes;
    public string AboutText => Loc.T("vm.about", AppEnvironment.Version);
}
