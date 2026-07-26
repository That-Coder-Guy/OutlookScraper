using System.Net.Http;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OutlookScraper.App.Notifications;
using OutlookScraper.App.Security;
using OutlookScraper.App.Startup;
using OutlookScraper.App.Tray;
using OutlookScraper.App.ViewModels;
using OutlookScraper.App.Views;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Calendar;
using OutlookScraper.Core.Ollama;
using OutlookScraper.Core.Pipeline;
using OutlookScraper.Core.Scheduling;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;
using OutlookScraper.Core.Text;
using OutlookScraper.Core.Time;
using OutlookScraper.Outlook;

namespace OutlookScraper.App;

/// <summary>Builds the object graph.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class Composition
{
    public static ServiceProvider Build(AppPaths paths, AppSettings settings, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();

        services.AddSingleton(loggerFactory);
        services.AddLogging();

        services.AddSingleton(paths);
        services.AddSingleton(settings);
        services.AddSingleton(settings.Ollama);
        services.AddSingleton(settings.Mail);
        services.AddSingleton(settings.Calendar);
        services.AddSingleton(settings.Blacklist);
        services.AddSingleton(settings.General);
        services.AddSingleton(new SettingsStore(paths.SettingsPath));
        services.AddSingleton<IClock>(SystemClock.Instance);

        // Storage
        services.AddSingleton(new Database(paths.DatabasePath));
        services.AddSingleton<ProcessedMessageRepository>();
        services.AddSingleton<SuggestionRepository>();
        services.AddSingleton<BlacklistRepository>();
        services.AddSingleton<CalendarMapRepository>();
        services.AddSingleton<StateRepository>();

        // Ollama. One HttpClient for the lifetime of the app: the base address and
        // timeout are fixed, and the endpoint is local.
        services.AddSingleton(_ => new HttpClient
        {
            BaseAddress = new Uri(settings.Ollama.BaseUrl),
            Timeout = TimeSpan.FromSeconds(settings.Ollama.RequestTimeoutSeconds),
        });

        services.AddSingleton<OllamaClient>();
        services.AddSingleton<OllamaHealthMonitor>();

        services.AddSingleton<IEmbeddingProvider>(sp => new OllamaEmbeddingProvider(
            sp.GetRequiredService<OllamaClient>(),
            settings.Ollama.EmbeddingModel,
            sp.GetService<ILogger<OllamaEmbeddingProvider>>()));

        services.AddSingleton(_ => new PromptBuilder(
            string.IsNullOrWhiteSpace(settings.Calendar.TimeZone)
                ? TimeZoneResolution.LocalIanaId()
                : settings.Calendar.TimeZone));

        services.AddSingleton<IClassifier, OllamaClassifier>();

        // Text and time
        services.AddSingleton<EmailPreparer>();
        services.AddSingleton<EventTimeResolver>();

        // Blacklist
        services.AddSingleton<IBlacklistMatcher, HybridBlacklistMatcher>();
        services.AddSingleton<BlacklistService>();

        // Calendar. The token store is DPAPI-backed rather than the stock
        // FileDataStore, which would write the refresh token in plaintext.
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton(sp => new GoogleAuthenticator(
            paths,
            new DpapiDataStore(paths.TokenDirectory, sp.GetRequiredService<ISecretProtector>())));
        services.AddSingleton<ICalendarSink, GoogleCalendarSink>();

        // Pipeline and scheduling
        services.AddSingleton<MailPipeline>();
        services.AddSingleton<ClassificationQueue>();
        services.AddSingleton<ToastRateLimiter>();
        services.AddSingleton(sp => new CircuitBreaker(sp.GetRequiredService<IClock>()));
        services.AddSingleton<ClassificationWorker>();

        // Windows surface
        services.AddSingleton<INotifier, ToastService>();
        services.AddSingleton<ToastActivationRouter>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<RunAtLoginService>();

        services.AddSingleton(sp => new OutlookMailSource(
            settings.Mail, sp.GetService<ILogger<OutlookMailSource>>()));

        services.AddSingleton<IMailSource>(sp => sp.GetRequiredService<OutlookMailSource>());

        services.AddSingleton<SuggestionActions>();
        services.AddSingleton<AppHost>();

        // UI
        services.AddSingleton<ReviewViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ReviewWindow>();
        services.AddSingleton<SettingsWindow>();

        return services.BuildServiceProvider();
    }
}
