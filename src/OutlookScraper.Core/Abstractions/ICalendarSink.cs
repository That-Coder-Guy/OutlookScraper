using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Abstractions;

/// <summary>Where accepted events get written.</summary>
public interface ICalendarSink
{
    Task<CalendarInsertResult> AddAsync(EventSuggestion suggestion, string calendarId, CancellationToken ct);

    Task<bool> RemoveAsync(Guid suggestionId, CancellationToken ct);
}

/// <summary>
/// The result of booking an event.
/// </summary>
/// <param name="AlreadyExisted">
/// True when the deterministic event id collided with an existing event. That is a
/// success, not a failure — it is exactly the double-booking guard doing its job,
/// and it survives database loss and reinstalls in a way a local check cannot.
/// </param>
public sealed record CalendarInsertResult(string EventId, string? HtmlLink, bool AlreadyExisted);

/// <summary>
/// Raises toasts and status messages. Implemented in the WPF shell; abstracted here
/// so that swapping the (formally superseded) notification package later touches one
/// file, and so the pipeline stays testable without Windows.
/// </summary>
public interface INotifier
{
    Task ShowSuggestionAsync(EventSuggestion suggestion);

    /// <summary>Used at the end of a backfill, and when toasts are being rate-limited.</summary>
    Task ShowSummaryAsync(int count);

    Task ShowStatusAsync(string title, string body);

    /// <summary>Clears a toast once its suggestion has been resolved elsewhere.</summary>
    Task RemoveAsync(Guid suggestionId);
}

/// <summary>Encrypt-at-rest seam. Backed by DPAPI on Windows, pass-through in tests.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Injected so scheduling, retention and the circuit breaker are testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
