using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StardewModUpdater.Core;

namespace StardewModUpdater.Web;

/// <summary>The local web server behind the window. It only answers requests from this PC and requires a per-run token.</summary>
internal sealed class WebHost : IAsyncDisposable
{
    private static readonly string[] OpenableHosts =
    {
        "nexusmods.com", "curseforge.com", "github.com", "moddrop.com", "smapi.io", "stardewvalleywiki.com", "wiki.stardewvalley.net",
    };

    private readonly UpdateService _service;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly CancellationTokenSource _quit = new();
    private WebApplication? _app;
    private DateTime _lastBeat = DateTime.UtcNow;
    private bool _sawBeat;

    public int Port { get; private set; }
    public string Token => _token;
    public string Url => $"http://127.0.0.1:{Port}/#t={_token}";
    public CancellationToken QuitToken => _quit.Token;
    public TimeSpan IdleLimit { get; set; } = TimeSpan.FromMinutes(3);

    public void Quit() => _quit.Cancel();

    public WebHost(UpdateService service) => _service = service;

    public static void OpenInBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    public async Task StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = ArchiveFile.MaxTotalBytes);

        var app = builder.Build();
        _app = app;

        app.Use(async (ctx, next) =>
        {
            var host = ctx.Request.Host.Host;
            if (host != "127.0.0.1" && host != "localhost")
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            if (ctx.Request.Path.StartsWithSegments("/api")
                && ctx.Request.Headers["X-Token"] != _token)
            {
                ctx.Response.StatusCode = 401;
                return;
            }
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; script-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
            await next();
        });

        app.MapGet("/", () =>
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
            using var r = new StreamReader(s);
            return Results.Content(r.ReadToEnd(), "text/html; charset=utf-8");
        });

        app.MapGet("/inter.woff2", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Stream(Assembly.GetExecutingAssembly().GetManifestResourceStream("inter.woff2")!, "font/woff2");
        });

        app.MapGet("/chicken.png", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Stream(Assembly.GetExecutingAssembly().GetManifestResourceStream("chicken.png")!, "image/png");
        });

        MapApi(app);

        await app.StartAsync();
        var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(addr).Port;
        _ = Task.Run(WatchdogAsync);
    }

    private void MapApi(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/ping", () => Results.Ok(new { ok = true }));

        api.MapGet("/state", async (long? v, CancellationToken ct) =>
        {
            for (var i = 0; i < 100 && v is not null && _service.Version == v && !ct.IsCancellationRequested; i++)
                await Task.Delay(150, CancellationToken.None);
            return Results.Json(_service.Snapshot(), AppEnvironment.Json);
        });

        api.MapPost("/heartbeat", () =>
        {
            _sawBeat = true;
            _lastBeat = DateTime.UtcNow;
            return Results.Ok();
        });

        api.MapPost("/quit", () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                _quit.Cancel();
            });
            return Results.Ok();
        });

        api.MapPost("/check", () =>
        {
            _ = _service.CheckAsync();
            return Results.Ok();
        });

        api.MapPost("/game", async (PathBody body) =>
        {
            var error = await _service.SetGameFolderAsync(body.Path ?? "");
            return Results.Json(new { error });
        });

        api.MapPost("/welcome", () => { _service.ConfirmWelcome(); return Results.Ok(); });
        api.MapPost("/dismiss", () => { _service.Dismiss(); return Results.Ok(); });
        api.MapPost("/back", () => { _service.BackToReview(); return Results.Ok(); });

        api.MapPost("/apply", (IdsBody body) =>
        {
            _ = _service.ApplyAsync(body.Ids ?? new());
            return Results.Ok();
        });

        api.MapPost("/select", (SelectBody body) =>
        {
            if (body.Id is null)
                _service.SetAllSelected(body.Selected);
            else
                _service.SetSelected(body.Id, body.Selected);
            return Results.Ok();
        });

        api.MapPost("/source", (SourceBody body) =>
        {
            _service.ChooseSource(body.Id ?? "", body.Index);
            return Results.Ok();
        });

        api.MapPost("/alternatives", async (IdBody body) =>
        {
            await _service.LoadAlternativesAsync(body.Id ?? "");
            return Results.Ok();
        });

        api.MapPost("/skip", (IdBody body) => { _service.SkipWaiting(body.Id ?? ""); return Results.Ok(); });
        api.MapPost("/reopen", (IdBody body) => { _service.ReopenPage(body.Id ?? ""); return Results.Ok(); });

        api.MapPost("/open", (UrlBody body) =>
        {
            if (!TryOpenable(body.Url, out var uri))
                return Results.BadRequest(new { error = "That address is not one the app opens." });
            OpenInBrowser(uri.ToString());
            return Results.Ok();
        });

        api.MapPost("/reveal", (WhatBody body) =>
        {
            var path = body.What switch
            {
                "backups" => AppEnvironment.BackupsDir,
                "logs" => AppEnvironment.LogsDir,
                "mods" => _service.Settings.Data.ModsPath,
                "downloads" => _service.Settings.DownloadsPath,
                _ => null,
            };
            if (path is null || !Directory.Exists(path))
                return Results.NotFound();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            return Results.Ok();
        });

        api.MapPost("/diagnostics", () => Results.Json(new { text = _service.Diagnostics() }));

        api.MapPost("/launch", () => Results.Json(new { error = _service.LaunchGame() }));

        api.MapPost("/reveal-mod", (IdBody body) =>
        {
            var path = _service.ModFolder(body.Id ?? "");
            if (path is null || !Directory.Exists(path))
                return Results.NotFound();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            return Results.Ok();
        });

        api.MapPost("/nexus/key", async (KeyBody body) =>
        {
            var error = await _service.ConnectNexusAsync(body.Key ?? "");
            return Results.Json(new { error });
        });
        api.MapPost("/nexus/disconnect", async () => { await _service.DisconnectNexusAsync(); return Results.Ok(); });
        api.MapPost("/nexus/nxm", (FlagBody body) =>
        {
            string? note = null;
            if (body.On) note = _service.RegisterNxm(); else _service.UnregisterNxm();
            return Results.Json(new { note });
        });

        api.MapPost("/window", (FlagBody body) =>
        {
            _service.Settings.Data.AppWindow = body.On;
            _service.Settings.Save();
            return Results.Ok();
        });

        api.MapPost("/policy", (PolicyBody body) =>
        {
            if (Enum.TryParse<SourcePolicy>(body.Policy, true, out var p))
                _service.SetPolicy(p);
            return Results.Ok();
        });

        api.MapPost("/downloads", (PathBody body) => { _service.SetDownloadsFolder(body.Path ?? ""); return Results.Ok(); });
        api.MapPost("/ignore", (IgnoreBody body) => { _service.SetIgnored(body.Id ?? "", body.Ignore); return Results.Ok(); });

        api.MapPost("/found/install", async (IdBody body) => { await _service.InstallFoundAsync(body.Id ?? ""); return Results.Ok(); });
        api.MapPost("/found/dismiss", (IdBody body) => { _service.DismissFound(body.Id ?? ""); return Results.Ok(); });

        api.MapPost("/undo", async (IdBody body) =>
        {
            var error = await _service.UndoAsync(body.Id ?? "");
            return Results.Json(new { error });
        });

        api.MapPost("/nxm", (UrlBody body) =>
        {
            _ = _service.HandleNxmAsync(body.Url ?? "");
            return Results.Ok();
        });

        api.MapPost("/upload", async (HttpRequest req, string name) =>
        {
            var safe = Path.GetFileName(name);
            if (!Downloader.IsArchiveName(safe))
                return Results.BadRequest(new { error = "Only .zip, .7z and .rar files can be installed." });
            var dir = Path.Combine(AppEnvironment.StagingDir, "dl-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, safe);
            await using (var fs = File.Create(path))
                await req.Body.CopyToAsync(fs);
            await _service.AddUploadedFileAsync(path, safe);
            return Results.Ok();
        });
    }

    internal static bool TryOpenable(string? raw, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = u.Host.ToLowerInvariant();
        if (!OpenableHosts.Any(h => host == h || host.EndsWith("." + h)))
            return false;
        uri = u;
        return true;
    }

    private async Task WatchdogAsync()
    {
        var startedAt = DateTime.UtcNow;
        while (!_quit.IsCancellationRequested)
        {
            await Task.Delay(5000);
            var idle = DateTime.UtcNow - (_sawBeat ? _lastBeat : startedAt);
            if (idle > IdleLimit && !_service.IsBusy)
            {
                Log.Write("No browser tab is open any more, exiting.");
                _quit.Cancel();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed record PathBody(string? Path);
    private sealed record IdBody(string? Id);
    private sealed record IdsBody(List<string>? Ids);
    private sealed record SelectBody(string? Id, bool Selected);
    private sealed record SourceBody(string? Id, int Index);
    private sealed record UrlBody(string? Url);
    private sealed record WhatBody(string? What);
    private sealed record KeyBody(string? Key);
    private sealed record FlagBody(bool On);
    private sealed record PolicyBody(string? Policy);
    private sealed record IgnoreBody(string? Id, bool Ignore);
}
