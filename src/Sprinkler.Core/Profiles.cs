using System.Text;
using System.Text.Json;

namespace Sprinkler.Core;

/// <summary>A named set of mods that are switched on, optionally with each mod's config.json as it was.</summary>
internal sealed class Profile
{
    public string Name { get; set; } = "";
    public List<string> Enabled { get; set; } = new();

    /// <summary>Mod ID to config.json text. Only filled when the person asked profiles to keep their own settings.</summary>
    public Dictionary<string, string> Configs
    {
        get => _configs;
        // a file read back from disk arrives as a plain dictionary, so the case-insensitive lookup is rebuilt here
        set => _configs = new Dictionary<string, string>(value ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, string> _configs = new(StringComparer.OrdinalIgnoreCase);

    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;

    public Profile Copy(string name) => new()
    {
        Name = name,
        Enabled = Enabled.ToList(),
        Configs = new Dictionary<string, string>(Configs, StringComparer.OrdinalIgnoreCase),
    };
}

internal sealed class ProfilesData
{
    public string? Active { get; set; }
    public bool KeepConfigs { get; set; }
    public List<Profile> Profiles { get; set; } = new();
}

internal sealed class ProfileException(string message) : Exception(message);

/// <summary>The profiles file. Names are unique ignoring case, and every change is saved at once.</summary>
internal sealed class ProfileStore
{
    public const int MaxNameLength = 60;

    private readonly string _file;
    private readonly object _gate = new();
    private ProfilesData _data = new();

    public ProfileStore(string? file = null)
    {
        _file = file ?? Path.Combine(AppEnvironment.DataDir, "profiles.json");
        Load();
    }

    public string? ActiveName
    {
        get
        {
            lock (_gate)
                return _data.Active;
        }
    }

    public bool KeepConfigs
    {
        get
        {
            lock (_gate)
                return _data.KeepConfigs;
        }
        set
        {
            lock (_gate)
            {
                _data.KeepConfigs = value;
                Save();
            }
        }
    }

    public List<Profile> All()
    {
        lock (_gate)
            return _data.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Profile? Find(string? name)
    {
        if (name is null)
            return null;
        lock (_gate)
            return _data.Profiles.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public Profile? Active => Find(ActiveName);

    /// <summary>Checks a proposed name and returns the tidied version, or throws a sentence the person can read.</summary>
    public string CleanName(string? name, string? allowSameAs = null)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
            throw new ProfileException("Give the profile a name.");
        if (clean.Length > MaxNameLength)
            throw new ProfileException($"Keep the name under {MaxNameLength} characters.");
        if (clean.Any(char.IsControl))
            throw new ProfileException("The name cannot contain control characters.");
        if (Find(clean) is not null && !clean.Equals(allowSameAs, StringComparison.OrdinalIgnoreCase))
            throw new ProfileException($"A profile called \"{clean}\" already exists.");
        return clean;
    }

    public Profile Add(Profile profile, bool makeActive)
    {
        lock (_gate)
        {
            profile.Name = CleanName(profile.Name);
            profile.Created = profile.Updated = DateTime.Now;
            _data.Profiles.Add(profile);
            if (makeActive)
                _data.Active = profile.Name;
            Save();
            return profile;
        }
    }

    /// <summary>Replaces a profile's contents in place (same name), keeping its creation time.</summary>
    public void Update(string name, Action<Profile> change)
    {
        lock (_gate)
        {
            var p = Find(name) ?? throw new ProfileException("That profile no longer exists.");
            change(p);
            p.Updated = DateTime.Now;
            Save();
        }
    }

    public void Rename(string name, string newName)
    {
        lock (_gate)
        {
            var p = Find(name) ?? throw new ProfileException("That profile no longer exists.");
            var clean = CleanName(newName, allowSameAs: p.Name);
            var wasActive = _data.Active?.Equals(p.Name, StringComparison.OrdinalIgnoreCase) == true;
            p.Name = clean;
            if (wasActive)
                _data.Active = clean;
            Save();
        }
    }

    public void Delete(string name)
    {
        lock (_gate)
        {
            var p = Find(name) ?? throw new ProfileException("That profile no longer exists.");
            _data.Profiles.Remove(p);
            if (_data.Active?.Equals(p.Name, StringComparison.OrdinalIgnoreCase) == true)
                _data.Active = null;
            Save();
        }
    }

    public void SetActive(string? name)
    {
        lock (_gate)
        {
            _data.Active = name is null ? null : Find(name)?.Name;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                _data = JsonSerializer.Deserialize<ProfilesData>(File.ReadAllText(_file), AppEnvironment.Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("Could not read the profiles file: " + ex.Message);
            // keep the unreadable file for the person instead of overwriting it silently
            try { File.Move(_file, _file + ".unreadable", overwrite: true); } catch (IOException) { }
            _data = new();
        }
        _data.Profiles ??= new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, AppEnvironment.JsonPretty), new UTF8Encoding(false));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not save the profiles file: " + ex.Message);
        }
    }
}

/// <summary>One mod in a shared profile: enough for someone else to see what it is and where to get it.</summary>
internal sealed record PortableMod(string UniqueId, string Name, string? Version, long? NexusId, string? GitHub);

/// <summary>The file a profile is shared as. It lists mods, never settings or paths.</summary>
internal sealed record PortableProfile(string Format, int Version, string Name, DateTime Exported, List<PortableMod> Mods)
{
    public const string FormatName = "sprinkler-profile";
    public const int MaxBytes = 1_000_000;
    public const int MaxMods = 2000;

    public string ToJson() => JsonSerializer.Serialize(this, AppEnvironment.JsonPretty);

    /// <summary>Reads a shared profile. Throws a readable sentence for anything that is not one.</summary>
    public static PortableProfile Parse(string json)
    {
        if (json.Length > MaxBytes)
            throw new ProfileException("That file is too large to be a profile.");
        PortableProfile? p;
        try
        {
            p = JsonSerializer.Deserialize<PortableProfile>(json, AppEnvironment.Json);
        }
        catch (JsonException)
        {
            throw new ProfileException("That file is not a Sprinkler profile.");
        }
        if (p is null || p.Format != FormatName)
            throw new ProfileException("That file is not a Sprinkler profile.");
        if (p.Version > 1)
            throw new ProfileException("That profile was made by a newer version of Sprinkler. Update Sprinkler to open it.");
        if (p.Mods is null || p.Mods.Count > MaxMods)
            throw new ProfileException("That profile has no usable mod list.");

        var clean = p.Mods
            .Where(m => !string.IsNullOrWhiteSpace(m.UniqueId) && m.UniqueId.Length <= 200 && !m.UniqueId.Any(char.IsControl))
            .Select(m => m with { Name = string.IsNullOrWhiteSpace(m.Name) ? m.UniqueId : m.Name.Trim()[..Math.Min(m.Name.Trim().Length, 120)] })
            .DistinctBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return p with { Mods = clean };
    }
}
