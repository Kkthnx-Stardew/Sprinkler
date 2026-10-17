using System.Text.RegularExpressions;

namespace Sprinkler.Core;

/// <summary>What a collection holds, compared with what is installed, for the person to read before adding it.</summary>
internal sealed record CollectionPreview(string Name, string? Curator, string? Summary, int Revision, int Required, int Optional, int RequiredInstalled, int OptionalInstalled);

// Nexus Collections. A collection becomes a profile that remembers the curator's list. The mods that are missing show up
// under Updates as installs and use the same update flow as everything else, so a free account clicks per mod and a
// Premium account is fully automatic. As each mod arrives it joins the profile.
internal sealed partial class UpdateService
{
    private static long? NexusIdOf(UpdateKey key) => key.Site == SourceKind.Nexus && long.TryParse(key.Id, out var id) ? id : null;

    /// <summary>The Nexus mod IDs of everything installed, found through the update keys in each manifest.</summary>
    private static HashSet<long> InstalledNexusIds(IEnumerable<ModInfo> mods) =>
        mods.Where(m => !m.Bundled).SelectMany(m => m.ParsedKeys.Select(NexusIdOf)).OfType<long>().ToHashSet();

    public async Task<CollectionRevision> LookUpCollectionAsync(string text)
    {
        var parsed = NexusCollections.Parse(text)
            ?? throw new CollectionException(Loc.T("core.col_not_collection"));
        return await NexusCollections.FetchAsync(parsed.Slug, parsed.Revision, CancellationToken.None);
    }

    public CollectionPreview PreviewCollection(CollectionRevision r)
    {
        HashSet<long> installed;
        lock (_gate)
            installed = InstalledNexusIds(_mods);
        var required = r.Mods.Where(m => !m.Optional).ToList();
        var optional = r.Mods.Where(m => m.Optional).ToList();
        return new CollectionPreview(r.Name, r.Curator, r.Summary, r.Revision, required.Count, optional.Count,
            required.Count(m => installed.Contains(m.ModId)), optional.Count(m => installed.Contains(m.ModId)));
    }

    /// <summary>
    /// Saves a collection as a profile. It does not switch to it and does not change any mod: it records the curator's list and,
    /// for mods that are not installed yet, offers them under Updates as installs.
    /// </summary>
    public ProfileResult AddCollection(CollectionRevision r, bool includeOptional)
    {
        try
        {
            var selected = r.Mods.Where(m => includeOptional || !m.Optional).ToList();
            if (selected.Count == 0)
                return ProfileResult.Fail(Loc.T("core.col_empty"));

            List<ModInfo> mods;
            lock (_gate)
                mods = _mods.ToList();
            var installedIds = InstalledNexusIds(mods);
            var wantedNexus = selected.Select(m => m.ModId).ToHashSet();
            var enabled = mods.Where(m => !m.Bundled && m.ParsedKeys.Select(NexusIdOf).OfType<long>().Any(wantedNexus.Contains))
                .Select(m => m.UniqueId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var toInstall = selected.Count(m => !installedIds.Contains(m.ModId));

            var baseName = r.Name.Length > ProfileStore.MaxNameLength - 12 ? r.Name[..(ProfileStore.MaxNameLength - 12)].TrimEnd() : r.Name;
            var name = baseName;
            for (var n = 2; Profiles.Find(name) is not null; n++)
                name = $"{baseName} ({n})";

            var profile = new Profile
            {
                Name = name,
                Enabled = enabled,
                PendingInstall = toInstall > 0,
                Collection = new ProfileCollection { Slug = r.Slug, Revision = r.Revision, Name = r.Name, Curator = r.Curator, Mods = selected },
            };
            Profiles.Add(profile, makeActive: false);
            Update(() => { });
            _ = CheckAsync();

            var message = Loc.T("core.col_added", profile.Name, Fmt(selected.Count), selected.Count - toInstall);
            if (toInstall > 0)
                message += " " + Loc.T("core.col_added_more", toInstall);
            return ProfileResult.Ok(message);
        }
        catch (ProfileException ex)
        {
            return ProfileResult.Fail(ex.Message);
        }
    }

    /// <summary>Stops offering a collection's missing mods. The profile and what is installed stay as they are.</summary>
    public ProfileResult StopInstalling(string name)
    {
        try
        {
            Profiles.Update(name, p => p.PendingInstall = false);
            Update(() => { });
            _ = CheckAsync();
            return ProfileResult.Ok(Loc.T("core.col_dismissed"));
        }
        catch (ProfileException ex)
        {
            return ProfileResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// After a scan: mods of a collection that have arrived join its profile, and a collection with nothing left to install stops
    /// being offered.
    /// </summary>
    internal void SyncCollectionProfiles(List<ModInfo> mods)
    {
        var installedNexus = InstalledNexusIds(mods);
        foreach (var p in Profiles.All().Where(p => p.Collection is not null))
        {
            var members = p.Collection!.Mods.Select(m => m.ModId).ToHashSet();
            var present = mods
                .Where(m => !m.Bundled && m.ParsedKeys.Select(NexusIdOf).OfType<long>().Any(members.Contains))
                .Select(m => m.UniqueId)
                .Where(id => !p.Enabled.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var stillMissing = p.Collection.Mods.Any(m => !installedNexus.Contains(m.ModId));
            if (present.Count == 0 && !(p.PendingInstall && !stillMissing))
                continue;
            Profiles.Update(p.Name, x =>
            {
                x.Enabled.AddRange(present);
                if (!stillMissing)
                    x.PendingInstall = false;
            });
        }
    }

    /// <summary>Install items for the mods of collections that are still being installed and are not installed yet.</summary>
    private List<UpdateItem> CollectionItems(HashSet<long> installedNexus, HashSet<long> alreadyOffered)
    {
        var items = new List<UpdateItem>();
        foreach (var p in Profiles.All().Where(p => p.PendingInstall && p.Collection is not null))
        {
            foreach (var m in p.Collection!.Mods)
            {
                if (installedNexus.Contains(m.ModId) || !alreadyOffered.Add(m.ModId))
                    continue;
                items.Add(new UpdateItem
                {
                    Id = "new:nexus:" + m.ModId,
                    Name = m.Name,
                    Target = m.Version ?? "",
                    IsNew = true,
                    AcceptAnyMod = true,
                    Why = p.Collection.Curator is null ? Loc.T("core.col_why", p.Collection.Name) : Loc.T("core.col_why_by", p.Collection.Name, p.Collection.Curator),
                    NexusId = m.ModId,
                    ModPageUrl = NexusPage(m.ModId),
                });
            }
        }
        return items;
    }

    /// <summary>
    /// Nexus names downloads like "Content Patcher-1915-2-9-1-1752000000.zip": the mod's ID is the first number of the trailing run.
    /// Used to tell which waiting install a file in the Downloads folder belongs to when its SMAPI ID is not known yet.
    /// </summary>
    internal static long? NexusFileModId(string path)
    {
        // the browser adds " (1)" when the same file is downloaded again
        var name = Regex.Replace(Path.GetFileNameWithoutExtension(path), @"\s*\(\d+\)$", "");
        var m = Regex.Match(name, @"(?:-\d+){2,}$");
        if (!m.Success)
            return null;
        var first = m.Value.TrimStart('-').Split('-')[0];
        return long.TryParse(first, out var id) ? id : null;
    }
}
