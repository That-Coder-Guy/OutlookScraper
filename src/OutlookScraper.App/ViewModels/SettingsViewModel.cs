using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Input;
using OutlookScraper.App.Logging;
using OutlookScraper.App.Notifications;
using OutlookScraper.App.Startup;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Ollama;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;

namespace OutlookScraper.App.ViewModels;

/// <summary>One blacklist rule in the management grid.</summary>
public sealed class BlacklistRowViewModel(BlacklistEntry entry)
{
    public BlacklistEntry Model { get; } = entry;

    public Guid Id => Model.Id;

    public string TopicTag => Model.TopicTag;

    public string Category => Model.Category;

    public int HitCount => Model.HitCount;

    public bool Enabled => Model.Enabled;

    public string Created => Model.CreatedUtc.LocalDateTime.ToString("d MMM yyyy");

    /// <summary>Makes it obvious when a rule is running without semantic matching.</summary>
    public string Matching => Model.Embedding is null ? "text only" : "text + meaning";
}

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly BlacklistRepository _blacklist;
    private readonly BlacklistService _blacklistService;
    private readonly OllamaClient _ollama;
    private readonly IEmbeddingProvider _embeddings;
    private readonly RunAtLoginService _runAtLogin;
    private readonly INotifier _notifier;
    private readonly AppPaths _paths;

    private string? _statusMessage;
    private string _testTagA = "";
    private string _testTagB = "";
    private string? _tagTestResult;
    private bool _embeddingsAvailable;

    public SettingsViewModel(
        SettingsStore store,
        AppSettings settings,
        BlacklistRepository blacklist,
        BlacklistService blacklistService,
        OllamaClient ollama,
        IEmbeddingProvider embeddings,
        RunAtLoginService runAtLogin,
        INotifier notifier,
        AppPaths paths)
    {
        _store = store;
        Settings = settings;
        _blacklist = blacklist;
        _blacklistService = blacklistService;
        _ollama = ollama;
        _embeddings = embeddings;
        _runAtLogin = runAtLogin;
        _notifier = notifier;
        _paths = paths;

        SaveCommand = new AsyncCommand(async _ => await SaveAsync());
        RefreshModelsCommand = new AsyncCommand(async _ => await RefreshModelsAsync());
        DeleteRuleCommand = new AsyncCommand(async p => await DeleteRuleAsync(p));
        TestTagsCommand = new AsyncCommand(_ => { TestTags(); return Task.CompletedTask; });
        SendTestToastCommand = new AsyncCommand(async _ => await SendTestToastAsync());
        OpenLogsCommand = new AsyncCommand(_ => { OpenFolder(_paths.LogDirectory); return Task.CompletedTask; });
        OpenDataFolderCommand = new AsyncCommand(_ => { OpenFolder(_paths.Root); return Task.CompletedTask; });
    }

    public AppSettings Settings { get; }

    public ObservableCollection<string> AvailableModels { get; } = [];

    public ObservableCollection<BlacklistRowViewModel> BlacklistRules { get; } = [];

    public ICommand SaveCommand { get; }

    public ICommand RefreshModelsCommand { get; }

    public ICommand DeleteRuleCommand { get; }

    public ICommand TestTagsCommand { get; }

    public ICommand SendTestToastCommand { get; }

    public ICommand OpenLogsCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public IReadOnlyList<string> ConfidenceOptions { get; } = ["low", "medium", "high"];

    /// <summary>
    /// Exposed as three named steps rather than a slider, because the underlying model
    /// confidence is a three-way enum and a slider would imply precision that is not there.
    /// </summary>
    public string ConfidenceChoice
    {
        get => Settings.Ollama.ConfidenceThreshold switch
        {
            >= 0.9 => "high",
            >= 0.6 => "medium",
            _ => "low",
        };
        set
        {
            Settings.Ollama.ConfidenceThreshold = value switch
            {
                "high" => 0.9,
                "medium" => 0.6,
                _ => 0.3,
            };

            OnPropertyChanged();
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Drives the one-off hint suggesting an embedding model. Deliberately a quiet
    /// note, not a nag — matching works fine without one.
    /// </summary>
    public bool EmbeddingsAvailable
    {
        get => _embeddingsAvailable;
        private set
        {
            if (SetProperty(ref _embeddingsAvailable, value))
            {
                OnPropertyChanged(nameof(EmbeddingHint));
            }
        }
    }

    public string? EmbeddingHint => EmbeddingsAvailable
        ? null
        : $"Smarter blacklist matching is available. Run: ollama pull {Settings.Ollama.EmbeddingModel}";

    public string TestTagA
    {
        get => _testTagA;
        set => SetProperty(ref _testTagA, value);
    }

    public string TestTagB
    {
        get => _testTagB;
        set => SetProperty(ref _testTagB, value);
    }

    public string? TagTestResult
    {
        get => _tagTestResult;
        private set => SetProperty(ref _tagTestResult, value);
    }

    public bool RunAtLogin
    {
        get => Settings.General.RunAtLogin;
        set
        {
            Settings.General.RunAtLogin = value;
            _runAtLogin.SetEnabled(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Applied immediately rather than on Save, and deliberately so: the reason to reach
    /// for this checkbox is that mail is being mishandled right now, and the next arrival
    /// is the one worth capturing. Save still persists it for the next run.
    /// </summary>
    public bool VerboseLogging
    {
        get => Settings.General.VerboseLogging;
        set
        {
            Settings.General.VerboseLogging = value;
            SerilogSetup.SetVerbose(value);
            OnPropertyChanged();

            StatusMessage = value
                ? "Verbose logging is on — every message now logs its body size, the "
                  + "model's reasoning and the queue depth."
                : "Verbose logging is off. Per-message outcomes are still logged.";
        }
    }

    public async Task LoadAsync()
    {
        await RefreshModelsAsync();
        await RefreshBlacklistAsync();
        EmbeddingsAvailable = await _embeddings.IsAvailableAsync(CancellationToken.None);
    }

    private async Task RefreshModelsAsync()
    {
        AvailableModels.Clear();

        try
        {
            foreach (var model in await _ollama.ListModelsAsync(CancellationToken.None))
            {
                AvailableModels.Add(model);
            }

            StatusMessage = $"Found {AvailableModels.Count} model(s).";
        }
        catch (Exception)
        {
            // Naming the exact command is far more useful than "Ollama unavailable".
            StatusMessage = $"Could not reach Ollama at {Settings.Ollama.BaseUrl}. Is it running?";
        }
    }

    private async Task RefreshBlacklistAsync()
    {
        BlacklistRules.Clear();

        foreach (var entry in await _blacklist.GetAllAsync())
        {
            BlacklistRules.Add(new BlacklistRowViewModel(entry));
        }
    }

    private async Task DeleteRuleAsync(object? parameter)
    {
        if (parameter is not BlacklistRowViewModel row)
        {
            return;
        }

        var restored = await _blacklistService.RemoveAsync(row.Id);
        await RefreshBlacklistAsync();

        StatusMessage = restored > 0
            ? $"Removed '{row.TopicTag}' and restored {restored} suggestion(s)."
            : $"Removed '{row.TopicTag}'.";
    }

    /// <summary>
    /// Shows exactly what the deterministic stages of the cascade make of two tags.
    /// This is the answer to "why was that suppressed?" without reading the log.
    /// </summary>
    private void TestTags()
    {
        var keyA = TagNormalizer.Normalize(TestTagA);
        var keyB = TagNormalizer.Normalize(TestTagB);
        var jaccard = TokenSimilarity.Jaccard(TestTagA, TestTagB);

        var verdict = keyA.Length > 0 && keyA == keyB
            ? "would match (identical topic)"
            : jaccard >= Settings.Blacklist.TokenThreshold
                ? $"would match (overlap {jaccard:F2})"
                : $"would not match on text alone (overlap {jaccard:F2})";

        TagTestResult = $"'{keyA}' vs '{keyB}' — {verdict}.";
    }

    /// <summary>
    /// Always attempts the notification, and reports whatever actually happened.
    /// </summary>
    /// <remarks>
    /// Deliberately not gated on a registration check. Showing the toast is what causes
    /// the notification layer to register in the first place, so refusing to send when
    /// nothing is registered yet would mean the button could never succeed on a fresh
    /// install — and that is exactly the bug an earlier version had.
    /// </remarks>
    private async Task SendTestToastAsync()
    {
        try
        {
            await _notifier.ShowStatusAsync(
                "OutlookScraper", "Notifications are working. This is a test.");

            StatusMessage = "Test notification sent. " + ToastRegistration.Describe();
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not send a notification: " + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        await _store.SaveAsync(Settings);
        StatusMessage = "Settings saved.";
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
