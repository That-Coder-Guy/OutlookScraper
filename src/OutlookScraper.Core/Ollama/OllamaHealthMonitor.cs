using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;

namespace OutlookScraper.Core.Ollama;

/// <summary>
/// Periodically probes the local Ollama server and reports its state.
/// </summary>
/// <remarks>
/// Distinguishes "server unreachable" from "server fine, configured model not pulled".
/// The second case is common on a fresh install and has an exact fix, so the UI can
/// print the literal <c>ollama pull &lt;model&gt;</c> command instead of silently
/// substituting some other model the user never chose.
/// </remarks>
public sealed class OllamaHealthMonitor(
    OllamaClient client,
    OllamaSettings settings,
    ILogger<OllamaHealthMonitor>? logger = null) : IDisposable
{
    private readonly OllamaClient _client = client;
    private readonly OllamaSettings _settings = settings;
    private readonly ILogger<OllamaHealthMonitor>? _logger = logger;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;

    public OllamaHealth Health { get; private set; } = OllamaHealth.Unknown;

    /// <summary>Raised only on an actual transition, so callers can toast once rather than nag.</summary>
    public event EventHandler<OllamaHealth>? HealthChanged;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_stopping.Token));
    }

    public async Task<OllamaHealth> ProbeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        OllamaHealth health;

        try
        {
            var models = await _client.ListModelsAsync(timeout.Token);

            health = models.Any(m =>
                m.Equals(_settings.Model, StringComparison.OrdinalIgnoreCase) ||
                m.StartsWith(_settings.Model + ":", StringComparison.OrdinalIgnoreCase))
                ? OllamaHealth.Healthy
                : OllamaHealth.ModelMissing;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OllamaException)
        {
            health = OllamaHealth.Unreachable;
        }

        if (health != Health)
        {
            _logger?.LogInformation("Ollama health changed: {Previous} -> {Current}", Health, health);
            Health = health;
            HealthChanged?.Invoke(this, health);
        }

        return health;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);

        await ProbeAsync(ct);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await ProbeAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
