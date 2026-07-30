using System.Drawing;
using System.IO;
using System.Windows;
using H.NotifyIcon.Core;
using OutlookScraper.Core.Models;

namespace OutlookScraper.App.Tray;

/// <summary>
/// The notification-area icon: status, pending count, and the context menu.
/// </summary>
/// <remarks>
/// The icon is the app's only permanent presence, so it carries the health of both
/// dependencies. "Outlook is closed" and "Ollama is not running" are both ordinary,
/// recoverable states, and the user needs to be able to see which one they are in
/// without opening anything.
/// </remarks>
// Deliberately no [SupportedOSPlatform("windows")] here. That attribute would widen
// this type's claim to *every* Windows version, while the tray APIs declare support
// from 5.1.2600 — and the platform analyzer rejects a call site that promises more
// than the API guarantees. Without it the type inherits windows10.0.19041.0 from the
// project's TFM, which satisfies them and tracks the TFM automatically.
public sealed class TrayIconService : IDisposable
{
    private readonly TrayIconWithContextMenu _icon;
    private MailSourceState _mailState = MailSourceState.Disconnected;
    private OllamaHealth _ollamaHealth = OllamaHealth.Unknown;
    private int _pendingCount;
    private int _queueDepth;

    private readonly Icon? _appIcon;

    public TrayIconService()
    {
        _appIcon = LoadIcon();

        _icon = new TrayIconWithContextMenu
        {
            Icon = (_appIcon ?? SystemIcons.Application).Handle,
            ToolTip = "OutlookScraper",
        };

        _icon.ContextMenu = new PopupMenu
        {
            Items =
            {
                new PopupMenuItem("Review free-food events…", (_, _) => ReviewRequested?.Invoke(this, EventArgs.Empty)),
                new PopupMenuSeparator(),
                new PopupMenuItem("Scan for new mail now", (_, _) => RescanRequested?.Invoke(this, EventArgs.Empty)),
                new PopupMenuItem("Retry failed messages", (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty)),
                new PopupMenuSeparator(),
                new PopupMenuItem("Settings…", (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)),
                new PopupMenuSeparator(),
                new PopupMenuItem("Exit", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)),
            },
        };

        _icon.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent == MouseEvent.IconLeftDoubleClick)
            {
                ReviewRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        _icon.Create();
    }

    public event EventHandler? ReviewRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? RescanRequested;

    public event EventHandler? RetryRequested;

    public event EventHandler? ExitRequested;

    public void SetMailState(MailSourceState state)
    {
        _mailState = state;
        UpdateTooltip();
    }

    public void SetOllamaHealth(OllamaHealth health)
    {
        _ollamaHealth = health;
        UpdateTooltip();
    }

    public void SetPendingCount(int pending)
    {
        _pendingCount = pending;
        UpdateTooltip();
    }

    /// <summary>Shows backfill progress, so a long first run does not look like a hang.</summary>
    public void SetQueueDepth(int depth)
    {
        _queueDepth = depth;
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        var lines = new List<string> { "OutlookScraper" };

        lines.Add(_mailState switch
        {
            MailSourceState.Connected => "Outlook: connected",
            MailSourceState.WaitingForHost => "Outlook: not running — waiting",
            MailSourceState.Connecting => "Outlook: connecting…",
            MailSourceState.Faulted => "Outlook: reconnecting…",
            _ => "Outlook: disconnected",
        });

        lines.Add(_ollamaHealth switch
        {
            OllamaHealth.Healthy => "Ollama: ready",
            OllamaHealth.ModelMissing => "Ollama: model not installed",
            OllamaHealth.Unreachable => "Ollama: not reachable",
            _ => "Ollama: checking…",
        });

        if (_pendingCount > 0)
        {
            lines.Add($"{_pendingCount} event(s) awaiting review");
        }

        if (_queueDepth > 0)
        {
            lines.Add($"{_queueDepth} message(s) queued");
        }

        // The Shell tooltip is capped at 127 characters and silently truncates.
        var tooltip = string.Join('\n', lines);
        _icon.UpdateToolTip(tooltip.Length > 127 ? tooltip[..127] : tooltip);
    }

    /// <summary>
    /// Loads the packed icon resource, falling back to the stock application icon
    /// rather than failing to show a tray presence at all.
    /// </summary>
    private static Icon? LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/tray.ico", UriKind.Absolute);
            using var stream = Application.GetResourceStream(uri)?.Stream;

            return stream is null ? null : new Icon(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _icon.Dispose();
        _appIcon?.Dispose();
    }
}
