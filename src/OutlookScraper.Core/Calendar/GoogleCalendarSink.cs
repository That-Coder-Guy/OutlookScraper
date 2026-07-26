using System.Net;
using Google;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Storage;

namespace OutlookScraper.Core.Calendar;

/// <summary>Books accepted suggestions onto Google Calendar, exactly once.</summary>
public sealed class GoogleCalendarSink(
    GoogleAuthenticator authenticator,
    CalendarMapRepository map,
    IClock clock,
    ILogger<GoogleCalendarSink>? logger = null) : ICalendarSink
{
    private readonly GoogleAuthenticator _authenticator = authenticator;
    private readonly CalendarMapRepository _map = map;
    private readonly IClock _clock = clock;
    private readonly ILogger<GoogleCalendarSink>? _logger = logger;

    /// <summary>
    /// Serializes per-suggestion work. The toast button and the review window button
    /// are two independent paths to the same action, and a user can easily hit both.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How much of the email body to attach. The full text does not belong on a cloud calendar.</summary>
    private const int MaxDescriptionChars = 500;

    public async Task<CalendarInsertResult> AddAsync(
        EventSuggestion suggestion, string calendarId, CancellationToken ct)
    {
        if (suggestion.StartUtc is null)
        {
            throw new InvalidOperationException(
                $"Suggestion {suggestion.Id} has no start time and cannot be booked.");
        }

        await _gate.WaitAsync(ct);

        try
        {
            // Layer 1: local mapping. Fast, and catches the double-action race.
            var existing = await _map.GetAsync(suggestion.Id, ct);

            if (existing is not null)
            {
                return new CalendarInsertResult(existing.GoogleEventId, existing.HtmlLink, true);
            }

            var service = await _authenticator.GetServiceAsync(ct);
            var eventId = GoogleEventIdFactory.FromEntryId(
                suggestion.EntryId, suggestion.StartUtc.Value);

            var result = await InsertOrRecoverAsync(service, calendarId, eventId, suggestion, ct);

            await _map.InsertAsync(
                new CalendarMapping(
                    suggestion.Id, calendarId, result.EventId, result.HtmlLink, _clock.UtcNow),
                ct);

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Inserts with an explicit deterministic id, and treats a 409 as success.
    /// </summary>
    /// <remarks>
    /// The awkward case is a user who booked the event and then deleted it in Google.
    /// The id stays reserved, so a re-insert still 409s — but the event is sitting there
    /// with status "cancelled". Without the recovery branch below, "delete then re-add"
    /// looks silently broken.
    /// </remarks>
    private async Task<CalendarInsertResult> InsertOrRecoverAsync(
        CalendarService service,
        string calendarId,
        string eventId,
        EventSuggestion suggestion,
        CancellationToken ct)
    {
        var payload = BuildEvent(eventId, suggestion);

        try
        {
            var created = await service.Events.Insert(payload, calendarId).ExecuteAsync(ct);
            return new CalendarInsertResult(created.Id, created.HtmlLink, false);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Conflict)
        {
            _logger?.LogInformation(
                "Event {EventId} already exists on {CalendarId}; reconciling.", eventId, calendarId);

            return await RecoverExistingAsync(service, calendarId, eventId, payload, ct);
        }
    }

    private async Task<CalendarInsertResult> RecoverExistingAsync(
        CalendarService service,
        string calendarId,
        string eventId,
        Event payload,
        CancellationToken ct)
    {
        try
        {
            var existing = await service.Events.Get(calendarId, eventId).ExecuteAsync(ct);

            if (!string.Equals(existing.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new CalendarInsertResult(existing.Id, existing.HtmlLink, true);
            }

            // Resurrect the deleted event rather than leaving the user with nothing.
            payload.Status = "confirmed";
            var revived = await service.Events.Update(payload, calendarId, eventId).ExecuteAsync(ct);

            return new CalendarInsertResult(revived.Id, revived.HtmlLink, false);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // The id is reserved but unreadable. Fall back to a server-assigned id so
            // the user still gets their event.
            payload.Id = null;
            var created = await service.Events.Insert(payload, calendarId).ExecuteAsync(ct);

            return new CalendarInsertResult(created.Id, created.HtmlLink, false);
        }
    }

    private static Event BuildEvent(string eventId, EventSuggestion suggestion)
    {
        var start = suggestion.StartUtc!.Value;
        var end = suggestion.EndUtc ?? start.AddHours(1);

        var calendarEvent = new Event
        {
            Id = eventId,
            Summary = string.IsNullOrWhiteSpace(suggestion.Title)
                ? "Free food on campus"
                : suggestion.Title,
            Location = suggestion.Location,
            Description = BuildDescription(suggestion),
            Source = new Event.SourceData
            {
                Title = Trim("Outlook: " + suggestion.Subject, 200),
            },
        };

        if (suggestion.IsAllDay)
        {
            // All-day events use bare dates, and Google treats the end as exclusive.
            calendarEvent.Start = new EventDateTime { Date = start.ToString("yyyy-MM-dd") };
            calendarEvent.End = new EventDateTime { Date = end.ToString("yyyy-MM-dd") };
        }
        else
        {
            // Wall-clock local time plus the IANA zone, never a UTC instant. A campus
            // event is scheduled in local time; sending UTC would shift it across a
            // daylight-saving boundary.
            var zone = Time.TimeZoneResolution.Resolve(suggestion.IanaTimeZone);

            calendarEvent.Start = new EventDateTime
            {
                DateTimeDateTimeOffset = TimeZoneInfo.ConvertTime(start, zone),
                TimeZone = suggestion.IanaTimeZone,
            };

            calendarEvent.End = new EventDateTime
            {
                DateTimeDateTimeOffset = TimeZoneInfo.ConvertTime(end, zone),
                TimeZone = suggestion.IanaTimeZone,
            };
        }

        return calendarEvent;
    }

    private static string BuildDescription(EventSuggestion suggestion)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(suggestion.FoodDescription))
        {
            lines.Add("Food: " + suggestion.FoodDescription);
        }

        if (!string.IsNullOrWhiteSpace(suggestion.Organization))
        {
            lines.Add("Hosted by: " + suggestion.Organization);
        }

        if (!string.IsNullOrWhiteSpace(suggestion.SenderName))
        {
            lines.Add($"From: {suggestion.SenderName} <{suggestion.SenderAddress}>");
        }

        if (!string.IsNullOrWhiteSpace(suggestion.Subject))
        {
            lines.Add("Subject: " + suggestion.Subject);
        }

        if (!string.IsNullOrWhiteSpace(suggestion.BodyExcerpt))
        {
            lines.Add("");
            lines.Add(Trim(suggestion.BodyExcerpt, MaxDescriptionChars));
        }

        lines.Add("");
        lines.Add("Detected by OutlookScraper.");

        return string.Join('\n', lines);
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    public async Task<bool> RemoveAsync(Guid suggestionId, CancellationToken ct)
    {
        var mapping = await _map.GetAsync(suggestionId, ct);

        if (mapping is null)
        {
            return false;
        }

        try
        {
            var service = await _authenticator.GetServiceAsync(ct);
            await service.Events.Delete(mapping.CalendarId, mapping.GoogleEventId).ExecuteAsync(ct);
        }
        catch (GoogleApiException ex) when (
            ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Already gone on the server; dropping the local mapping is still correct.
        }

        await _map.DeleteAsync(suggestionId, ct);
        return true;
    }
}
