using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Sprinkler.Core;

namespace Sprinkler;

internal static class Program
{
    [DllImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern bool AttachConsole(int processId);

    /// <summary>A windowed Windows program has no console, so borrow the one it was started from. Elsewhere the terminal is already attached.</summary>
    private static void AttachParentConsole()
    {
        if (OperatingSystem.IsWindows())
            AttachConsole(-1);
    }

    internal static Options Options { get; private set; } = Options.Parse(Array.Empty<string>());

    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Options = Options.Parse(args);
        var options = Options;

        if (options.Help || options.Check || options.Version || options.InstallShortcut || options.RemoveShortcut)
            AttachParentConsole();

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
        if (options.InstallShortcut || options.RemoveShortcut)
            return ManageShortcut(options.InstallShortcut);
        if (options.Check)
            return CheckOnlyAsync(options).GetAwaiter().GetResult();

        Instance = new SingleInstance();
        if (!Instance.IsFirst)
        {
            // another copy is running: wake it up (or hand it the Nexus link) and leave
            var message = options.NxmUrl is not null ? "nxm " + options.NxmUrl : "activate";
            if (InstanceChannel.SendAsync(message).GetAwaiter().GetResult())
                return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "Sprinkler" })
            .WithInterFont()
            .LogToTrace();

    private static int ManageShortcut(bool install)
    {
        if (!Platform.IsLinux)
        {
            Console.WriteLine("Menu shortcuts are only needed on Linux. On Windows, pin or copy the program instead.");
            return 1;
        }
        try
        {
            if (install)
                Console.WriteLine(LinuxShortcut.Install());
            else
            {
                LinuxShortcut.Remove();
                Console.WriteLine("Removed Sprinkler from your application menu.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("Could not change the menu shortcut: " + ex.Message);
            return 2;
        }
    }

    /// <summary>Prints what is out of date without opening a window or changing anything.</summary>
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
}

/// <summary>Command line choices.</summary>
internal sealed record Options(string? ModsPath, string? GamePath, bool Check, bool Help, string? NxmUrl, bool Version = false, bool InstallShortcut = false, bool RemoveShortcut = false, string? Tab = null, string? Theme = null, bool NoTour = false, string? Filter = null, string? Search = null, string? Language = null)
{
    public const string HelpText =
        "Sprinkler, a Stardew Valley mod updater\n" +
        "  Sprinkler                          open the app\n" +
        "  Sprinkler --check                  print what is out of date, change nothing\n" +
        "  --game <folder>                    use this Stardew Valley folder\n" +
        "  --mods <folder>                    use this Mods folder\n" +
        "  --lang <code>                      use a language for this run, for example de or ja\n" +
        "  --version                          print the version\n" +
        "  --install-shortcut                 add the app to the Linux application menu\n" +
        "  --remove-shortcut                  take it out of the menu again\n";

    public static Options Parse(string[] args)
    {
        string? mods = null, game = null, nxm = null, tab = null, theme = null, filter = null, search = null, language = null;
        bool check = false, help = false, version = false, installShortcut = false, removeShortcut = false, noTour = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--mods" when i + 1 < args.Length: mods = args[++i]; break;
                case "--game" when i + 1 < args.Length: game = args[++i]; break;
                case "--check": check = true; break;
                case "--tab" when i + 1 < args.Length: tab = args[++i]; break;
                case "--theme" when i + 1 < args.Length: theme = args[++i]; break;
                case "--no-tour": noTour = true; break;
                case "--lang" when i + 1 < args.Length: language = args[++i]; break;
                case "--filter" when i + 1 < args.Length: filter = args[++i]; break;
                case "--search" when i + 1 < args.Length: search = args[++i]; break;
                case "--install-shortcut": installShortcut = true; break;
                case "--remove-shortcut": removeShortcut = true; break;
                case "--version" or "-v": version = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default:
                    if (a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
                        nxm = a;
                    break;
            }
        }
        return new Options(mods, game, check, help, nxm, version, installShortcut, removeShortcut, tab, theme, noTour, filter, search, language);
    }
}
