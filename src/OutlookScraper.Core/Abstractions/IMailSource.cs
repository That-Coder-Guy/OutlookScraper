using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Abstractions;

/// <summary>
/// Where mail comes from. Implemented today only by the Outlook COM adapter.
/// </summary>
/// <remarks>
/// This seam exists because classic Outlook is on a clock: Microsoft is migrating
/// users to the new Outlook client, which removed COM entirely. When that becomes a
/// problem, a Microsoft Graph implementation drops in behind this interface without
/// the pipeline noticing. That is the whole extent of the hedge — the Graph backend
/// is deliberately not being built now.
///
/// Note the return types: <see cref="RawEmail"/> only, never a COM object. Everything
/// on the far side of this interface stays platform-agnostic.
/// </remarks>
public interface IMailSource : IAsyncDisposable
{
    /// <summary>
    /// Raised with an EntryID as soon as the host reports new mail — deliberately the
    /// id alone, not the message.
    /// </summary>
    /// <remarks>
    /// Outlook raises its events on its own UI thread, and a slow handler visibly
    /// freezes the user's Outlook. So the handler reads one property and returns, and
    /// the worker re-opens the message by id when it is ready to do the expensive work.
    /// </remarks>
    event EventHandler<string>? EntryIdArrived;

    event EventHandler<MailSourceState>? StateChanged;

    /// <summary>
    /// Raised after reconnecting, so the host can sweep for whatever arrived while the
    /// connection was down.
    /// </summary>
    event EventHandler? Reconnected;

    MailSourceState State { get; }

    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Pulls everything received since <paramref name="sinceLocal"/>, capped at
    /// <paramref name="max"/>. Used for the first-run backfill, the periodic safety-net
    /// sweep, and the catch-up after a reconnect.
    /// </summary>
    Task<IReadOnlyList<RawEmail>> SweepAsync(DateTimeOffset sinceLocal, int max, CancellationToken ct);

    /// <summary>Re-reads a single message. Returns null if it has since been moved or deleted.</summary>
    Task<RawEmail?> GetByEntryIdAsync(string entryId, CancellationToken ct);
}
