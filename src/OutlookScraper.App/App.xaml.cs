using System.Runtime.Versioning;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OutlookScraper.App.Logging;
using OutlookScraper.App.Notifications;
using OutlookScraper.App.Startup;
using OutlookScraper.App.Tray;
using OutlookScraper.App.Views;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;
using OutlookScraper.Core.Time;

namespace OutlookScraper.App;

[SupportedOSPlatform("windows10.0.19041.0")]
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private ServiceProvider? _services;
    private AppHost? _host;
    private TrayIconService? _tray;
    private ILoggerFactory? _loggerFactory;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = SingleInstance.Acquire();

        if (!_singleInstance.IsPrimary)
        {
            // Another copy owns the tray. Hand over the command line and get out of
            // the way. (Toast activation of a running app does not come through here —
            // Windows calls its COM server in-process instead.)
            SingleInstance.ForwardToPrimary(e.Args);
            Shutdown();
            return;
        }

        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "OutlookScraper could not start:\n\n" + ex.Message,
                "OutlookScraper",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown();
        }
    }

    private async Task StartAsync()
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        // Settings first, deliberately: the verbose flag decides the log level, and a
        // logger created before it would miss startup at the level the user asked for.
        // SettingsStore takes no logger and falls back to defaults on a corrupt file, so
        // nothing worth logging can happen before this line.
        var settingsStore = new SettingsStore(paths.SettingsPath);
        var settings = await settingsStore.LoadAsync();

        _loggerFactory = SerilogSetup.Create(paths.LogDirectory, settings.General.VerboseLogging);
        var logger = _loggerFactory.CreateLogger<App>();

        if (string.IsNullOrWhiteSpace(settings.Calendar.TimeZone))
        {
            settings.Calendar.TimeZone = TimeZoneResolution.LocalIanaId();
            await settingsStore.SaveAsync(settings);
        }

        _services = Composition.Build(paths, settings, _loggerFactory);

        using (var connection = _services.GetRequiredService<Database>().Open())
        {
            Migrations.Apply(connection);
        }

        // Must be subscribed before any window is shown and before the first toast, or
        // a cold-start activation is dropped on the floor.
        var router = _services.GetRequiredService<ToastActivationRouter>();
        router.Subscribe();

        // Informational. Nothing is registered until the first notification is shown, so
        // "not registered yet" on a fresh install is expected and must not read as a
        // fault. Only a stale registration — the app was moved, which breaks toasts
        // silently — is worth a warning.
        if (ToastRegistration.LooksStale(out var registrationDetail))
        {
            logger.LogWarning("Toast registration is stale: {Detail}", registrationDetail);
        }
        else
        {
            logger.LogInformation("Toast registration: {Detail}", registrationDetail);
        }

        var runAtLogin = _services.GetRequiredService<RunAtLoginService>();
        runAtLogin.RepairIfMoved();

        if (settings.General.RunAtLogin && !runAtLogin.IsEnabled)
        {
            runAtLogin.SetEnabled(true);
        }

        SetUpTray();

        _host = _services.GetRequiredService<AppHost>();
        _host.ReviewRequested += async (_, _) => await ShowReviewAsync();
        _host.MailStateChanged += (_, state) => _tray?.SetMailState(state);
        _host.OllamaHealthChanged += (_, health) => _tray?.SetOllamaHealth(health);
        _host.PendingCountChanged += (_, count) =>
        {
            _tray?.SetPendingCount(count);
            _tray?.SetQueueDepth(_host!.QueueDepth);
        };

        await _host.StartAsync();

        // A process launched purely to service a background toast button must not pop
        // a window at the user.
        if (!ToastActivationRouter.LaunchedByToast() && !IsTrayLaunch())
        {
            await ShowReviewAsync();
        }

        logger.LogInformation(
            "OutlookScraper started. Verbose logging is {State}.",
            settings.General.VerboseLogging ? "on" : "off (enable it in Settings ▸ General)");
    }

    private static bool IsTrayLaunch() =>
        Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

    private void SetUpTray()
    {
        _tray = _services!.GetRequiredService<TrayIconService>();

        _tray.ReviewRequested += async (_, _) => await ShowReviewAsync();
        _tray.SettingsRequested += async (_, _) => await ShowSettingsAsync();
        _tray.RescanRequested += async (_, _) => await (_host?.CatchUpAsync() ?? Task.CompletedTask);
        _tray.RetryRequested += async (_, _) => await (_host?.RetryFailedAsync() ?? Task.CompletedTask);
        _tray.ExitRequested += (_, _) => Shutdown();
    }

    // InvokeAsync over an async lambda yields a DispatcherOperation<Task>; awaiting that
    // alone returns as soon as the *outer* delegate returns, not when the work finishes.
    // Unwrap is what makes these actually await the window refresh.
    private Task ShowReviewAsync() =>
        Dispatcher.InvokeAsync(
            () => _services!.GetRequiredService<ReviewWindow>().ShowRefreshedAsync())
            .Task.Unwrap();

    private Task ShowSettingsAsync() =>
        Dispatcher.InvokeAsync(
            () => _services!.GetRequiredService<SettingsWindow>().ShowRefreshedAsync())
            .Task.Unwrap();

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _tray?.Dispose();
        _services?.GetService<ToastActivationRouter>()?.Dispose();
        _services?.GetService<Database>()?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();

        SerilogSetup.Shutdown();
        _loggerFactory?.Dispose();

        base.OnExit(e);
    }
}
