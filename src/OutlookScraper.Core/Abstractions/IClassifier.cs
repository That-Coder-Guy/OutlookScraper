using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Abstractions;

/// <summary>Decides whether an email describes a campus event with free food.</summary>
public interface IClassifier
{
    Task<ClassificationResult> ClassifyAsync(CleanedEmail email, CancellationToken ct);
}

/// <summary>
/// Produces vectors for semantic blacklist matching.
/// </summary>
/// <remarks>
/// Optional by design. When no embedding model is installed the cascade runs stages
/// 0–2 only, which still handles the common case of one listserv sending near-identical
/// mail over and over.
/// </remarks>
public interface IEmbeddingProvider
{
    string ModelName { get; }

    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>Returns null when embeddings are unavailable — never throws for that case.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct);
}

/// <summary>Runs a classification past the stored blacklist rules.</summary>
public interface IBlacklistMatcher
{
    Task<BlacklistMatch?> MatchAsync(ClassificationResult result, CancellationToken ct);
}
