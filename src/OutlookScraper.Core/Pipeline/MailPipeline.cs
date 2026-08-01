using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;
using OutlookScraper.Core.Text;
using OutlookScraper.Core.Time;

namespace OutlookScraper.Core.Pipeline;

/// <summary>What happened to one message.</summary>
public enum PipelineOutcomeKind
{
    /// <summary>Seen before. The normal result when several delivery paths overlap.</summary>
    Duplicate,

    /// <summary>Filtered structurally, before the model was consulted.</summary>
    Skipped,

    /// <summary>Classified, but not a free-food event (or below the confidence bar).</summary>
    NotRelevant,

    /// <summary>A free-food event the user has already said they don't want.</summary>
    Suppressed,

    /// <summary>A new suggestion the user should see.</summary>
    Suggested,

    /// <summary>Classification failed; the message stays retryable.</summary>
    Failed,
}

public sealed record PipelineOutcome(
    PipelineOutcomeKind Kind,
    EventSuggestion? Suggestion = null,
    SkipReason SkipReason = SkipReason.None,
    string? Error = null);

/// <summary>
/// Takes one raw email all the way to either a stored suggestion or a recorded reason
/// it was not one.
/// </summary>
public sealed class MailPipeline(
    ProcessedMessageRepository messages,
    SuggestionRepository suggestions,
    BlacklistRepository blacklist,
    IClassifier classifier,
    IBlacklistMatcher matcher,
    EmailPreparer preparer,
    EventTimeResolver timeResolver,
    AppSettings settings,
    IClock clock,
    ILogger<MailPipeline>? logger = null)
{
    private readonly ProcessedMessageRepository _messages = messages;
    private readonly SuggestionRepository _suggestions = suggestions;
    private readonly BlacklistRepository _blacklist = blacklist;
    private readonly IClassifier _classifier = classifier;
    private readonly IBlacklistMatcher _matcher = matcher;
    private readonly EmailPreparer _preparer = preparer;
    private readonly EventTimeResolver _timeResolver = timeResolver;
    private readonly AppSettings _settings = settings;
    private readonly IClock _clock = clock;
    private readonly ILogger<MailPipeline>? _logger = logger;

    public async Task<PipelineOutcome> ProcessAsync(RawEmail email, CancellationToken ct)
    {
        // Claiming the message is what makes ItemAdd, NewMailEx and the sweep safe to
        // all deliver the same mail.
        var id = ShortId(email.EntryId);

        if (!await _messages.TryBeginAsync(email, ct))
        {
            // Expected constantly — three delivery paths race for every message.
            _logger?.LogDebug("[{Id}] already seen, dropping duplicate delivery.", id);
            return new PipelineOutcome(PipelineOutcomeKind.Duplicate);
        }

        _logger?.LogInformation(
            "[{Id}] processing '{Subject}' from {Sender}, received {Received:yyyy-MM-dd HH:mm}.",
            id, Trim(email.Subject, 70), email.SenderAddress, email.ReceivedLocal);

        if (!email.IsMailItem)
        {
            _logger?.LogInformation(
                "[{Id}] skipped: not a mail item (MessageClass '{Class}').", id, email.MessageClass);

            await _messages.MarkSkippedAsync(email.EntryId, SkipReason.NotMailItem, ct: ct);
            return new PipelineOutcome(PipelineOutcomeKind.Skipped, SkipReason: SkipReason.NotMailItem);
        }

        if (email.IsAutoReply)
        {
            _logger?.LogInformation("[{Id}] skipped: auto-reply or delivery report.", id);
            await _messages.MarkSkippedAsync(email.EntryId, SkipReason.AutoReply, ct: ct);
            return new PipelineOutcome(PipelineOutcomeKind.Skipped, SkipReason: SkipReason.AutoReply);
        }

        var cleaned = _preparer.Prepare(email);

        _logger?.LogDebug(
            "[{Id}] cleaned body {Length} chars (from {Source}), hash {Hash}.",
            id,
            cleaned.Body.Length,
            string.IsNullOrWhiteSpace(email.PlainBody) ? "HTML" : "plain text",
            cleaned.BodyHash[..8]);

        if (cleaned.SignalLength < EmailPreparer.MinimumSignalChars)
        {
            _logger?.LogInformation(
                "[{Id}] skipped: too little text ({Signal} chars of subject + body, "
                + "under the {Minimum} minimum).",
                id, cleaned.SignalLength, EmailPreparer.MinimumSignalChars);

            await _messages.MarkSkippedAsync(
                email.EntryId, SkipReason.BodyTooShort, cleaned.BodyHash, ct);

            return new PipelineOutcome(PipelineOutcomeKind.Skipped, SkipReason: SkipReason.BodyTooShort);
        }

        // A listserv resending byte-identical content gets the previous verdict for free.
        var duplicate = await _messages.FindClassifiedByBodyHashAsync(
            cleaned.BodyHash, email.EntryId, ct);

        if (duplicate is not null)
        {
            _logger?.LogInformation(
                "[{Id}] skipped: identical body already classified as [{Other}]; reusing that verdict.",
                id, ShortId(duplicate.EntryId));

            await _messages.MarkSkippedAsync(
                email.EntryId, SkipReason.DuplicateBody, cleaned.BodyHash, ct);

            return new PipelineOutcome(PipelineOutcomeKind.Skipped, SkipReason: SkipReason.DuplicateBody);
        }

        ClassificationResult classification;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger?.LogDebug(
                "[{Id}] asking {Model} to classify {Length} chars.",
                id, _settings.Ollama.Model, cleaned.Body.Length);

            classification = await ClassifyWithHeartbeatAsync(id, cleaned, stopwatch, ct);
            stopwatch.Stop();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();

            _logger?.LogWarning(
                "[{Id}] classification FAILED after {Elapsed:n1}s: {Message}",
                id, stopwatch.Elapsed.TotalSeconds, ex.Message);

            await _messages.MarkFailedAsync(email.EntryId, ex.Message, ct);

            return new PipelineOutcome(PipelineOutcomeKind.Failed, Error: ex.Message);
        }

        _logger?.LogInformation(
            "[{Id}] classified in {Elapsed:n1}s: event={IsEvent} freeFood={HasFood} " +
            "confidence={Confidence} category={Category} tag='{Tag}'.",
            id,
            stopwatch.Elapsed.TotalSeconds,
            classification.IsEvent,
            classification.HasFreeFood,
            classification.Confidence,
            classification.Category,
            classification.TopicTag);

        _logger?.LogDebug("[{Id}] model reasoning: {Reason}", id, classification.Reason);

        var rawJson = (_classifier as Ollama.OllamaClassifier)?.LastRawJson;

        await _messages.MarkClassifiedAsync(
            email.EntryId, cleaned.BodyHash, _settings.Ollama.Model, rawJson, _clock.UtcNow, ct);

        if (!classification.QualifiesAsFreeFoodEvent(_settings.Ollama.ConfidenceThreshold))
        {
            // Spell out which of the three conditions failed — "not relevant" alone
            // makes a mis-set confidence threshold look like a model problem.
            _logger?.LogInformation(
                "[{Id}] not a free-food event ({Why}).",
                id,
                !classification.IsEvent ? "not an event"
                    : !classification.HasFreeFood ? "food is not free"
                    : $"confidence {classification.Confidence} below the configured " +
                      $"threshold {_settings.Ollama.ConfidenceThreshold:0.0}");

            return new PipelineOutcome(PipelineOutcomeKind.NotRelevant);
        }

        var suggestion = BuildSuggestion(email, cleaned, classification);

        _logger?.LogDebug(
            "[{Id}] extracted '{Title}' at '{Location}', when={When}, needsDateReview={Review}.",
            id,
            Trim(suggestion.Title, 60),
            Trim(suggestion.Location, 40),
            suggestion.StartUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "unresolved",
            suggestion.NeedsDateReview);

        // Suppression necessarily happens here rather than before the model, because the
        // topic tag the blacklist matches on is produced *by* the model. That costs
        // nothing: every message was going to be classified anyway.
        var match = await _matcher.MatchAsync(classification, ct);

        await _suggestions.InsertAsync(suggestion, ct);

        if (match is not null)
        {
            await _suggestions.SuppressAsync(suggestion.Id, match, _clock.UtcNow, ct);
            await _blacklist.IncrementHitCountAsync(match.TagId, ct);

            suggestion.State = SuggestionState.Suppressed;
            suggestion.SuppressedByTagId = match.TagId;
            suggestion.SuppressStage = match.Stage;
            suggestion.SuppressScore = match.Score;

            _logger?.LogInformation(
                "[{Id}] SUPPRESSED by blacklist rule {Tag} ({Stage}, score {Score:0.00}). " +
                "Visible in the Suppressed tab.",
                id, match.TagId, match.Stage, match.Score);

            return new PipelineOutcome(PipelineOutcomeKind.Suppressed, suggestion);
        }

        _logger?.LogInformation(
            "[{Id}] FREE FOOD EVENT: '{Title}' — added to the pending list.",
            id, Trim(suggestion.Title, 60));

        return new PipelineOutcome(PipelineOutcomeKind.Suggested, suggestion);
    }

    /// <summary>How long a classification may run before the log says it is still alive.</summary>
    private static readonly TimeSpan HeartbeatAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs the classifier, logging a line if it takes long enough that the log would
    /// otherwise look like the app had stopped.
    /// </summary>
    /// <remarks>
    /// The first classification after Ollama starts pays for loading the model into
    /// VRAM, which on a cold 8B model is comfortably a minute. Without this, that minute
    /// is a gap between "processing" and nothing at all — indistinguishable from a hang,
    /// a deadlocked STA thread or a crashed worker. Saying "still waiting, 20s of a 90s
    /// timeout" costs one line and answers the question outright.
    /// </remarks>
    private async Task<ClassificationResult> ClassifyWithHeartbeatAsync(
        string id,
        CleanedEmail cleaned,
        System.Diagnostics.Stopwatch stopwatch,
        CancellationToken ct)
    {
        var classification = _classifier.ClassifyAsync(cleaned, ct);

        while (true)
        {
            var heartbeat = Task.Delay(HeartbeatAfter, ct);

            if (await Task.WhenAny(classification, heartbeat) == classification)
            {
                // Awaited rather than returned directly so a fault surfaces as its own
                // exception rather than an AggregateException.
                return await classification;
            }

            _logger?.LogInformation(
                "[{Id}] still waiting on {Model} after {Elapsed:n0}s (timeout is {Timeout}s). "
                + "A cold model load is slow the first time and fast afterwards.",
                id,
                _settings.Ollama.Model,
                stopwatch.Elapsed.TotalSeconds,
                _settings.Ollama.RequestTimeoutSeconds);
        }
    }

    /// <summary>
    /// Outlook EntryIDs are ~140 hex characters, which makes a log unreadable. The tail
    /// is the message-specific part, so it is enough to correlate lines within a run.
    /// </summary>
    private static string ShortId(string entryId) =>
        string.IsNullOrEmpty(entryId) ? "?" :
        entryId.Length <= 10 ? entryId : entryId[^10..];

    private static string Trim(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? "(none)"
        : value.Length <= maxLength ? value
        : value[..maxLength] + "…";

    private EventSuggestion BuildSuggestion(
        RawEmail email, CleanedEmail cleaned, ClassificationResult classification)
    {
        var resolution = _timeResolver.Resolve(classification, email.ReceivedLocal);

        return new EventSuggestion
        {
            Id = Guid.NewGuid(),
            EntryId = email.EntryId,
            Title = classification.Title,
            FoodDescription = classification.FoodDescription,
            Location = classification.Location,
            Organization = classification.Organization,
            StartUtc = resolution.Time?.Start,
            EndUtc = resolution.Time?.End,
            IanaTimeZone = resolution.IanaTimeZone,
            IsAllDay = resolution.Time?.IsAllDay ?? false,
            DateIsExplicit = classification.DateIsExplicit,

            // A suggestion with no usable date still surfaces — the user can read the
            // email and fill it in. It just cannot be booked until they do.
            NeedsDateReview = !resolution.IsResolved || !classification.DateIsExplicit,

            Category = classification.Category,
            TopicTag = classification.TopicTag,
            TopicTagKey = TagNormalizer.Normalize(classification.TopicTag),
            Reason = classification.Reason,
            Confidence = classification.Confidence.ToScore(),
            SenderName = email.SenderName,
            SenderAddress = email.SenderAddress,
            Subject = email.Subject,
            BodyExcerpt = cleaned.Excerpt(),
            State = SuggestionState.Pending,
            CreatedUtc = _clock.UtcNow,
        };
    }
}
