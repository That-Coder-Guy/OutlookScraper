using System.Globalization;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;

namespace OutlookScraper.Core.Time;

/// <summary>
/// Turns the model's naive local datetime strings into real instants.
/// </summary>
/// <remarks>
/// The model is instructed to emit local wall-clock time with no offset, precisely so
/// that this conversion happens here where DST and the user's configured zone are
/// known. Trusting a model to do timezone arithmetic is a reliable way to book events
/// an hour off twice a year.
/// </remarks>
public sealed class EventTimeResolver(CalendarSettings settings)
{
    private readonly CalendarSettings _settings = settings;

    /// <summary>
    /// Beyond this, a "resolved" date is almost certainly a hallucination or a parse of
    /// something that was not a date at all.
    /// </summary>
    public const int MaxDaysAhead = 180;

    /// <summary>Accepted shapes, in preference order. Anything else is rejected.</summary>
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
    ];

    private static readonly string[] DateOnlyFormats =
    [
        "yyyy-MM-dd",
    ];

    public ResolutionOutcome Resolve(ClassificationResult result, DateTimeOffset receivedLocal)
    {
        var ianaId = string.IsNullOrWhiteSpace(_settings.TimeZone)
            ? TimeZoneResolution.LocalIanaId()
            : _settings.TimeZone;

        var zone = TimeZoneResolution.Resolve(ianaId);

        if (!TryParseNaive(result.StartLocal, out var start, out var startWasDateOnly))
        {
            return ResolutionOutcome.Failed(
                string.IsNullOrWhiteSpace(result.StartLocal)
                    ? DateResolutionProblem.Missing
                    : DateResolutionProblem.Unparseable,
                ianaId);
        }

        var isAllDay = result.IsAllDay || startWasDateOnly;
        var startOffset = TimeZoneResolution.ToOffset(start, zone);

        // A stated end that lands before the start is nonsense; fall back to the
        // default duration rather than booking a negative-length event.
        DateTimeOffset endOffset;

        if (TryParseNaive(result.EndLocal, out var end, out _))
        {
            endOffset = TimeZoneResolution.ToOffset(end, zone);

            if (endOffset <= startOffset)
            {
                endOffset = startOffset.AddMinutes(_settings.DefaultDurationMinutes);
            }
        }
        else
        {
            endOffset = isAllDay
                ? startOffset.AddDays(1)
                : startOffset.AddMinutes(_settings.DefaultDurationMinutes);
        }

        // Sanity-check against the email's own received time, not against "now" — a
        // backfill legitimately processes old mail, and judging it against the present
        // would reject every historical message.
        var reference = receivedLocal;

        if (startOffset < reference.AddDays(-1))
        {
            return ResolutionOutcome.Failed(DateResolutionProblem.InPast, ianaId);
        }

        if (startOffset > reference.AddDays(MaxDaysAhead))
        {
            return ResolutionOutcome.Failed(DateResolutionProblem.TooFarOut, ianaId);
        }

        return ResolutionOutcome.Succeeded(
            new ResolvedEventTime(startOffset, endOffset, ianaId, isAllDay));
    }

    internal static bool TryParseNaive(string? value, out DateTime parsed, out bool wasDateOnly)
    {
        parsed = default;
        wasDateOnly = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        if (DateTime.TryParseExact(
                trimmed,
                DateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed))
        {
            return true;
        }

        if (DateTime.TryParseExact(
                trimmed,
                DateOnlyFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed))
        {
            wasDateOnly = true;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Either a usable time or the reason there is not one. A failure is not fatal: the
/// suggestion still surfaces so the user can read the email and fill the date in — it
/// just cannot be booked as-is.
/// </summary>
public sealed record ResolutionOutcome(
    ResolvedEventTime? Time,
    DateResolutionProblem Problem,
    string IanaTimeZone)
{
    public bool IsResolved => Time is not null;

    public static ResolutionOutcome Succeeded(ResolvedEventTime time) =>
        new(time, DateResolutionProblem.None, time.IanaTimeZone);

    public static ResolutionOutcome Failed(DateResolutionProblem problem, string ianaId) =>
        new(null, problem, ianaId);
}
