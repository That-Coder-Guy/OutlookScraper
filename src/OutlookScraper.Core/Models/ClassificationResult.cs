namespace OutlookScraper.Core.Models;

/// <summary>
/// The model's verdict on one email, after JSON deserialization and validation.
/// </summary>
/// <remarks>
/// Every field in the wire schema is <c>required</c> with <c>""</c> as the "unknown"
/// sentinel rather than <c>null</c>. Ollama compiles the schema down to a GBNF
/// grammar, and optional properties get omitted unpredictably while
/// <c>anyOf</c>/type-arrays have patchy support — all-required-with-sentinels is the
/// shape that actually survives.
/// </remarks>
public sealed record ClassificationResult
{
    public required bool IsEvent { get; init; }
    public required bool HasFreeFood { get; init; }
    public required ConfidenceLevel Confidence { get; init; }

    public string Title { get; init; } = "";
    public string FoodDescription { get; init; } = "";
    public string Location { get; init; } = "";
    public string Organization { get; init; } = "";

    /// <summary>Naive local time as <c>yyyy-MM-ddTHH:mm</c>, or empty. Never carries an offset.</summary>
    public string StartLocal { get; init; } = "";

    /// <summary>Naive local time as <c>yyyy-MM-ddTHH:mm</c>, or empty. Never carries an offset.</summary>
    public string EndLocal { get; init; } = "";

    public bool IsAllDay { get; init; }

    /// <summary>
    /// True only when the email literally states the date. Lets the review window
    /// flag a guessed date before it gets booked onto a real calendar.
    /// </summary>
    public bool DateIsExplicit { get; init; }

    public string Category { get; init; } = EventCategory.Other;

    /// <summary>
    /// A kebab-case tag describing the recurring *type* of event, not this instance.
    /// The prompt forbids dates, names and room numbers here — without that, you get
    /// <c>sigma-chi-rush-oct-14-pizza</c>, which never matches anything again and
    /// makes blacklisting useless.
    /// </summary>
    public string TopicTag { get; init; } = "";

    /// <summary>
    /// One sentence explaining the call. Also the highest-signal part of the string
    /// that gets embedded for semantic blacklist matching — a bare three-token
    /// kebab tag carries far too little for a useful vector.
    /// </summary>
    public string Reason { get; init; } = "";

    /// <summary>
    /// The gate for surfacing anything to the user at all.
    /// </summary>
    public bool QualifiesAsFreeFoodEvent(double confidenceThreshold) =>
        IsEvent && HasFreeFood && Confidence.ToScore() >= confidenceThreshold;

    /// <summary>
    /// The text handed to the embedding model. Category and reason are what give the
    /// vector real content; the tag alone is too short to separate cleanly.
    /// </summary>
    public string EmbeddingInput() =>
        $"{Category}. {TopicTag.Replace('-', ' ')}. {Reason}".Trim();
}
