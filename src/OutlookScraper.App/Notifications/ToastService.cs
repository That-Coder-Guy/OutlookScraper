using System.Runtime.Versioning;
using CommunityToolkit.WinUI.Notifications;
using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Time;

namespace OutlookScraper.App.Notifications;

/// <summary>
/// Builds and shows the interactive toasts.
/// </summary>
/// <remarks>
/// The only thing put into a toast's arguments is the suggestion's GUID, never the
/// event details. The handler resolves it against SQLite, which means a button pressed
/// after a reboot (when the app has no in-memory state at all) takes exactly the same
/// code path as one pressed while the app is running. It is also why the pipeline
/// persists a suggestion before asking for a toast.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ToastService : INotifier
{
    /// <summary>Groups this app's toasts so they can be cleared without touching others.</summary>
    private const string ToastGroup = "freefood";

    public const string ArgumentAction = "action";
    public const string ArgumentSuggestionId = "sid";

    public const string ActionOpen = "open";
    public const string ActionAdd = "add";
    public const string ActionBlacklist = "blacklist";

    public Task ShowSuggestionAsync(EventSuggestion suggestion)
    {
        var id = suggestion.Id.ToString("N");

        var builder = new ToastContentBuilder()
            .AddArgument(ArgumentAction, ActionOpen)
            .AddArgument(ArgumentSuggestionId, suggestion.Id.ToString())
            .AddText(BuildHeadline(suggestion))
            .AddText(BuildDetail(suggestion));

        if (!string.IsNullOrWhiteSpace(suggestion.SenderName))
        {
            builder.AddAttributionText(suggestion.SenderName);
        }

        // Background activation so a button press does not yank focus away from
        // whatever the user is actually doing.
        builder
            .AddButton(new ToastButton()
                .SetContent(suggestion.CanAddToCalendar ? "Add to Calendar" : "Review date")
                .AddArgument(ArgumentAction, suggestion.CanAddToCalendar ? ActionAdd : ActionOpen)
                .AddArgument(ArgumentSuggestionId, suggestion.Id.ToString())
                .SetBackgroundActivation())
            .AddButton(new ToastButton()
                .SetContent("Blacklist")
                .AddArgument(ArgumentAction, ActionBlacklist)
                .AddArgument(ArgumentSuggestionId, suggestion.Id.ToString())
                .SetBackgroundActivation())
            .AddButton(new ToastButtonDismiss("Dismiss"));

        builder.Show(toast =>
        {
            toast.Tag = id;
            toast.Group = ToastGroup;

            // Nothing actionable should linger in the notification centre forever.
            toast.ExpirationTime = DateTimeOffset.Now.AddDays(1);
        });

        return Task.CompletedTask;
    }

    private static string BuildHeadline(EventSuggestion suggestion) =>
        string.IsNullOrWhiteSpace(suggestion.Title)
            ? "Free food on campus"
            : "Free food: " + suggestion.Title;

    private static string BuildDetail(EventSuggestion suggestion)
    {
        var parts = new List<string>();

        if (suggestion.StartUtc is { } start)
        {
            var zone = TimeZoneResolution.Resolve(suggestion.IanaTimeZone);
            var local = TimeZoneInfo.ConvertTime(start, zone);

            parts.Add(suggestion.IsAllDay
                ? local.ToString("ddd d MMM")
                : local.ToString("ddd d MMM, h:mm tt"));
        }
        else
        {
            parts.Add("Date not stated");
        }

        if (!string.IsNullOrWhiteSpace(suggestion.Location))
        {
            parts.Add(suggestion.Location);
        }

        if (!string.IsNullOrWhiteSpace(suggestion.FoodDescription))
        {
            parts.Add(suggestion.FoodDescription);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// One toast standing in for several withheld detections, so a burst of campus mail
    /// does not become a wall of notifications.
    /// </summary>
    public Task ShowSummaryAsync(int count)
    {
        new ToastContentBuilder()
            .AddArgument(ArgumentAction, ActionOpen)
            .AddText(count == 1
                ? "1 more free-food event found"
                : $"{count} more free-food events found")
            .AddText("Open OutlookScraper to review them.")
            .Show(toast =>
            {
                toast.Tag = "summary";
                toast.Group = ToastGroup;
                toast.ExpirationTime = DateTimeOffset.Now.AddDays(1);
            });

        return Task.CompletedTask;
    }

    public Task ShowStatusAsync(string title, string body)
    {
        new ToastContentBuilder()
            .AddArgument(ArgumentAction, ActionOpen)
            .AddText(title)
            .AddText(body)
            .Show(toast =>
            {
                toast.Tag = "status";
                toast.Group = ToastGroup;
                toast.ExpirationTime = DateTimeOffset.Now.AddHours(6);
            });

        return Task.CompletedTask;
    }

    /// <summary>Clears a toast once its suggestion has been resolved somewhere else.</summary>
    public Task RemoveAsync(Guid suggestionId)
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(suggestionId.ToString("N"), ToastGroup);
        }
        catch (Exception)
        {
            // The history API throws on some Windows builds when the notification is
            // already gone. Failing to clear a stale toast is not worth crashing over.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops toasts for suggestions that are no longer pending. Run at startup so a
    /// reboot does not leave dead buttons in the notification centre.
    /// </summary>
    public void RemoveStale(IEnumerable<Guid> resolvedSuggestionIds)
    {
        foreach (var id in resolvedSuggestionIds)
        {
            _ = RemoveAsync(id);
        }
    }
}
