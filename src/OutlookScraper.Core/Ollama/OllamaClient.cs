using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OutlookScraper.Core.Ollama;

/// <summary>
/// Thin HTTP wrapper over the local Ollama server.
/// </summary>
/// <remarks>
/// Deliberately dumb: it speaks the wire format and nothing else. Prompt assembly,
/// schema construction, validation and retry all live a layer up in
/// <see cref="OllamaClassifier"/>, which keeps this class trivially fake-able in tests.
/// </remarks>
public sealed class OllamaClient(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Runs a chat completion constrained by <paramref name="schema"/>.
    /// </summary>
    /// <remarks>
    /// <c>keep_alive</c> is the real throughput lever on a backfill: without it Ollama
    /// unloads the model between requests and every email pays the reload cost.
    /// </remarks>
    public async Task<string> ChatAsync(
        string model,
        string systemPrompt,
        string userMessage,
        JsonNode? schema,
        string keepAlive,
        int numCtx,
        CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["keep_alive"] = keepAlive,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userMessage }),
            ["options"] = new JsonObject
            {
                // Classification is an extraction task; sampling only adds variance.
                ["temperature"] = 0,
                ["num_ctx"] = numCtx,
            },
        };

        if (schema is not null)
        {
            // A deep clone is required: a JsonNode cannot be parented twice, and the
            // schema instance is cached and reused across every request.
            request["format"] = schema.DeepClone();
        }

        using var response = await _httpClient.PostAsJsonAsync("/api/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, ct)
                      ?? throw new OllamaException("Ollama returned an empty chat response.");

        return payload.Message?.Content
               ?? throw new OllamaException("Ollama chat response contained no message content.");
    }

    /// <summary>Embeds a single string. Returns null when the model produced nothing usable.</summary>
    public async Task<float[]?> EmbedAsync(string model, string input, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["input"] = input,
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/embed", request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(JsonOptions, ct);

        return payload?.Embeddings is { Count: > 0 } embeddings ? embeddings[0] : null;
    }

    /// <summary>Locally installed models. Also doubles as the health probe.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync("/api/tags", ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TagsResponse>(JsonOptions, ct);

        return payload?.Models?.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).ToList()
               ?? [];
    }

    private sealed record ChatResponse
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; init; }
    }

    private sealed record ChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed record EmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]>? Embeddings { get; init; }
    }

    private sealed record TagsResponse
    {
        [JsonPropertyName("models")]
        public List<TagModel>? Models { get; init; }
    }

    private sealed record TagModel
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";
    }
}

public sealed class OllamaException(string message, Exception? inner = null)
    : Exception(message, inner);
