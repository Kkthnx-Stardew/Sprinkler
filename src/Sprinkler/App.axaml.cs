using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Sprinkler.Core;
using Sprinkler.ViewModels;
using Sprinkler.Views;

namespace Sprinkler;

public partial class App : Application
{
    private UpdateService? _service;
    private MainViewModel? _viewModel;
    private InstanceChannel? _channel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = Program.Options;
            var settings = new SettingsStore();
            if (options.GamePath is not null)
                settings.Data.GamePath = options.GamePath;
            if (options.ModsPath is not null)
                settings.Data.ModsPath = options.ModsPath;

            _service = new UpdateService(settings, new HistoryStore(), url =>
            {
                if (!UrlPolicy.Open(url))
                    Log.Write("Refused to open " + url);
            });
            _viewModel = new MainViewModel(_service)
            {
                ThemeOverride = options.Theme,
                SuppressTour = options.NoTour,
            };
            if (options.Filter is { } filter)
                _viewModel.SetModFilter(filter);
            if (options.Search is { } search)
                _viewModel.ModSearch = search;
            if (options.Tab is { } tab && Enum.TryParse<AppTab>(tab, ignoreCase: true, out var startTab))
                _viewModel.Tab = startTab;

            var window = new MainWindow();
            window.Attach(_viewModel);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => Shutdown();

            _channel = new InstanceChannel();
            _channel.MessageReceived += message => Dispatcher.UIThread.Post(() => OnInstanceMessage(window, message));
            _channel.Start();

            _viewModel.Start();

            if (options.NxmUrl is { } nxm)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    await Dispatcher.UIThread.InvokeAsync(() => _viewModel.HandleNxmAsync(nxm));
                });

            Log.Write($"Started {AppEnvironment.AppName} {AppEnvironment.Version}");
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnInstanceMessage(Window window, string message)
    {
        if (message.StartsWith("nxm ", StringComparison.Ordinal))
            _ = _viewModel?.HandleNxmAsync(message[4..]);

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Shutdown()
    {
        _channel?.Dispose();
        _viewModel?.Dispose();
        _service?.Dispose();
    }

    /// <summary>Applies the person's light, dark or follow-the-system choice.</summary>
    internal static void ApplyTheme(string theme)
    {
        if (Current is { } app)
            app.RequestedThemeVariant = theme switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }
}
