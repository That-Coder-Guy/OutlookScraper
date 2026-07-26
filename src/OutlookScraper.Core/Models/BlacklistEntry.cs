namespace OutlookScraper.Core.Models;

/// <summary>
/// One "stop suggesting this kind of thing" rule, keyed on the model's topic tag.
/// </summary>
/// <remarks>
/// <see cref="EmbedModel"/> and <see cref="EmbedDim"/> are stored per row on purpose:
/// vectors from different embedding models are not comparable, so when the configured
/// model changes, stage 3 is skipped for stale rows (stages 0–2 still apply) and they
/// get queued for re-embedding rather than silently producing nonsense similarities.
/// </remarks>
public sealed class BlacklistEntry
{
    public required Guid Id { get; init; }

    /// <summary>Stage 0 gate — a category mismatch rules out a match outright.</summary>
    public required string Category { get; init; }

    public required string TopicTag { get; init; }

    /// <summary>Normalized canonical form; stage 1 compares these for equality.</summary>
    public required string TopicTagKey { get; init; }

    public string Reason { get; init; } = "";

    /// <summary>Null when no embedding model was installed at the time. Stages 0–2 still work.</summary>
    public float[]? Embedding { get; set; }

    public string? EmbedModel { get; set; }
    public int? EmbedDim { get; set; }

    public string? SourceEntryId { get; init; }
    public bool Enabled { get; set; } = true;

    /// <summary>How many suggestions this rule has suppressed; shown in the blacklist manager.</summary>
    public int HitCount { get; set; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool HasUsableEmbedding(string currentModel) =>
        Embedding is { Length: > 0 } &&
        string.Equals(EmbedModel, currentModel, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A user override rescuing one topic tag from a rule that over-matched it. Written
/// when someone hits "not the same thing" on a soft-suppressed suggestion.
/// </summary>
public sealed record BlacklistException(Guid TagId, string TopicTagKey, DateTimeOffset CreatedUtc);

/// <summary>The outcome of running a classification past the blacklist cascade.</summary>
public sealed record BlacklistMatch(
    Guid TagId,
    SuppressStage Stage,
    double Score)
{
    public bool IsSoft => Stage == SuppressStage.SemanticSoft;
}
