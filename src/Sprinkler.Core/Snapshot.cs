namespace Sprinkler.Core;

// The state the window shows. UpdateService builds one of these under its lock, so the UI never reads half-changed data.
// The property names double as the JSON names (camel case) used by the --check command and the tests.

internal sealed record UpdateNotice(string Version, string Url);

internal sealed record ShortcutInfo(bool Installed);

internal sealed record AppInfo(string Name, string Version, string Repo, string Os, string DataDir, ShortcutInfo? Shortcut, UpdateNotice? Update);

internal sealed record GameInfo(string Path, string Mods, string Store, string? GameVersion, string? Smapi, bool Running, bool CanLaunch, string Os);

internal sealed record CandidateInfo(string Path, string Store, string? Smapi);

internal sealed record NexusInfo(bool Connected, string? User, bool Premium, string? Message, bool Nxm, bool OtherHandler, bool NxmSupported, string KeyStorage);

internal sealed record SettingsInfo(SourcePolicy Policy, bool AppWindow, string Theme, bool TourSeen, string Downloads, bool Watching, int BackupsPerMod, List<string> Ignored);

internal sealed record OfferInfo(SourceKind Source, string Label, DeliveryMode Mode, string? Version, bool Usable, string Note);

internal sealed record ItemInfo(
    string Id, string Name, string? Installed, string Target, int Parts, bool IsNew, string? Why, ItemStatus Status, string Message,
    int Progress, bool Selected, int Chosen, bool AlternativesLoaded, List<string> Changelog, string? PageUrl, List<string> Details,
    string? HistoryId, List<OfferInfo> Offers, string? ArtKey);

internal sealed record FoundInfo(string Id, string File, string Origin, List<string> Lines, bool Busy, string? Error);

internal sealed record HistoryInfo(string Id, DateTime When, string Name, string? From, string To, string Source, bool Undone, bool CanUndo, bool Removed);

internal sealed record ProfileSummary(string Name, int ModCount, int MissingCount, DateTime Updated, int ConfigCount, bool IsActive);

internal sealed record ProfilesInfo(string? Active, bool Modified, bool KeepConfigs, List<ProfileSummary> Profiles);

internal sealed record SkippedInfo(string Name, string? Version, string Reason, SkipKind Kind, string Hint);

internal sealed record GameLogInfo(DateTime WrittenAt, int LoadedMods, int LoadedPacks, List<SkippedInfo> Skipped, List<LogErrorSource> Errors, bool Dismissed);

internal sealed record StateSnapshot(
    long Version, Phase Phase, string Status, string? Error, string? Summary,
    AppInfo App, GameInfo? Game, List<CandidateInfo> Candidates, bool Welcomed, UpdateNotice? SmapiUpdate, DateTime? LastChecked,
    NexusInfo Nexus, SettingsInfo Settings, List<ItemInfo> Items, List<ModRow> Rows, List<Problem> Problems,
    List<FoundInfo> Found, List<HistoryInfo> History, GameLogInfo? GameLog, long BackupBytes,
    ProfilesInfo Profiles, Dictionary<string, string> ModNotes, List<long> Endorsed);
