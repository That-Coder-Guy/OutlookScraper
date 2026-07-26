using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;

namespace OutlookScraper.App;

/// <summary>The result of acting on a suggestion, phrased for display.</summary>
public sealed record ActionResult(bool Success, string Message);

/// <summary>
/// The single implementation of Add / Blacklist / Dismiss.
/// </summary>
/// <remarks>
/// Both the toast buttons and the review window route through here, so the two paths
/// cannot drift apart in behaviour — which is exactly the kind of divergence that
/// produces "it works from the window but not the toast" bugs.
/// </remarks>
public sealed class SuggestionActions(
    SuggestionRepository suggestions,
    BlacklistService blacklist,
    ICalendarSink calendar,
    INotifier notifier,
    AppSettings settings,
    IClock clock,
    ILogger<SuggestionActions>? logger = null)
{
    private readonly SuggestionRepository _suggestions = suggestions;
    private readonly BlacklistService _blacklist = blacklist;
    private readonly ICalendarSink _calendar = calendar;
    private readonly INotifier _notifier = notifier;
    private readonly AppSettings _settings = settings;
    private readonly IClock _clock = clock;
    private readonly ILogger<SuggestionActions>? _logger = logger;

    /// <summary>Raised after any action so open windows can refresh.</summary>
    public event EventHandler? Changed;

    public async Task<ActionResult> AddToCalendarAsync(Guid suggestionId, CancellationToken ct = default)
    {
        var suggestion = await _suggestions.GetAsync(suggestionId, ct);

        if (suggestion is null)
        {
            return new ActionResult(false, "That suggestion no longer exists.");
        }

        // Acting on an already-resolved suggestion is a no-op, not an error — it just
        // means the user got there from two places.
        if (suggestion.State == SuggestionState.Added)
        {
            await _notifier.RemoveAsync(suggestionId);
            return new ActionResult(true, "Already on your calendar.");
        }

        if (!suggestion.CanAddToCalendar)
        {
            return new ActionResult(false, "This event needs a date before it can be added.");
        }

        try
        {
            var result = await _calendar.AddAsync(suggestion, _settings.Calendar.CalendarId, ct);

            await _suggestions.SetStateAsync(
                suggestionId, SuggestionState.Added, _clock.UtcNow, ct);

            await _notifier.RemoveAsync(suggestionId);
            Changed?.Invoke(this, EventArgs.Empty);

            return new ActionResult(
                true, result.AlreadyExisted ? "Already on your calendar." : "Added to your calendar.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to add suggestion {Id} to the calendar.", suggestionId);
            return new ActionResult(false, "Could not add to calendar: " + ex.Message);
        }
    }

    public async Task<ActionResult> BlacklistAsync(Guid suggestionId, CancellationToken ct = default)
    {
        var suggestion = await _suggestions.GetAsync(suggestionId, ct);

        if (suggestion is null)
        {
            return new ActionResult(false, "That suggestion no longer exists.");
        }

        try
        {
            var (entry, swept) = await _blacklist.BlacklistAsync(suggestionId, ct);

            await _notifier.RemoveAsync(suggestionId);
            Changed?.Invoke(this, EventArgs.Empty);

            // Surfacing the sweep count is what makes the feature feel intelligent
            // rather than like a single-message mute.
            var message = swept > 0
                ? $"Blacklisted '{entry.TopicTag}' — also hid {swept} similar."
                : $"Blacklisted '{entry.TopicTag}'.";

            return new ActionResult(true, message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to blacklist suggestion {Id}.", suggestionId);
            return new ActionResult(false, "Could not blacklist: " + ex.Message);
        }
    }

    public async Task<ActionResult> DismissAsync(Guid suggestionId, CancellationToken ct = default)
    {
        // Dismiss means dismiss. Deliberately no learning, no side effects — inferring
        // a preference from a dismissal is exactly the kind of cleverness that makes an
        // app feel like it is second-guessing you.
        await _suggestions.SetStateAsync(suggestionId, SuggestionState.Dismissed, _clock.UtcNow, ct);
        await _notifier.RemoveAsync(suggestionId);

        Changed?.Invoke(this, EventArgs.Empty);
        return new ActionResult(true, "Dismissed.");
    }

    /// <summary>Restores a soft-suppressed suggestion and stops that rule re-matching it.</summary>
    public async Task<ActionResult> RescueAsync(Guid suggestionId, CancellationToken ct = default)
    {
        await _blacklist.RescueAsync(suggestionId, ct);
        Changed?.Invoke(this, EventArgs.Empty);

        return new ActionResult(true, "Restored, and this rule will not hide it again.");
    }
}
