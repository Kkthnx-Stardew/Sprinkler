using System.IO.Compression;

namespace StardewModUpdater.Core;

internal class InstallException(string message) : Exception(message);

/// <summary>The file is fine but is not the one wanted (wrong mod, or not newer). Another source may still work.</summary>
internal sealed class WrongFileException(string message) : InstallException(message);

/// <summary>One mod folder the installer will write.</summary>
internal sealed class PlannedFolder
{
    public required ArchiveMod Source { get; init; }
    public required string TargetPath { get; init; }
    public ModInfo? Replaces { get; init; }
    public bool IsNew => Replaces is null;
    public List<string> Warnings { get; } = new();
}

internal sealed class InstallPlan
{
    public required string ArchivePath { get; init; }
    public List<PlannedFolder> Folders { get; } = new();
}

internal sealed record InstallResult(string HistoryId, List<string> Messages);

/// <summary>
/// Applies an archive to the Mods folder. Every replaced folder is zipped to the backup folder first,
/// the user's config.json is kept, old code files that the new version no longer ships are removed,
/// and any failure restores the backup before the error is reported.
/// </summary>
internal sealed class Installer(HistoryStore history, SettingsStore settings)
{
    private static readonly string[] StaleCodeExtensions = { ".dll", ".pdb", ".exe", ".deps.json" };

    public static InstallPlan Plan(string archivePath, ArchiveFile archive, string modsPath, IReadOnlyList<ModInfo> installed)
    {
        var mods = ArchiveInspector.FindMods(archive);
        if (mods.Count == 0)
            throw new WrongFileException("This archive has no manifest.json, so it is not a SMAPI mod or content pack.");

        var plan = new InstallPlan { ArchivePath = archivePath };
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            var existing = installed
                .Where(m => m.UniqueId.Equals(mod.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Disabled)
                .FirstOrDefault();

            string target;
            if (existing is not null)
            {
                target = existing.FolderPath;
            }
            else
            {
                var baseName = mod.RootPath.Length > 0 ? mod.RootPath.Split('/').Last() : Sanitize(mod.Manifest.Name);
                if (baseName.StartsWith('.'))
                    baseName = baseName.TrimStart('.');
                var name = baseName;
                var n = 2;
                while (Directory.Exists(Path.Combine(modsPath, name)) || !usedNames.Add(name))
                    name = $"{baseName} ({n++})";
                target = Path.Combine(modsPath, name);
            }

            var planned = new PlannedFolder { Source = mod, TargetPath = target, Replaces = existing };
            if (existing is not null)
            {
                var newV = ModVersion.Parse(mod.Manifest.Version);
                var oldV = ModVersion.Parse(existing.Version);
                if (newV is not null && oldV is not null && newV < oldV)
                    planned.Warnings.Add($"{existing.Name}: the archive has {mod.Manifest.Version}, which is older than the installed {existing.Version}.");
                if (existing.Disabled)
                    planned.Warnings.Add($"{existing.Name} is switched off (its folder starts with a dot). It is updated but stays off.");
            }
            plan.Folders.Add(planned);
        }
        return plan;
    }

    public InstallResult Apply(InstallPlan plan, string modsPath, string sourceLabel)
    {
        if (GameLocator.IsGameRunning(fresh: true))
            throw new InstallException("Stardew Valley or SMAPI is running. Close it first so no file is in use.");

        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var stagingRoot = Path.Combine(AppEnvironment.StagingDir, id);
        var messages = new List<string>();
        var backupFile = id + ".zip";
        var backupPath = Path.Combine(history.BackupsDir, backupFile);
        var done = new List<PlannedFolder>();
        var backedUp = new List<PlannedFolder>();

        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(history.BackupsDir);

        try
        {
            using var archive = ArchiveFile.Open(plan.ArchivePath);

            var staged = new List<(PlannedFolder Folder, string Dir)>();
            for (var i = 0; i < plan.Folders.Count; i++)
            {
                var folder = plan.Folders[i];
                var dir = Path.Combine(stagingRoot, i.ToString());
                Extract(archive, folder.Source.RootPath, dir);
                staged.Add((folder, dir));
            }

            if (plan.Folders.Any(f => f.Replaces is not null))
            {
                using var zip = ZipFile.Open(backupPath, ZipArchiveMode.Create);
                foreach (var f in plan.Folders.Where(f => f.Replaces is not null))
                {
                    AddFolderToZip(zip, f.TargetPath, RelativeTo(modsPath, f.TargetPath));
                    backedUp.Add(f);
                }
            }

            foreach (var (folder, dir) in staged)
            {
                done.Add(folder);
                messages.AddRange(Merge(dir, folder.TargetPath, folder.Replaces is not null));

                var written = ManifestReader.ReadFile(Path.Combine(folder.TargetPath, "manifest.json"), folder.TargetPath);
                if (written is null
                    || !written.UniqueId.Equals(folder.Source.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase)
                    || written.Version != folder.Source.Manifest.Version)
                    throw new InstallException($"After copying, {folder.Source.Manifest.Name} did not read back correctly, so the change was undone.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallException or ArchiveException)
        {
            Log.Write("Install failed, rolling back: " + ex.Message);
            Rollback(done, backedUp, backupPath, modsPath);
            TryDeleteFile(backupPath);
            TryDeleteDirectory(stagingRoot);
            throw ex is InstallException or ArchiveException
                ? ex
                : new InstallException("A file could not be written (" + ex.Message + "). Close the game and any program showing the Mods folder, then try again. Nothing was changed.");
        }

        TryDeleteDirectory(stagingRoot);

        var first = plan.Folders[0];
        var entry = new HistoryEntry
        {
            Id = id,
            When = DateTime.Now,
            Name = plan.Folders.Count == 1 ? first.Source.Manifest.Name : $"{first.Source.Manifest.Name} (+{plan.Folders.Count - 1})",
            FromVersion = first.Replaces?.Version,
            ToVersion = first.Source.Manifest.Version,
            Source = sourceLabel,
            BackupFile = backedUp.Count > 0 ? backupFile : null,
            Folders = plan.Folders.Select(f => new HistoryFolder
            {
                RelativePath = RelativeTo(modsPath, f.TargetPath),
                UniqueId = f.Source.Manifest.UniqueId,
                WasNew = f.IsNew,
            }).ToList(),
        };
        history.Add(entry, settings.Data.BackupsPerMod);
        return new InstallResult(id, messages);
    }

    /// <summary>Puts a previous install back: restores the backup, or removes the folders it added.</summary>
    public void Undo(string historyId, string modsPath)
    {
        var entry = history.Find(historyId) ?? throw new InstallException("That change is not in the history.");
        if (entry.Undone)
            throw new InstallException("That change was already undone.");
        if (GameLocator.IsGameRunning(fresh: true))
            throw new InstallException("Stardew Valley or SMAPI is running. Close it first.");

        var backupPath = entry.BackupFile is null ? null : Path.Combine(history.BackupsDir, entry.BackupFile);
        var needsBackup = entry.Folders.Any(f => !f.WasNew);
        if (needsBackup && (backupPath is null || !File.Exists(backupPath)))
            throw new InstallException("The backup for this change was removed to save space, so it cannot be undone.");

        foreach (var f in entry.Folders)
        {
            var target = SafeJoin(modsPath, f.RelativePath);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }

        if (needsBackup)
        {
            using var zip = ZipFile.OpenRead(backupPath!);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/'))
                    continue;
                var dest = SafeJoin(modsPath, e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
        }
        history.MarkUndone(historyId);
    }

    private static List<string> Merge(string stagedDir, string target, bool replacing)
    {
        var messages = new List<string>();
        Directory.CreateDirectory(target);

        var newFiles = Directory.EnumerateFiles(stagedDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(stagedDir, f))
            .ToList();
        var newSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);

        var keptConfig = false;
        foreach (var rel in newFiles)
        {
            var dest = Path.Combine(target, rel);
            if (replacing && rel.Equals("config.json", StringComparison.OrdinalIgnoreCase) && File.Exists(dest))
            {
                keptConfig = true;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(stagedDir, rel), dest, overwrite: true);
        }
        if (keptConfig)
            messages.Add("Kept your existing config.json.");

        if (replacing)
        {
            var removed = 0;
            foreach (var old in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList())
            {
                var rel = Path.GetRelativePath(target, old);
                if (newSet.Contains(rel))
                    continue;
                if (StaleCodeExtensions.Any(x => rel.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                {
                    File.Delete(old);
                    removed++;
                }
            }
            if (removed > 0)
                messages.Add($"Removed {removed} old code file{(removed == 1 ? "" : "s")} the new version no longer uses.");
        }
        return messages;
    }

    private static void Extract(ArchiveFile archive, string rootPath, string destDir)
    {
        var prefix = rootPath.Length == 0 ? "" : rootPath + "/";
        var fullDest = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destDir);

        foreach (var entry in archive.Entries)
        {
            if (!entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var rel = entry.Path[prefix.Length..];
            if (rel.Length == 0)
                continue;

            var dest = Path.GetFullPath(Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase))
                throw new ArchiveException("The archive tried to write outside its folder (" + entry.Path + ").");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var input = archive.OpenEntry(entry.Path);
            using var output = File.Create(dest);
            input.CopyTo(output);
        }
    }

    private static void AddFolderToZip(ZipArchive zip, string folder, string relativeRoot)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var rel = relativeRoot + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, rel, CompressionLevel.Fastest);
        }
    }

    private void Rollback(List<PlannedFolder> done, List<PlannedFolder> backedUp, string backupPath, string modsPath)
    {
        try
        {
            foreach (var f in done)
            {
                if (f.IsNew && Directory.Exists(f.TargetPath))
                    Directory.Delete(f.TargetPath, recursive: true);
            }

            var restore = done.Where(f => !f.IsNew).ToList();
            if (restore.Count > 0 && File.Exists(backupPath))
            {
                using var zip = ZipFile.OpenRead(backupPath);
                foreach (var f in restore)
                {
                    var rel = RelativeTo(modsPath, f.TargetPath) + "/";
                    if (Directory.Exists(f.TargetPath))
                        Directory.Delete(f.TargetPath, recursive: true);
                    foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(rel, StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/')))
                    {
                        var dest = SafeJoin(modsPath, e.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        e.ExtractToFile(dest, overwrite: true);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Rollback problem: " + ex.Message + ". Backup kept at " + backupPath);
        }
    }

    private static string RelativeTo(string modsPath, string folder) =>
        Path.GetRelativePath(modsPath, folder).Replace('\\', '/');

    private static string SafeJoin(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InstallException("A stored path pointed outside the Mods folder, so it was not touched.");
        return full;
    }

    private static string Sanitize(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "NewMod" : clean;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
