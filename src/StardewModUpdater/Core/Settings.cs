using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StardewModUpdater.Core;

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
    public int BackupsPerMod { get; set; } = 5;
    public bool Welcomed { get; set; }
    public bool AppWindow { get; set; } = true;
    public long LogDismissedTicks { get; set; }
    public string Theme { get; set; } = "auto";
    public bool TourSeen { get; set; }
}

/// <summary>Settings stored as JSON. The Nexus key is encrypted for the current Windows user.</summary>
internal sealed class SettingsStore
{
    private readonly string _file;
    private readonly object _gate = new();

    public SettingsData Data { get; private set; } = new();

    public SettingsStore(string? file = null)
    {
        _file = file ?? AppEnvironment.SettingsFile;
        Load();
    }

    public string? NexusKey
    {
        get
        {
            var protectedText = Data.NexusKeyProtected;
            if (string.IsNullOrEmpty(protectedText))
                return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }
        }
        set
        {
            Data.NexusKeyProtected = string.IsNullOrWhiteSpace(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
            Save();
        }
    }

    public string DownloadsPath => string.IsNullOrWhiteSpace(Data.DownloadsPath) ? AppEnvironment.DownloadsFolder() : Data.DownloadsPath!;

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
            Data = new SettingsData();
        }
    }
}
