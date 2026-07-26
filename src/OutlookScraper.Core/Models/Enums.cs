namespace OutlookScraper.Core.Models;

/// <summary>
/// Why a message never reached the model. These are all *structural* skips — there
/// is deliberately no keyword pre-filter, because "refreshments provided",
/// "catered" and "we'll feed you" all evade a naive food regex and recall is the
/// entire reason for using an LLM here.
/// </summary>
public enum SkipReason
{
    None = 0,
    NotMailItem,
    AutoReply,
    BodyTooShort,
    DuplicateBody,
}

/// <summary>Processing state of a single Outlook message.</summary>
public enum ProcessedStatus
{
    Queued = 0,
    Classified,
    Skipped,
    Failed,
}

/// <summary>What the user did (or the matcher did) with a detected event.</summary>
public enum SuggestionState
{
    Pending = 0,
    Added,
    Blacklisted,
    Dismissed,
    Suppressed,
}

/// <summary>
/// The model's own confidence. Deliberately three-valued: LLM-emitted numeric
/// confidence is badly calibrated and clusters around 0.9, whereas a three-way
/// choice is something models actually do well.
/// </summary>
public enum ConfidenceLevel
{
    Low = 0,
    Medium,
    High,
}

/// <summary>Which stage of the blacklist cascade produced a match.</summary>
public enum SuppressStage
{
    None = 0,

    /// <summary>Normalized canonical keys were identical.</summary>
    Exact,

    /// <summary>Jaccard token overlap cleared the threshold.</summary>
    Tokens,

    /// <summary>Embedding cosine at or above the strong threshold.</summary>
    SemanticStrong,

    /// <summary>Embedding cosine in the soft band — suppressed, but recoverable.</summary>
    SemanticSoft,
}

/// <summary>Connection state of the mail source, surfaced on the tray icon.</summary>
public enum MailSourceState
{
    Disconnected = 0,
    WaitingForHost,
    Connecting,
    Connected,
    Faulted,
}

/// <summary>Health of the local Ollama server.</summary>
public enum OllamaHealth
{
    Unknown = 0,
    Healthy,

    /// <summary>Server is up but the configured model is not pulled.</summary>
    ModelMissing,
    Unreachable,
}

/// <summary>Live mail preempts backfill so a large sweep never delays a new arrival.</summary>
public enum ProcessingPriority
{
    Live = 0,
    Backfill,
}

public static class ConfidenceLevelExtensions
{
    /// <summary>
    /// Maps the model's three-way confidence onto the numeric threshold the user
    /// configures. The UI exposes this as a three-item dropdown, never a slider.
    /// </summary>
    public static double ToScore(this ConfidenceLevel level) => level switch
    {
        ConfidenceLevel.High => 0.9,
        ConfidenceLevel.Medium => 0.6,
        _ => 0.3,
    };

    public static bool TryParse(string? value, out ConfidenceLevel level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "high": level = ConfidenceLevel.High; return true;
            case "medium": level = ConfidenceLevel.Medium; return true;
            case "low": level = ConfidenceLevel.Low; return true;
            default: level = ConfidenceLevel.Low; return false;
        }
    }
}
