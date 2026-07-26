namespace OutlookScraper.Core.Models;

/// <summary>
/// Per-message processing record, keyed on the Outlook <c>EntryID</c>.
/// </summary>
/// <remarks>
/// This table is the idempotency backbone. New mail can arrive via three independent
/// paths — <c>Items.ItemAdd</c>, <c>Application.NewMailEx</c>, and the periodic
/// <c>Restrict</c> sweep — and all three are allowed to deliver the same message,
/// because the insert here is what decides whether any work actually happens.
/// </remarks>
public sealed class ProcessedMessage
{
    public required string EntryId { get; init; }
    public required string StoreId { get; init; }

    /// <summary>SHA-256 of the cleaned body. Catches listserv resends with a new EntryID.</summary>
    public string BodyHash { get; set; } = "";

    public string Subject { get; set; } = "";

    /// <summary>Display and debugging only. Never used for blacklist matching.</summary>
    public string SenderAddress { get; set; } = "";

    public string SenderName { get; set; } = "";
    public DateTimeOffset ReceivedUtc { get; set; }

    public ProcessedStatus Status { get; set; } = ProcessedStatus.Queued;
    public SkipReason SkipReason { get; set; } = SkipReason.None;

    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public DateTimeOffset? ClassifiedUtc { get; set; }
    public string? ModelName { get; set; }

    /// <summary>Raw model output, kept for prompt tuning and nulled out by the retention job.</summary>
    public string? RawLlmJson { get; set; }

    public bool IsTerminal =>
        Status is ProcessedStatus.Classified or ProcessedStatus.Skipped;
}
