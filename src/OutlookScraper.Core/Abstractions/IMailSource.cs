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
    event EventHandler<RawEmail>? MailArrived;
    event EventHandler<MailSourceState>? StateChanged;

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
