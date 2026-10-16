using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using StardewModUpdater.Core;
using StardewModUpdater.Web;

namespace StardewModUpdater;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);

        if (options.Help || options.Check || options.Version)
            AttachConsole(-1);

        if (options.Version)
        {
            Console.WriteLine(AppEnvironment.AppName + " " + AppEnvironment.Version);
            return 0;
        }

        if (options.Help)
        {
            Console.WriteLine(Options.HelpText);
            return 0;
        }

        if (options.Check)
            return await CheckOnlyAsync(options);

        using var mutex = new Mutex(true, "Local\\StardewModUpdater.Single", out var first);
        if (!first && await ForwardToRunningInstanceAsync(options))
            return 0;

        var settings = new SettingsStore();
        var history = new HistoryStore();
        if (options.GamePath is not null)
            settings.Data.GamePath = options.GamePath;
        if (options.ModsPath is not null)
            settings.Data.ModsPath = options.ModsPath;

        using var service = new UpdateService(settings, history, url =>
        {
            if (WebHost.TryOpenable(url, out var u))
                WebHost.OpenInBrowser(u.ToString());
            else if (Environment.GetEnvironmentVariable("SMU_ALLOW_LOCAL") == "1")
                WebHost.OpenInBrowser(url);
        });

        await using var host = new WebHost(service);
        await host.StartAsync(options.Port);

        try
        {
            Directory.CreateDirectory(AppEnvironment.DataDir);
            File.WriteAllText(AppEnvironment.InstanceFile,
                JsonSerializer.Serialize(new { port = host.Port, token = host.Token, pid = Environment.ProcessId }));
        }
        catch (IOException ex)
        {
            Log.Write("Could not write instance file: " + ex.Message);
        }

        Log.Write($"Started {AppEnvironment.AppName} {AppEnvironment.Version} on port {host.Port}");
        if (!options.NoBrowser)
        {
            var window = AppWindow.Open(host.Url, settings.Data.AppWindow && !options.Browser);
            if (window is not null)
            {
                // closing the window ends the app; the idle timer is only a safety net here
                host.IdleLimit = TimeSpan.FromHours(6);
                _ = Task.Run(async () =>
                {
                    await window.WaitForExitAsync();
                    await Task.Delay(1500);
                    host.Quit();
                });
            }
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(AppEnvironment.DataDir, "url.txt"), host.Url);
        }

        _ = service.InitializeAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                Log.Write("Startup failed: " + t.Exception);
        });

        if (options.NxmUrl is not null)
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                await service.HandleNxmAsync(options.NxmUrl);
            });

        try
        {
            await Task.Delay(Timeout.Infinite, host.QuitToken);
        }
        catch (OperationCanceledException)
        {
        }

        try { File.Delete(AppEnvironment.InstanceFile); } catch (IOException) { }
        return 0;
    }

    private static async Task<bool> ForwardToRunningInstanceAsync(Options options)
    {
        try
        {
            var doc = JsonDocument.Parse(await File.ReadAllTextAsync(AppEnvironment.InstanceFile));
            var port = doc.RootElement.GetProperty("port").GetInt32();
            var token = doc.RootElement.GetProperty("token").GetString()!;

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(4) };
            http.DefaultRequestHeaders.Add("X-Token", token);
            (await http.GetAsync("/api/ping")).EnsureSuccessStatusCode();

            if (options.NxmUrl is not null)
            {
                (await http.PostAsJsonAsync("/api/nxm", new { url = options.NxmUrl })).EnsureSuccessStatusCode();
            }
            else
            {
                AppWindow.Open($"http://127.0.0.1:{port}/#t={token}", new SettingsStore().Data.AppWindow && !options.Browser);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static async Task<int> CheckOnlyAsync(Options options)
    {
        var settings = new SettingsStore();
        if (options.GamePath is not null)
            settings.Data.GamePath = options.GamePath;
        if (options.ModsPath is not null)
            settings.Data.ModsPath = options.ModsPath;

        using var service = new UpdateService(settings, new HistoryStore(), _ => { });
        await service.InitializeAsync();

        var snap = JsonSerializer.SerializeToElement(service.Snapshot(), AppEnvironment.Json);
        Console.WriteLine();
        if (snap.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine("Error: " + err.GetString());
            return 2;
        }
        if (snap.TryGetProperty("game", out var game))
        {
            var smapi = game.TryGetProperty("smapi", out var sv) ? sv.GetString() : "not found";
            var gameVersion = game.TryGetProperty("gameVersion", out var gv) ? gv.GetString() : "?";
            Console.WriteLine($"Game: {game.GetProperty("path").GetString()}  (SMAPI {smapi}, game {gameVersion})");
        }

        foreach (var row in snap.GetProperty("rows").EnumerateArray())
            Console.WriteLine($"  [{row.GetProperty("state").GetString(),-9}] {row.GetProperty("name").GetString(),-42} {row.GetProperty("version").GetString(),-14} {row.GetProperty("note").GetString()}");

        Console.WriteLine();
        var items = snap.GetProperty("items").EnumerateArray().ToList();
        Console.WriteLine(items.Count == 0 ? "No updates." : $"{items.Count} update(s) / install(s):");
        foreach (var item in items)
        {
            var offers = item.GetProperty("offers").EnumerateArray().ToList();
            var chosen = item.GetProperty("chosen").GetInt32();
            var via = offers.Count == 0 ? "no source found" : $"{offers[chosen].GetProperty("label").GetString()} ({offers[chosen].GetProperty("mode").GetString()})";
            var installedText = item.TryGetProperty("installed", out var inst) ? inst.GetString() : "new";
            Console.WriteLine($"  {item.GetProperty("name").GetString(),-42} {installedText} -> {item.GetProperty("target").GetString()}   via {via}");
            foreach (var o in offers)
                Console.WriteLine($"      - {o.GetProperty("label").GetString()} {(o.GetProperty("usable").GetBoolean() ? "" : "[unavailable] ")}{o.GetProperty("note").GetString()}");
        }

        foreach (var p in snap.GetProperty("problems").EnumerateArray())
            Console.WriteLine($"  ! {p.GetProperty("title").GetString()}: {p.GetProperty("detail").GetString()}");
        return 0;
    }

    private sealed record Options(string? ModsPath, string? GamePath, bool NoBrowser, bool Check, bool Help, int Port, string? NxmUrl, bool Browser = false, bool Version = false)
    {
        public const string HelpText =
            "Stardew Mod Updater\n" +
            "  StardewModUpdater.exe              open the app in your browser\n" +
            "  StardewModUpdater.exe --check      print what is out of date, change nothing\n" +
            "  --game <folder>                    use this Stardew Valley folder\n" +
            "  --mods <folder>                    use this Mods folder\n" +
            "  --no-browser                       do not open a browser tab\n" +
            "  --port <number>                    listen on a fixed local port\n";

        public static Options Parse(string[] args)
        {
            string? mods = null, game = null, nxm = null;
            bool noBrowser = false, check = false, help = false, browser = false, version = false;
            var port = 0;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--mods" when i + 1 < args.Length: mods = args[++i]; break;
                    case "--game" when i + 1 < args.Length: game = args[++i]; break;
                    case "--port" when i + 1 < args.Length: int.TryParse(args[++i], out port); break;
                    case "--no-browser": noBrowser = true; break;
                    case "--check": check = true; break;
                    case "--browser": browser = true; break;
                    case "--version" or "-v": version = true; break;
                    case "--help" or "-h" or "/?": help = true; break;
                    default:
                        if (a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                            nxm = a;
                        break;
                }
            }
            return new Options(mods, game, noBrowser, check, help, port, nxm, browser, version);
        }
    }
}
