using System.Text.RegularExpressions;

namespace Sprinkler.Core;

internal enum SkipKind
{
    MissingMods,
    NeedsNewerMods,
    NeedsNewerGameOrSmapi,
    Incompatible,
    Duplicate,
    BrokenInstall,
    FailedToLoad,
    Other,
}

internal sealed record SkippedMod(string Name, string? Version, string Reason, SkipKind Kind, string Hint);

internal sealed record LogErrorSource(string Source, int Count);

internal sealed record GameLog(DateTime WrittenAt, int LoadedMods, int LoadedContentPacks, List<SkippedMod> Skipped, List<LogErrorSource> Errors);

/// <summary>
/// Reads the log SMAPI wrote during the last game session and picks out the mods it skipped and why.
/// The wording comes from SMAPI's own source (ModResolver and SCore), so it is matched, not guessed.
/// </summary>
internal static partial class SmapiLog
{
    public static string DefaultPath => Environment.GetEnvironmentVariable("SPRINKLER_LOG") is { Length: > 0 } custom ? custom : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewValley", "ErrorLogs", "SMAPI-latest.txt");

    public static GameLog? Read(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            if (info.Length > 40_000_000)
                return null;

            // SMAPI keeps the file open while the game runs, so share it for reading
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return Parse(reader.ReadToEnd().Split('\n'), info.LastWriteTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static GameLog Parse(IEnumerable<string> lines, DateTime writtenAt)
    {
        var skipped = new List<SkippedMod>();
        var errors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int loadedMods = 0, loadedPacks = 0;
        var inSkipped = false;

        foreach (var raw in lines)
        {
            var m = LineRegex().Match(raw.TrimEnd('\r'));
            if (!m.Success)
                continue;

            var level = m.Groups["lvl"].Value;
            var source = m.Groups["src"].Value.Trim();
            var msg = m.Groups["msg"].Value;

            if (source == "SMAPI")
            {
                if (msg.TrimEnd() == "   Skipped mods" && level == "ERROR")
                {
                    inSkipped = true;
                    continue;
                }

                var loaded = LoadedRegex().Match(msg);
                if (loaded.Success)
                {
                    var n = int.Parse(loaded.Groups["n"].Value);
                    if (loaded.Groups["what"].Value == "mods") loadedMods = n; else loadedPacks = n;
                }

                if (inSkipped)
                {
                    var item = SkippedRegex().Match(msg);
                    if (item.Success)
                    {
                        skipped.Add(Classify(item.Groups["name"].Value, item.Groups["reason"].Value));
                        continue;
                    }
                    var leading = msg.Length - msg.TrimStart().Length;
                    if (msg.Trim().Length > 0 && leading <= 4 && !msg.TrimStart().StartsWith("--"))
                        inSkipped = false;
                    continue;
                }
            }

            if (level == "ERROR" && source != "SMAPI" && source != "game")
                errors[source] = errors.GetValueOrDefault(source) + 1;
        }

        return new GameLog(writtenAt, loadedMods, loadedPacks, skipped,
            errors.OrderByDescending(e => e.Value).Take(8).Select(e => new LogErrorSource(e.Key, e.Value)).ToList());
    }

    private static SkippedMod Classify(string nameAndVersion, string reason)
    {
        var name = nameAndVersion.Trim();
        string? version = null;
        var parts = name.Split(' ');
        if (parts.Length > 1 && ModVersion.TryParse(parts[^1], out _))
        {
            version = parts[^1];
            name = string.Join(' ', parts[..^1]);
        }

        if (reason.StartsWith("it requires mods which aren't installed", StringComparison.Ordinal))
            return new SkippedMod(name, version, reason, SkipKind.MissingMods, "Install the mods named here. Check again and the app offers them as installs.");
        if (reason.StartsWith("it needs newer versions of some mods", StringComparison.Ordinal))
            return new SkippedMod(name, version, reason, SkipKind.NeedsNewerMods, "Update the mods named here, then check again.");
        if (reason.StartsWith("it needs SMAPI", StringComparison.Ordinal) || reason.StartsWith("it needs Stardew Valley", StringComparison.Ordinal))
            return new SkippedMod(name, version, reason, SkipKind.NeedsNewerGameOrSmapi, "Update SMAPI or the game to the latest version.");
        if (reason.StartsWith("it's no longer compatible", StringComparison.Ordinal) || reason.StartsWith("it's obsolete", StringComparison.Ordinal))
            return new SkippedMod(name, version, reason, SkipKind.Incompatible, "Look for a newer version of this mod, or remove it.");
        if (reason.StartsWith("you have multiple copies", StringComparison.Ordinal))
            return new SkippedMod(name, version, reason, SkipKind.Duplicate, "Delete the extra copy in your Mods folder.");
        if (reason.Contains("doesn't exist", StringComparison.Ordinal) || reason.StartsWith("its manifest", StringComparison.Ordinal))
            return new SkippedMod(name, version, reason, SkipKind.BrokenInstall, "The mod's files are incomplete. Reinstall it.");
        if (reason.StartsWith("its DLL", StringComparison.Ordinal) || reason.StartsWith("it needs to be updated", StringComparison.Ordinal)
            || reason.StartsWith("initialization failed", StringComparison.Ordinal))
            return new SkippedMod(name, version, reason, SkipKind.FailedToLoad, "Update or reinstall this mod.");
        return new SkippedMod(name, version, reason, SkipKind.Other, "Read the reason above, or ask the mod's author.");
    }

    [GeneratedRegex(@"^\[(?<time>\d\d:\d\d:\d\d) (?<lvl>[A-Z]+)\s+(?<src>[^\]]+)\] (?<msg>.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^ {6}- (?<name>.+?) because (?<reason>.+)$")]
    private static partial Regex SkippedRegex();

    [GeneratedRegex(@"^Loaded (?<n>\d+) (?<what>mods|content packs):")]
    private static partial Regex LoadedRegex();
}
