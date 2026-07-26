using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Storage;

namespace OutlookScraper.Core.Blacklist;

/// <summary>
/// Adds and removes blacklist rules, and keeps the pending queue consistent with them.
/// </summary>
public sealed class BlacklistService(
    BlacklistRepository blacklist,
    SuggestionRepository suggestions,
    IBlacklistMatcher matcher,
    IEmbeddingProvider embeddings,
    IClock clock,
    ILogger<BlacklistService>? logger = null)
{
    private readonly BlacklistRepository _blacklist = blacklist;
    private readonly SuggestionRepository _suggestions = suggestions;
    private readonly IBlacklistMatcher _matcher = matcher;
    private readonly IEmbeddingProvider _embeddings = embeddings;
    private readonly IClock _clock = clock;
    private readonly ILogger<BlacklistService>? _logger = logger;

    /// <summary>
    /// Blacklists the kind of event a suggestion represents, then immediately sweeps
    /// everything still pending.
    /// </summary>
    /// <returns>The new rule and how many other pending suggestions it swallowed.</returns>
    /// <remarks>
    /// The retroactive sweep is what makes tag-based blacklisting feel intelligent
    /// rather than tedious: blacklisting one fraternity pizza email should clear the
    /// other four already sitting in the queue, not leave them to be dismissed one by one.
    /// </remarks>
    public async Task<(BlacklistEntry Entry, int Swept)> BlacklistAsync(
        Guid suggestionId, CancellationToken ct = default)
    {
        var suggestion = await _suggestions.GetAsync(suggestionId, ct)
            ?? throw new InvalidOperationException($"Suggestion {suggestionId} not found.");

        // Best-effort. An embedding failure must never block the user's action — the
        // row is saved without one and picked up by the re-embedding backfill later.
        var vector = await _embeddings.EmbedAsync(
            $"{suggestion.Category}. {suggestion.TopicTag.Replace('-', ' ')}. {suggestion.Reason}", ct);

        var entry = new BlacklistEntry
        {
            Id = Guid.NewGuid(),
            Category = suggestion.Category,
            TopicTag = suggestion.TopicTag,
            TopicTagKey = string.IsNullOrEmpty(suggestion.TopicTagKey)
                ? TagNormalizer.Normalize(suggestion.TopicTag)
                : suggestion.TopicTagKey,
            Reason = suggestion.Reason,
            Embedding = vector,
            EmbedModel = vector is null ? null : _embeddings.ModelName,
            EmbedDim = vector?.Length,
            SourceEntryId = suggestion.EntryId,
            CreatedUtc = _clock.UtcNow,
        };

        var saved = await _blacklist.UpsertAsync(entry, ct);

        await _suggestions.SetStateAsync(
            suggestionId, SuggestionState.Blacklisted, _clock.UtcNow, ct);

        var swept = await SweepPendingAsync(saved.Id, ct);

        _logger?.LogInformation(
            "Blacklisted '{Tag}' ({Category}); suppressed {Swept} pending suggestions.",
            saved.TopicTag, saved.Category, swept);

        return (saved, swept);
    }

    /// <summary>
    /// Re-runs the matcher over everything pending and suppresses whatever the new rule
    /// now covers.
    /// </summary>
    public async Task<int> SweepPendingAsync(Guid newTagId, CancellationToken ct = default)
    {
        var pending = await _suggestions.GetByStateAsync(SuggestionState.Pending, ct);
        var swept = 0;

        foreach (var suggestion in pending)
        {
            var probe = new ClassificationResult
            {
                IsEvent = true,
                HasFreeFood = true,
                Confidence = ConfidenceLevel.High,
                Category = suggestion.Category,
                TopicTag = suggestion.TopicTag,
                Reason = suggestion.Reason,
            };

            var match = await _matcher.MatchAsync(probe, ct);

            if (match is null || match.TagId != newTagId)
            {
                continue;
            }

            await _suggestions.SuppressAsync(suggestion.Id, match, _clock.UtcNow, ct);
            await _blacklist.IncrementHitCountAsync(match.TagId, ct);
            swept++;
        }

        return swept;
    }

    /// <summary>
    /// Deletes a rule and restores anything it had suppressed, so removing a blacklist
    /// entry is genuinely reversible rather than leaving orphaned hidden suggestions.
    /// </summary>
    public async Task<int> RemoveAsync(Guid tagId, CancellationToken ct = default)
    {
        var affected = await _suggestions.GetSuppressedByTagAsync(tagId, ct);

        foreach (var suggestion in affected)
        {
            await _suggestions.UnsuppressAsync(suggestion.Id, ct);
        }

        await _blacklist.DeleteAsync(tagId, ct);

        return affected.Count;
    }

    /// <summary>
    /// Handles "this is not the same thing" on a soft-suppressed suggestion: restores
    /// it and records an exception so the same rule cannot re-match that tag.
    /// </summary>
    public async Task RescueAsync(Guid suggestionId, CancellationToken ct = default)
    {
        var suggestion = await _suggestions.GetAsync(suggestionId, ct)
            ?? throw new InvalidOperationException($"Suggestion {suggestionId} not found.");

        if (suggestion.SuppressedByTagId is { } tagId)
        {
            var key = string.IsNullOrEmpty(suggestion.TopicTagKey)
                ? TagNormalizer.Normalize(suggestion.TopicTag)
                : suggestion.TopicTagKey;

            await _blacklist.AddExceptionAsync(tagId, key, _clock.UtcNow, ct);
        }

        await _suggestions.UnsuppressAsync(suggestionId, ct);
    }

    /// <summary>
    /// Embeds any rule that has no usable vector for the current model. Runs when an
    /// embedding model first appears, and again whenever the configured model changes —
    /// vectors from different models are not comparable, so stale rows must be redone
    /// rather than compared across models.
    /// </summary>
    public async Task<int> BackfillEmbeddingsAsync(CancellationToken ct = default)
    {
        if (!await _embeddings.IsAvailableAsync(ct))
        {
            return 0;
        }

        var stale = await _blacklist.GetNeedingEmbeddingAsync(_embeddings.ModelName, ct);
        var updated = 0;

        foreach (var entry in stale)
        {
            var vector = await _embeddings.EmbedAsync(
                $"{entry.Category}. {entry.TopicTag.Replace('-', ' ')}. {entry.Reason}", ct);

            if (vector is null)
            {
                continue;
            }

            await _blacklist.SetEmbeddingAsync(entry.Id, vector, _embeddings.ModelName, ct);
            updated++;
        }

        if (updated > 0)
        {
            _logger?.LogInformation(
                "Backfilled {Count} blacklist embeddings using {Model}.", updated, _embeddings.ModelName);
        }

        return updated;
    }
}
