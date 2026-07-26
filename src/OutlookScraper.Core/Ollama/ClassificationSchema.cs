using System.Text.Json;
using System.Text.Json.Nodes;
using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Ollama;

/// <summary>
/// The JSON Schema handed to Ollama's <c>format</c> parameter to constrain output.
/// </summary>
/// <remarks>
/// Several choices here are deliberate and easy to "helpfully" break later:
///
/// <list type="bullet">
/// <item>
/// <b>Every property is required, and "unknown" is <c>""</c> rather than null.</b>
/// Ollama compiles this schema into a GBNF grammar. Optional properties get omitted
/// unpredictably and <c>anyOf</c>/type-arrays have patchy support, so
/// all-required-with-sentinels is the only shape that reliably round-trips.
/// </item>
/// <item>
/// <b>No <c>pattern</c> on the datetime fields.</b> Grammar-level regex support is
/// unreliable; the datetimes are validated and repaired in C# instead.
/// </item>
/// <item>
/// <b><c>confidence</c> is an enum, not a number.</b> Models are badly calibrated at
/// emitting numeric confidence and cluster everything around 0.9; a three-way choice
/// is something they actually do well.
/// </item>
/// <item>
/// <b><c>topic_tag</c> describes the recurring type, not the instance.</b> This is the
/// single instruction that makes blacklisting generalize — without it the model emits
/// tags like <c>sigma-chi-rush-oct-14-pizza</c>, which never match anything again.
/// </item>
/// </list>
/// </remarks>
public static class ClassificationSchema
{
    public static JsonNode Build()
    {
        var categories = new JsonArray();

        foreach (var category in EventCategory.All)
        {
            // JsonValue.Create, not Add(category): the generic Add<T> overload produces
            // a JsonValueCustomized that cannot be serialized without a TypeInfoResolver.
            categories.Add(JsonValue.Create(category));
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(
                "is_event", "has_free_food", "confidence", "title", "food_description",
                "location", "organization", "start_local", "end_local", "is_all_day",
                "date_is_explicit", "category", "topic_tag", "reason"),
            ["properties"] = new JsonObject
            {
                ["is_event"] = Property(
                    "boolean",
                    "True if the email announces a specific gathering that people can attend."),

                ["has_free_food"] = Property(
                    "boolean",
                    "True if food or drink is provided at no cost to attendees. Includes snacks, " +
                    "refreshments, catering, pizza, boba, coffee, donuts, and phrases like " +
                    "'lunch provided' or 'we'll feed you'. False if food must be purchased, is " +
                    "discounted rather than free, is free only with a purchase, or is merely " +
                    "mentioned in passing."),

                ["confidence"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("low", "medium", "high"),
                    ["description"] = "How certain you are about is_event and has_free_food together.",
                },

                ["title"] = Property(
                    "string",
                    "Short event title, at most 80 characters. Empty string if unknown."),

                ["food_description"] = Property(
                    "string",
                    "What food is offered, e.g. 'free pizza and soda'. Empty string if unknown."),

                ["location"] = Property(
                    "string",
                    "Room, building, or address exactly as stated. Empty string if unknown."),

                ["organization"] = Property(
                    "string",
                    "The hosting club, department, or group. Empty string if unknown."),

                ["start_local"] = Property(
                    "string",
                    "Local start time as YYYY-MM-DDTHH:MM with NO timezone and NO offset. " +
                    "Empty string if the email does not state a date and time."),

                ["end_local"] = Property(
                    "string",
                    "Local end time as YYYY-MM-DDTHH:MM with NO timezone and NO offset. " +
                    "Empty string if not stated."),

                ["is_all_day"] = Property(
                    "boolean",
                    "True if the event has no specific start time."),

                ["date_is_explicit"] = Property(
                    "boolean",
                    "True only if the email literally states the date. False if you inferred, " +
                    "guessed, or calculated it."),

                ["category"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = categories,
                    ["description"] = "The kind of campus group or occasion this belongs to.",
                },

                ["topic_tag"] = Property(
                    "string",
                    "A 2-5 word lowercase kebab-case tag describing the RECURRING TYPE of this " +
                    "event, not this specific instance. No dates, no proper names, no room " +
                    "numbers. Example: fraternity-recruitment-pizza"),

                ["reason"] = Property(
                    "string",
                    "One sentence, at most 200 characters, explaining the classification."),
            },
        };
    }

    private static JsonObject Property(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    /// <summary>Cached serialized form — the schema is constant across requests.</summary>
    public static JsonNode Instance { get; } = Build();

    public static string ToJson() => Instance.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
    });
}
