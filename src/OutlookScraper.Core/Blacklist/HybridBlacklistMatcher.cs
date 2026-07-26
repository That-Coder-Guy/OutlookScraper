using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;

namespace OutlookScraper.Core.Blacklist;

/// <summary>
/// Decides whether a classification matches an existing "stop showing me this" rule.
/// </summary>
/// <remarks>
/// A four-stage cascade — deterministic where determinism is cheap, semantic only
/// where it earns its keep:
///
/// <list type="number">
/// <item><b>Stage 0, category gate.</b> A category mismatch rules the rule out outright.
/// Cheap, and it stops a fraternity pizza rule from ever being compared against a
/// chemistry seminar.</item>
/// <item><b>Stage 1, canonical key.</b> Normalized token keys compared for equality.
/// This is what catches <c>free-pizza-club-meeting</c> vs
/// <c>club-meeting-with-free-pizza</c>, with no model involved.</item>
/// <item><b>Stage 2, token overlap.</b> Jaccard over the same token sets, for the
/// near-misses stage 1 cannot reach.</item>
/// <item><b>Stage 3, embedding cosine.</b> Only runs when an embedding model is
/// installed. Catches true synonyms (<c>boba-social</c> vs <c>bubble-tea-social</c>)
/// that no amount of string manipulation would.</item>
/// </list>
///
/// Stage 3 has two thresholds. Above the strong one a match is treated as certain;
/// between soft and strong the suggestion is still hidden from toasts but is marked
/// soft-suppressed, stays visible in the Suppressed tab, and can be rescued in one
/// click. That band exists because silently swallowing an event the user wanted is by
/// far the worst failure this app can have — much worse than one extra toast.
/// </remarks>
public sealed class HybridBlacklistMatcher(
    BlacklistRepository repository,
    IEmbeddingProvider embeddings,
    BlacklistSettings settings) : IBlacklistMatcher
{
    private readonly BlacklistRepository _repository = repository;
    private readonly IEmbeddingProvider _embeddings = embeddings;
    private readonly BlacklistSettings _settings = settings;

    /// <summary>
    /// Embeddings are float32 but thresholds are doubles, so a similarity that should
    /// land exactly on a threshold arrives ~1e-7 off. Without this tolerance the
    /// behaviour at the boundary is decided by rounding noise rather than by the
    /// configured value.
    /// </summary>
    private const double ThresholdEpsilon = 1e-6;

    public async Task<BlacklistMatch?> MatchAsync(ClassificationResult result, CancellationToken ct)
    {
        var entries = await _repository.GetEnabledAsync(ct);

        if (entries.Count == 0)
        {
            return null;
        }

        var exceptions = await _repository.GetExceptionsAsync(ct);
        var incomingKey = TagNormalizer.Normalize(result.TopicTag);
        var incomingTokens = TagNormalizer.Tokenize(result.TopicTag);

        // Stage 0: only rules in the same category are even candidates.
        var candidates = entries
            .Where(e => string.Equals(e.Category, result.Category, StringComparison.OrdinalIgnoreCase))
            .Where(e => !IsExcepted(exceptions, e.Id, incomingKey))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // Stage 1: exact canonical-key equality.
        foreach (var entry in candidates)
        {
            if (incomingKey.Length > 0 &&
                string.Equals(entry.TopicTagKey, incomingKey, StringComparison.Ordinal))
            {
                return new BlacklistMatch(entry.Id, SuppressStage.Exact, 1.0);
            }
        }

        // Stage 2: token-set overlap. Best match wins, so a strong overlap is not
        // beaten by whichever rule happens to be first in the list.
        BlacklistMatch? bestToken = null;

        foreach (var entry in candidates)
        {
            var score = TokenSimilarity.Jaccard(incomingTokens, TagNormalizer.Tokenize(entry.TopicTag));

            if (score >= _settings.TokenThreshold - ThresholdEpsilon &&
                (bestToken is null || score > bestToken.Score))
            {
                bestToken = new BlacklistMatch(entry.Id, SuppressStage.Tokens, score);
            }
        }

        if (bestToken is not null)
        {
            return bestToken;
        }

        // Stage 3: semantics. Skipped entirely when embeddings are unavailable — that
        // is a functional degradation, not a failure.
        var comparable = candidates
            .Where(e => e.HasUsableEmbedding(_embeddings.ModelName))
            .ToList();

        if (comparable.Count == 0)
        {
            return null;
        }

        var vector = await _embeddings.EmbedAsync(result.EmbeddingInput(), ct);

        if (vector is null)
        {
            return null;
        }

        BlacklistMatch? bestSemantic = null;

        foreach (var entry in comparable)
        {
            var score = VectorMath.Cosine(vector, entry.Embedding);

            if (score < _settings.SemanticSoftThreshold - ThresholdEpsilon)
            {
                continue;
            }

            if (bestSemantic is null || score > bestSemantic.Score)
            {
                var stage = score >= _settings.SemanticStrongThreshold - ThresholdEpsilon
                    ? SuppressStage.SemanticStrong
                    : SuppressStage.SemanticSoft;

                bestSemantic = new BlacklistMatch(entry.Id, stage, score);
            }
        }

        return bestSemantic;
    }

    /// <summary>
    /// Honours a user's earlier "this is not the same thing" rescue, so a rule that
    /// over-matched a tag once cannot quietly re-swallow it.
    /// </summary>
    private static bool IsExcepted(
        IReadOnlyList<BlacklistException> exceptions, Guid tagId, string incomingKey) =>
        exceptions.Any(e =>
            e.TagId == tagId && string.Equals(e.TopicTagKey, incomingKey, StringComparison.Ordinal));
}
