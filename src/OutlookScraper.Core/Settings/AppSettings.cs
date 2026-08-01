using System.Text.Json.Serialization;

namespace OutlookScraper.Core.Settings;

/// <summary>
/// User-facing configuration, persisted as <c>settings.json</c> rather than in the
/// database — hand-editable, diffable and trivially resettable, which matters a lot
/// when tuning thresholds on a personal tool. The database keeps only
/// machine-managed runtime state (watermarks and the like).
/// </summary>
public sealed class AppSettings
{
    public OllamaSettings Ollama { get; set; } = new();
    public MailSettings Mail { get; set; } = new();
    public CalendarSettings Calendar { get; set; } = new();
    public BlacklistSettings Blacklist { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

public sealed class OllamaSettings
{
    public string BaseUrl { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "llama3.1:8b";

    /// <summary>
    /// Optional. When absent, the blacklist cascade degrades to stages 0–2, which is
    /// a fully functional mode rather than an error.
    /// </summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>
    /// The minimum model confidence that surfaces a suggestion. Exposed in the UI as
    /// a three-item dropdown (low/medium/high), never a slider — see
    /// <see cref="Models.ConfidenceLevel"/> for why.
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.6;

    public int RequestTimeoutSeconds { get; set; } = 90;

    /// <summary>Head characters of the cleaned body sent to the model.</summary>
    public int MaxBodyChars { get; set; } = 4000;

    /// <summary>Tail characters kept as well — event details often sit at the bottom.</summary>
    public int TailBodyChars { get; set; } = 800;

    /// <summary>
    /// Keeps the model resident between emails. This is the real throughput lever on
    /// a backfill: without it Ollama unloads and reloads the model per request.
    /// </summary>
    public string KeepAlive { get; set; } = "10m";

    public int NumCtx { get; set; } = 8192;
}

public sealed class MailSettings
{
    /// <summary>Empty means "the default Inbox".</summary>
    public List<string> WatchedFolders { get; set; } = [];

    /// <summary>
    /// The safety-net sweep. Mandatory, not optional: <c>Items.ItemAdd</c> is
    /// documented not to fire when more than 16 items arrive at once, so events alone
    /// will silently miss bulk deliveries.
    /// </summary>
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>How far back the first run reaches. Later runs use the stored watermark.</summary>
    public int BackfillDays { get; set; } = 7;

    /// <summary>Caps a single sweep so a first run against a huge mailbox cannot stall the STA thread.</summary>
    public int MaxSweepItems { get; set; } = 1000;
}

public sealed class CalendarSettings
{
    /// <summary>
    /// The <c>calendar.events</c> scope cannot enumerate the user's calendar list, so
    /// this is a text field rather than a dropdown. "primary" is what almost everyone
    /// wants, and broadening the OAuth grant just to populate a combo box is a bad trade.
    /// </summary>
    public string CalendarId { get; set; } = "primary";

    public int DefaultDurationMinutes { get; set; } = 60;

    /// <summary>IANA id. Defaults from the OS at first run.</summary>
    public string TimeZone { get; set; } = "";
}

public sealed class BlacklistSettings
{
    /// <summary>Jaccard overlap of normalized token sets at or above this suppresses.</summary>
    public double TokenThreshold { get; set; } = 0.60;

    /// <summary>Cosine at or above this is treated as the same kind of event outright.</summary>
    public double SemanticStrongThreshold { get; set; } = 0.90;

    /// <summary>
    /// The soft band floor. Between this and the strong threshold a suggestion is
    /// hidden from toasts but kept visible and rescuable in the Suppressed tab.
    /// </summary>
    public double SemanticSoftThreshold { get; set; } = 0.82;
}

public sealed class GeneralSettings
{
    public bool RunAtLogin { get; set; } = true;

    /// <summary>Beyond this many toasts per window, arrivals coalesce into one summary.</summary>
    public int MaxToastsPerWindow { get; set; } = 3;

    public int ToastWindowMinutes { get; set; } = 10;

    public int RetentionDays { get; set; } = 180;

    /// <summary>
    /// Writes Debug-level detail to the log: per-message body sizes, the model's
    /// reasoning, queue depths, and why the sweep watermark is or is not advancing.
    /// Off by default because it is noisy; the per-message outcome lines are logged at
    /// Information either way.
    /// </summary>
    public bool VerboseLogging { get; set; }

    /// <summary>Raw model output is nulled out this many days after classification.</summary>
    public int RawJsonRetentionDays { get; set; } = 30;

    [JsonIgnore]
    public TimeSpan ToastWindow => TimeSpan.FromMinutes(ToastWindowMinutes);
}
