using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Abstractions;

namespace OutlookScraper.Core.Ollama;

/// <summary>
/// Embeddings for semantic blacklist matching.
/// </summary>
/// <remarks>
/// Entirely optional. If no embedding model is installed this reports unavailable and
/// the cascade runs stages 0–2, which still catches the common case of one listserv
/// sending near-identical mail repeatedly. Nothing here throws for that condition —
/// an absent embedding model is a configuration state, not an error.
/// </remarks>
public sealed class OllamaEmbeddingProvider(
    OllamaClient client,
    string modelName,
    ILogger<OllamaEmbeddingProvider>? logger = null) : IEmbeddingProvider
{
    private readonly OllamaClient _client = client;
    private readonly ILogger<OllamaEmbeddingProvider>? _logger = logger;

    public string ModelName { get; } = modelName;

    /// <summary>
    /// <c>nomic-embed-text</c> was trained with <c>search_document:</c> /
    /// <c>search_query:</c> task prefixes. Blacklist matching compares one stored
    /// description against another, so both sides get the *same* document prefix —
    /// being consistent matters far more than which prefix is chosen.
    /// </summary>
    private const string DocumentPrefix = "search_document: ";

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ModelName))
        {
            return false;
        }

        try
        {
            var models = await _client.ListModelsAsync(ct);

            return models.Any(m =>
                m.Equals(ModelName, StringComparison.OrdinalIgnoreCase) ||
                m.StartsWith(ModelName + ":", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OllamaException)
        {
            return false;
        }
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ModelName) || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return await _client.EmbedAsync(ModelName, DocumentPrefix + text, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OllamaException)
        {
            // Never let an embedding failure block a user action. The caller stores a
            // null embedding and queues the row for a later retry.
            _logger?.LogDebug(ex, "Embedding unavailable; falling back to token matching.");
            return null;
        }
    }
}
