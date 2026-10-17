using System.Text.Json;

namespace Sprinkler.Core;

internal enum SourcePolicy
{
    /// <summary>Nexus first; use other sites only if Nexus fails or the user picks one.</summary>
    NexusFirst,

    /// <summary>Use a site that downloads without clicks when its version matches Nexus.</summary>
    InstantFirst,
}

internal sealed class SettingsData
{
    public string? GamePath { get; set; }
    public string? ModsPath { get; set; }
    public string? DownloadsPath { get; set; }
    public string? NexusKeyProtected { get; set; }
    public SourcePolicy Policy { get; set; } = SourcePolicy.NexusFirst;
    public bool NxmRegistered { get; set; }
    public string? NxmPreviousHandler { get; set; }
    public List<string> Ignored { get; set; } = new();
    public Dictionary<string, string> SkippedVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ModNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int BackupsPerMod { get; set; } = 5;
    public bool Welcomed { get; set; }
    public bool AppWindow { get; set; } = true;
    public long LogDismissedTicks { get; set; }
    public string Theme { get; set; } = "auto";
    public string Language { get; set; } = "auto";
    public bool CheckAppUpdates { get; set; } = true;
    public bool TourSeen { get; set; }
    public bool GroupContentPacks { get; set; } = true;
}

/// <summary>Settings stored as JSON. The Nexus key goes to the safest place the system offers (see SecretStores).</summary>
internal sealed class SettingsStore
{
    private readonly string _file;
    private readonly object _gate = new();

    public SettingsData Data { get; private set; } = new();

    private readonly ISecretStore _secrets;

    public SettingsStore(string? file = null)
    {
        _file = file ?? AppEnvironment.SettingsFile;
        Load();
        _secrets = SecretStores.Create(Data, Save, Path.GetDirectoryName(Path.GetFullPath(_file))!);
    }

    /// <summary>A short phrase saying where the Nexus key is kept, shown to the user.</summary>
    public string KeyStorage => _secrets.Description;

    private readonly object _keyGate = new();
    private string? _keyCache;
    private DateTime _keyReadAt = DateTime.MinValue;

    /// <summary>
    /// The Nexus key. Asking the keyring can mean starting a helper program, and every request to Nexus asks for the key, so
    /// the answer is kept. A missing key is only remembered for a few seconds, so one added in the meantime is noticed.
    /// </summary>
    public string? NexusKey
    {
        get
        {
            lock (_keyGate)
            {
                var age = DateTime.UtcNow - _keyReadAt;
                if (age > (_keyCache is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(10)))
                {
                    _keyCache = _secrets.Read();
                    _keyReadAt = DateTime.UtcNow;
                }
                return _keyCache;
            }
        }
        set
        {
            _secrets.Write(value);
            lock (_keyGate)
            {
                _keyCache = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                _keyReadAt = DateTime.UtcNow;
            }
        }
    }

    public string DownloadsPath => string.IsNullOrWhiteSpace(Data.DownloadsPath) ? Platform.DownloadsFolder() : Data.DownloadsPath!;

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, AppEnvironment.JsonPretty));
            File.Move(tmp, _file, overwrite: true);
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                Data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_file), AppEnvironment.Json) ?? new SettingsData();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write("Settings unreadable, using defaults: " + ex.Message);
            // keep the damaged file: it holds the saved Nexus key, notes and ignored mods, and the next save would replace it
            try { File.Move(_file, _file + ".unreadable", overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Data = new SettingsData();
        }
    }
}
