using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewModUpdater.Core;

/// <summary>Where the app keeps data, and the base URLs it talks to (overridable so tests can use a fake server).</summary>
internal static class AppEnvironment
{
    public const string AppName = "Stardew Mod Updater";

    public static readonly string Version =
        typeof(AppEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string DataDir { get; } = ResolveDataDir();

    public static string BackupsDir => Path.Combine(DataDir, "backups");
    public static string StagingDir => Path.Combine(DataDir, "staging");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string InstanceFile => Path.Combine(DataDir, "instance.json");

    public static string SmapiApiUrl => Env("SMU_SMAPI_URL", "https://smapi.io/api/v3.0/mods");
    public static string NexusApiUrl => Env("SMU_NEXUS_URL", "https://api.nexusmods.com/v1").TrimEnd('/');
    public static string NexusSiteUrl => Env("SMU_NEXUS_SITE", "https://www.nexusmods.com").TrimEnd('/');
    public static string CurseForgeUrl => Env("SMU_CF_URL", "https://www.curseforge.com").TrimEnd('/');
    public static string GitHubApiUrl => Env("SMU_GH_URL", "https://api.github.com").TrimEnd('/');

    public const string NexusGame = "stardewvalley";
    public const string RepoSlug = "Kkthnx-Stardew/StardewModUpdater";
    public static string RepoUrl => "https://github.com/" + RepoSlug;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions JsonPretty = new(Json) { WriteIndented = true };

    private static readonly Lazy<HttpClient> SharedClient = new(CreateClient);

    public static HttpClient Http => SharedClient.Value;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StardewModUpdater", Version));
        return client;
    }

    private static string ResolveDataDir()
    {
        var overridePath = Environment.GetEnvironmentVariable("SMU_DATA");
        var dir = !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StardewModUpdater");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Env(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    public static string DownloadsFolder()
    {
        try
        {
            var guid = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            var hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var path);
            if (hr == 0 && !string.IsNullOrEmpty(path))
                return path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string path);
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppEnvironment.LogsDir);
            var file = Path.Combine(AppEnvironment.LogsDir, "app.log");
            lock (Gate)
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 1_000_000)
                    File.Move(file, Path.Combine(AppEnvironment.LogsDir, "app.old.log"), overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
