namespace OutlookScraper.Core.Models;

/// <summary>
/// A concrete event time, resolved from the model's naive local string against the
/// email's received time and the user's configured zone.
/// </summary>
public sealed record ResolvedEventTime(
    DateTimeOffset Start,
    DateTimeOffset End,
    string IanaTimeZone,
    bool IsAllDay)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Why a date could not be pinned down. The suggestion still surfaces — the user can
/// fill the date in from the email text — it just cannot be booked as-is.
/// </summary>
public enum DateResolutionProblem
{
    None = 0,
    Missing,
    Unparseable,
    InPast,
    TooFarOut,
}
