namespace OutlookScraper.Core.Models;

/// <summary>
/// A plain snapshot of an Outlook <c>MailItem</c>, taken on the STA thread and
/// handed out to everything else.
/// </summary>
/// <remarks>
/// This type is the COM boundary. No RCW is ever allowed past it — the mapper reads
/// every property it needs inside the STA invoke, builds one of these, and releases
/// the underlying COM object before returning. That single rule is what keeps Core
/// free of Windows dependencies and testable on Linux.
/// </remarks>
public sealed record RawEmail(
    string EntryId,
    string StoreId,
    string Subject,
    string SenderName,
    string SenderAddress,
    DateTimeOffset ReceivedLocal,
    string MessageClass,
    string PlainBody,
    string? HtmlBody,
    bool IsAutoReply,
    string FolderName)
{
    /// <summary>Normal mail. Anything else (meeting requests, tasks, reports) is skipped.</summary>
    public const string NoteMessageClass = "IPM.Note";

    public bool IsMailItem =>
        MessageClass.StartsWith(NoteMessageClass, StringComparison.OrdinalIgnoreCase);
}
