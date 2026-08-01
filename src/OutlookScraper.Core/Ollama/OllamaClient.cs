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
        await EnsureSuccessAsync(response, model, ct);

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
        await EnsureSuccessAsync(response, model, ct);

        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(JsonOptions, ct);

        return payload?.Embeddings is { Count: > 0 } embeddings ? embeddings[0] : null;
    }

    /// <summary>
    /// Turns a failed response into an exception that names the actual problem.
    /// </summary>
    /// <remarks>
    /// Ollama answers a request for a model it does not have with a bare 404, and
    /// <c>EnsureSuccessStatusCode</c> renders that as "Response status code does not
    /// indicate success: 404 (Not Found)" — which tells the user nothing, even though
    /// the fix is a single command. The model name is known here, so the message may as
    /// well contain the command that resolves it.
    /// </remarks>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string model, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await SafeReadAsync(response, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new OllamaException(
                $"Ollama does not have the model '{model}'. Install it with:  ollama pull {model}" +
                (string.IsNullOrWhiteSpace(body) ? "" : $"  (server said: {body})"));
        }

        throw new OllamaException(
            $"Ollama returned {(int)response.StatusCode} ({response.ReasonPhrase}) for model '{model}'." +
            (string.IsNullOrWhiteSpace(body) ? "" : $" {body}"));
    }

    /// <summary>Reads the error body for context; never masks the original failure.</summary>
    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            return body.Length > 300 ? body[..300] : body;
        }
        catch (Exception)
        {
            return "";
        }
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
