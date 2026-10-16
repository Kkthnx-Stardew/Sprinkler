using System.Text.Json;

namespace StardewModUpdater.Core;

internal sealed class HistoryFolder
{
    /// <summary>Folder path relative to the Mods folder, using forward slashes.</summary>
    public string RelativePath { get; set; } = "";
    public string UniqueId { get; set; } = "";
    public bool WasNew { get; set; }
}

internal sealed class HistoryEntry
{
    public string Id { get; set; } = "";
    public DateTime When { get; set; }
    public string Name { get; set; } = "";
    public string? FromVersion { get; set; }
    public string ToVersion { get; set; } = "";
    public string Source { get; set; } = "";
    public string? BackupFile { get; set; }
    public List<HistoryFolder> Folders { get; set; } = new();
    public bool Undone { get; set; }
    public string? Note { get; set; }
}

/// <summary>A list of what the app changed, with the backup that lets each change be undone.</summary>
internal sealed class HistoryStore
{
    private readonly string _file;
    private readonly string _backupsDir;
    private readonly object _gate = new();
    private List<HistoryEntry> _entries = new();

    public HistoryStore(string? file = null, string? backupsDir = null)
    {
        _file = file ?? AppEnvironment.HistoryFile;
        _backupsDir = backupsDir ?? AppEnvironment.BackupsDir;
        Load();
    }

    public string BackupsDir => _backupsDir;

    public List<HistoryEntry> All()
    {
        lock (_gate)
            return _entries.OrderByDescending(e => e.When).ToList();
    }

    public HistoryEntry? Find(string id)
    {
        lock (_gate)
            return _entries.FirstOrDefault(e => e.Id == id);
    }

    public void Add(HistoryEntry entry, int keepPerMod)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Prune(keepPerMod);
            Save();
        }
    }

    public void MarkUndone(string id)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e is not null)
            {
                e.Undone = true;
                Save();
            }
        }
    }

    private long _bytes;
    private DateTime _bytesAt = DateTime.MinValue;

    public long BackupBytes()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _bytesAt < TimeSpan.FromSeconds(10))
                return _bytes;
            try
            {
                _bytes = Directory.Exists(_backupsDir)
                    ? new DirectoryInfo(_backupsDir).EnumerateFiles("*.zip").Sum(f => f.Length)
                    : 0;
            }
            catch (IOException)
            {
                _bytes = 0;
            }
            _bytesAt = DateTime.UtcNow;
            return _bytes;
        }
    }

    private void Prune(int keepPerMod)
    {
        var withBackup = _entries.Where(e => e.BackupFile is not null && !e.Undone).ToList();
        foreach (var group in withBackup.GroupBy(e => e.Folders.FirstOrDefault()?.UniqueId ?? e.Name))
        {
            foreach (var old in group.OrderByDescending(e => e.When).Skip(Math.Max(1, keepPerMod)))
            {
                try
                {
                    var path = Path.Combine(_backupsDir, old.BackupFile!);
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (IOException)
                {
                }
                old.BackupFile = null;
            }
        }

        if (_entries.Count > 500)
            _entries = _entries.OrderByDescending(e => e.When).Take(500).ToList();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_file), AppEnvironment.Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write("History unreadable: " + ex.Message);
            _entries = new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, AppEnvironment.JsonPretty));
        File.Move(tmp, _file, overwrite: true);
    }
}
