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
        if (!await _messages.TryBeginAsync(email, ct))
        {
            return new PipelineOutcome(PipelineOutcomeKind.Duplicate);
        }

        if (!email.IsMailItem)
        {
            await _messages.MarkSkippedAsync(email.EntryId, SkipReason.NotMailItem, ct: ct);
            return new PipelineOutcome(PipelineOutcomeKind.Skipped, SkipReason: SkipReason.NotMailItem);
        }

        if (email.IsAutoReply)
        {
            await _messages.MarkSkippedAsync(email.EntryId, SkipReason.AutoReply, ct: ct);
            return new PipelineOutcome(PipelineOutcomeKind.Skipped, SkipReason: SkipReason.AutoReply);
        }

        var cleaned = _preparer.Prepare(email);

        if (cleaned.Body.Length < EmailPreparer.MinimumBodyChars)
        {
            await _messages.MarkSkippedAsync(
                email.EntryId, SkipReason.BodyTooShort, cleaned.BodyHash, ct);

            return new PipelineOutcome(PipelineOutcomeKind.Skipped, SkipReason: SkipReason.BodyTooShort);
        }

        // A listserv resending byte-identical content gets the previous verdict for free.
        var duplicate = await _messages.FindClassifiedByBodyHashAsync(
            cleaned.BodyHash, email.EntryId, ct);

        if (duplicate is not null)
        {
            await _messages.MarkSkippedAsync(
                email.EntryId, SkipReason.DuplicateBody, cleaned.BodyHash, ct);

            return new PipelineOutcome(PipelineOutcomeKind.Skipped, SkipReason: SkipReason.DuplicateBody);
        }

        ClassificationResult classification;

        try
        {
            classification = await _classifier.ClassifyAsync(cleaned, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Classification failed for {EntryId}.", email.EntryId);
            await _messages.MarkFailedAsync(email.EntryId, ex.Message, ct);

            return new PipelineOutcome(PipelineOutcomeKind.Failed, Error: ex.Message);
        }

        var rawJson = (_classifier as Ollama.OllamaClassifier)?.LastRawJson;

        await _messages.MarkClassifiedAsync(
            email.EntryId, cleaned.BodyHash, _settings.Ollama.Model, rawJson, _clock.UtcNow, ct);

        if (!classification.QualifiesAsFreeFoodEvent(_settings.Ollama.ConfidenceThreshold))
        {
            return new PipelineOutcome(PipelineOutcomeKind.NotRelevant);
        }

        var suggestion = BuildSuggestion(email, cleaned, classification);

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

            return new PipelineOutcome(PipelineOutcomeKind.Suppressed, suggestion);
        }

        return new PipelineOutcome(PipelineOutcomeKind.Suggested, suggestion);
    }

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
