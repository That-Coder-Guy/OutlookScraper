using System.Text.Json;
using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;

namespace OutlookScraper.Core.Ollama;

/// <summary>
/// Classifies an email by prompting a local model with a schema-constrained request,
/// then validating and repairing whatever comes back.
/// </summary>
public sealed class OllamaClassifier(
    OllamaClient client,
    OllamaSettings settings,
    PromptBuilder promptBuilder,
    IClock clock,
    ILogger<OllamaClassifier>? logger = null) : IClassifier
{
    private readonly OllamaClient _client = client;
    private readonly OllamaSettings _settings = settings;
    private readonly PromptBuilder _promptBuilder = promptBuilder;
    private readonly IClock _clock = clock;
    private readonly ILogger<OllamaClassifier>? _logger = logger;

    /// <summary>The raw model output of the last call, kept for prompt tuning.</summary>
    public string? LastRawJson { get; private set; }

    public async Task<ClassificationResult> ClassifyAsync(CleanedEmail email, CancellationToken ct)
    {
        var userMessage = _promptBuilder.BuildUserMessage(email, _clock.UtcNow);

        var raw = await _client.ChatAsync(
            _settings.Model,
            PromptBuilder.SystemPrompt,
            userMessage,
            ClassificationSchema.Instance,
            _settings.KeepAlive,
            _settings.NumCtx,
            ct);

        LastRawJson = raw;

        if (TryParse(raw, out var result))
        {
            return result;
        }

        // One nudge, then give up. Grammar-constrained output should not be malformed,
        // but a truncated response or a context overflow can still produce garbage, and
        // retrying forever on a poison message would stall the queue.
        _logger?.LogWarning(
            "Malformed classification JSON for {EntryId}; retrying once.", email.EntryId);

        var retryMessage = userMessage +
            "\n\nYour previous response was not valid JSON matching the required schema. " +
            "Respond with the JSON object only.";

        raw = await _client.ChatAsync(
            _settings.Model,
            PromptBuilder.SystemPrompt,
            retryMessage,
            ClassificationSchema.Instance,
            _settings.KeepAlive,
            _settings.NumCtx,
            ct);

        LastRawJson = raw;

        if (TryParse(raw, out result))
        {
            return result;
        }

        throw new OllamaException(
            $"Model did not return schema-valid JSON for message {email.EntryId} after a retry.");
    }

    /// <summary>
    /// Parses and sanitizes model output. Anything structurally present but nonsensical
    /// (an unknown category, an out-of-range confidence) is coerced rather than
    /// rejected — the alternative is discarding an otherwise good classification over a
    /// single bad field.
    /// </summary>
    internal static bool TryParse(string? raw, out ClassificationResult result)
    {
        result = null!;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJsonObject(raw));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // is_event and has_free_food are the two fields the whole pipeline turns on;
            // a response missing either is not usable.
            if (!TryGetBool(root, "is_event", out var isEvent) ||
                !TryGetBool(root, "has_free_food", out var hasFreeFood))
            {
                return false;
            }

            ConfidenceLevelExtensions.TryParse(GetString(root, "confidence"), out var confidence);

            result = new ClassificationResult
            {
                IsEvent = isEvent,
                HasFreeFood = hasFreeFood,
                Confidence = confidence,
                Title = Trim(GetString(root, "title"), 200),
                FoodDescription = Trim(GetString(root, "food_description"), 200),
                Location = Trim(GetString(root, "location"), 200),
                Organization = Trim(GetString(root, "organization"), 200),
                StartLocal = GetString(root, "start_local").Trim(),
                EndLocal = GetString(root, "end_local").Trim(),
                IsAllDay = TryGetBool(root, "is_all_day", out var allDay) && allDay,
                DateIsExplicit = TryGetBool(root, "date_is_explicit", out var explicitDate) && explicitDate,
                Category = EventCategory.Normalize(GetString(root, "category")),
                TopicTag = Trim(GetString(root, "topic_tag"), 100),
                Reason = Trim(GetString(root, "reason"), 400),
            };

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pulls the outermost JSON object out of a response. Grammar-constrained output is
    /// normally bare JSON, but a model that ignores the grammar tends to wrap it in
    /// prose or a markdown fence.
    /// </summary>
    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');

        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }

    private static bool TryGetBool(JsonElement root, string name, out bool value)
    {
        value = false;

        if (!root.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String:
                // Some models emit "true"/"false" as strings despite the grammar.
                return bool.TryParse(property.GetString(), out value);
            default:
                return false;
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value.Trim() : value[..maxLength].Trim();
}
