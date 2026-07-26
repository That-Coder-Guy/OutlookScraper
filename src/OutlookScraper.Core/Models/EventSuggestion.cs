namespace OutlookScraper.Core.Models;

/// <summary>
/// A detected free-food event awaiting the user's decision.
/// </summary>
/// <remarks>
/// The <see cref="Id"/> is the toast correlation key, and it is the *only* thing put
/// into the toast payload — everything else is looked up from SQLite when the button
/// is pressed. That is deliberate: a cold-start activation has no in-memory state, so
/// resolving against the database makes hot and cold activation the identical code
/// path. It is also why a suggestion must be persisted before its toast is shown.
/// </remarks>
public sealed class EventSuggestion
{
    public required Guid Id { get; init; }
    public required string EntryId { get; init; }

    public string Title { get; set; } = "";
    public string FoodDescription { get; set; } = "";
    public string Location { get; set; } = "";
    public string Organization { get; set; } = "";

    public DateTimeOffset? StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public string IanaTimeZone { get; set; } = "UTC";
    public bool IsAllDay { get; set; }

    /// <summary>The model admitted to guessing the date, so the UI asks for a glance.</summary>
    public bool DateIsExplicit { get; set; }

    /// <summary>No usable date came back; the user must supply one before booking.</summary>
    public bool NeedsDateReview { get; set; }

    public string Category { get; init; } = EventCategory.Other;
    public string TopicTag { get; init; } = "";

    /// <summary>Normalized canonical form of <see cref="TopicTag"/>; stages 1 and 2 match on this.</summary>
    public string TopicTagKey { get; init; } = "";

    public string Reason { get; init; } = "";
    public double Confidence { get; init; }

    /// <summary>Sender details are kept for display and debugging only — never for matching.</summary>
    public string SenderName { get; init; } = "";
    public string SenderAddress { get; init; } = "";
    public string Subject { get; init; } = "";
    public string BodyExcerpt { get; init; } = "";

    public SuggestionState State { get; set; } = SuggestionState.Pending;

    // Suppression audit trail. Without these a bad threshold is invisible and
    // undiagnosable, and silently swallowing an event the user wanted is the worst
    // failure this app has.
    public Guid? SuppressedByTagId { get; set; }
    public SuppressStage SuppressStage { get; set; } = SuppressStage.None;
    public double? SuppressScore { get; set; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedUtc { get; set; }

    /// <summary>
    /// Soft-suppressed items skip the toast but still appear in the Suppressed tab
    /// with a one-click rescue, because the semantic band is the part most likely to
    /// be wrong.
    /// </summary>
    public bool IsSoftSuppressed =>
        State == SuggestionState.Suppressed && SuppressStage == SuppressStage.SemanticSoft;

    public bool CanAddToCalendar => StartUtc.HasValue && !NeedsDateReview;
}
