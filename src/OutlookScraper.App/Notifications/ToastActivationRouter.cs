using System.Runtime.Versioning;
using System.Threading.Channels;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Extensions.Logging;

namespace OutlookScraper.App.Notifications;

/// <summary>What the user pressed on a toast.</summary>
public sealed record ToastAction(string Action, Guid SuggestionId);

/// <summary>
/// Receives toast activations and hands them to the app on a thread it controls.
/// </summary>
/// <remarks>
/// <c>OnActivated</c> is raised by Windows on a COM-owned background thread, and the
/// handler must return promptly. Doing database or HTTP work inline there is a good way
/// to get an activation dropped, so this parses the arguments, writes to a channel, and
/// returns; a consumer elsewhere does the real work and marshals to the UI thread only
/// when it needs to.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ToastActivationRouter : IDisposable
{
    private readonly Channel<ToastAction> _actions = Channel.CreateUnbounded<ToastAction>();
    private readonly ILogger<ToastActivationRouter>? _logger;
    private bool _subscribed;

    public ToastActivationRouter(ILogger<ToastActivationRouter>? logger = null) => _logger = logger;

    public ChannelReader<ToastAction> Actions => _actions.Reader;

    /// <summary>
    /// Must be called before any window is shown and before the first toast, or a
    /// cold-start activation is lost.
    /// </summary>
    public void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        ToastNotificationManagerCompat.OnActivated += OnActivated;
        _subscribed = true;
    }

    /// <summary>
    /// True when Windows launched this process specifically to handle a toast button.
    /// The app should then start straight to the tray rather than popping a window at
    /// someone who pressed a background-activation button.
    /// </summary>
    public static bool LaunchedByToast()
    {
        try
        {
            return ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var arguments = ToastArguments.Parse(e.Argument);

            if (!arguments.TryGetValue(ToastService.ArgumentAction, out var action))
            {
                return;
            }

            // A summary or status toast carries no suggestion id; it just opens the app.
            if (!arguments.TryGetValue(ToastService.ArgumentSuggestionId, out var rawId) ||
                !Guid.TryParse(rawId, out var suggestionId))
            {
                _actions.Writer.TryWrite(new ToastAction(ToastService.ActionOpen, Guid.Empty));
                return;
            }

            _actions.Writer.TryWrite(new ToastAction(action, suggestionId));
        }
        catch (Exception ex)
        {
            // This runs on a Windows-owned thread; an escaping exception would be very
            // hard to diagnose.
            _logger?.LogError(ex, "Failed to route a toast activation.");
        }
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            ToastNotificationManagerCompat.OnActivated -= OnActivated;
            _subscribed = false;
        }

        _actions.Writer.TryComplete();
    }
}
