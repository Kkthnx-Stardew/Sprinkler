namespace StardewModUpdater.Core;

/// <summary>
/// Makes sure only one copy of the app runs per data folder. A file held open without sharing works the same on
/// Windows, Linux and macOS, and the system releases it by itself if the app crashes.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream? _lock;

    public bool IsFirst => _lock is not null;

    public SingleInstance()
    {
        try
        {
            Directory.CreateDirectory(AppEnvironment.DataDir);
            _lock = new FileStream(Path.Combine(AppEnvironment.DataDir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            _lock = null;
        }
        catch (UnauthorizedAccessException)
        {
            _lock = null;
        }
    }

    public void Dispose() => _lock?.Dispose();
}
