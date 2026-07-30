using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OutlookScraper.Core.Models;
// Aliased as `Interop`, deliberately not as `Outlook`. This project's own
// namespace is OutlookScraper.Outlook, so an alias named `Outlook` loses to the
// enclosing namespace: `Outlook.MailItem` resolves to OutlookScraper.Outlook.MailItem,
// which does not exist. Renaming the alias is the fix.
using Interop = Microsoft.Office.Interop.Outlook;

namespace OutlookScraper.Outlook;

/// <summary>
/// Converts a live <c>MailItem</c> into a plain <see cref="RawEmail"/>.
/// </summary>
/// <remarks>
/// This is the boundary. Everything is read here, on the STA thread, and no COM object
/// is allowed out the other side. Keeping that rule absolute is what lets the entire
/// rest of the application be platform-agnostic and unit-testable.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class MailItemMapper
{
    /// <summary>PR_TRANSPORT_MESSAGE_HEADERS, used to spot auto-replies reliably.</summary>
    private const string TransportHeadersSchema =
        "http://schemas.microsoft.com/mapi/proptag/0x007D001E";

    public static RawEmail Map(Interop.MailItem item, string folderName)
    {
        using var scope = new ComScope();

        var sender = ResolveSenderAddress(item, scope);

        return new RawEmail(
            EntryId: Safe(() => item.EntryID) ?? "",
            StoreId: ResolveStoreId(item, scope),
            Subject: Safe(() => item.Subject) ?? "",
            SenderName: Safe(() => item.SenderName) ?? "",
            SenderAddress: sender,
            ReceivedLocal: ResolveReceivedTime(item),
            MessageClass: Safe(() => item.MessageClass) ?? "",
            PlainBody: Safe(() => item.Body) ?? "",
            HtmlBody: Safe(() => item.HTMLBody),
            IsAutoReply: IsAutoReply(item, scope),
            FolderName: folderName);
    }

    /// <summary>
    /// Exchange senders come back as an opaque X.400 style address, so the SMTP address
    /// has to be pulled off the <c>ExchangeUser</c> behind the sender.
    /// </summary>
    private static string ResolveSenderAddress(Interop.MailItem item, ComScope scope)
    {
        var smtp = Safe(() => item.SenderEmailAddress) ?? "";
        var type = Safe(() => item.SenderEmailType) ?? "";

        if (!type.Equals("EX", StringComparison.OrdinalIgnoreCase))
        {
            return smtp;
        }

        try
        {
            var sender = scope.Track(item.Sender);

            if (sender is null)
            {
                return smtp;
            }

            var exchangeUser = scope.Track(sender.GetExchangeUser());
            var primary = exchangeUser?.PrimarySmtpAddress;

            return string.IsNullOrWhiteSpace(primary) ? smtp : primary;
        }
        catch (COMException)
        {
            return smtp;
        }
    }

    private static string ResolveStoreId(Interop.MailItem item, ComScope scope)
    {
        try
        {
            var parent = scope.Track(item.Parent as Interop.MAPIFolder);
            var store = parent is null ? null : scope.Track(parent.Store);

            return store?.StoreID ?? "";
        }
        catch (COMException)
        {
            return "";
        }
    }

    private static DateTimeOffset ResolveReceivedTime(Interop.MailItem item)
    {
        // Explicit DateTime? — Safe<T> infers T = DateTime otherwise, and a missing
        // value would silently arrive as DateTime.MinValue rather than null.
        var received = Safe<DateTime?>(() => item.ReceivedTime);

        // Unsent or draft items have no received time and come back as a sentinel.
        if (received is null || received.Value.Year < 1900)
        {
            received = Safe<DateTime?>(() => item.CreationTime) ?? DateTime.Now;
        }

        return new DateTimeOffset(
            DateTime.SpecifyKind(received.Value, DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(received.Value));
    }

    /// <summary>
    /// Out-of-office replies and bounces are never event announcements, and filtering
    /// them here saves the model a pointless call.
    /// </summary>
    private static bool IsAutoReply(Interop.MailItem item, ComScope scope)
    {
        var messageClass = Safe(() => item.MessageClass) ?? "";

        if (messageClass.Contains("Report", StringComparison.OrdinalIgnoreCase) ||
            messageClass.Contains("Automatic", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var accessor = scope.Track(item.PropertyAccessor);
            var headers = accessor?.GetProperty(TransportHeadersSchema) as string ?? "";

            return headers.Contains("Auto-Submitted: auto-", StringComparison.OrdinalIgnoreCase) ||
                   headers.Contains("X-Autoreply:", StringComparison.OrdinalIgnoreCase) ||
                   headers.Contains("X-Autorespond:", StringComparison.OrdinalIgnoreCase) ||
                   headers.Contains("Precedence: auto_reply", StringComparison.OrdinalIgnoreCase);
        }
        catch (COMException)
        {
            // The property is absent on some stores; fall back to the subject.
            var subject = Safe(() => item.Subject) ?? "";

            return subject.StartsWith("Automatic reply:", StringComparison.OrdinalIgnoreCase) ||
                   subject.StartsWith("Out of Office:", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Any property read can throw if the item is being deleted or the connection drops
    /// mid-map. A partial record is far better than losing the whole message.
    /// </summary>
    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return default;
        }
        catch (InvalidComObjectException)
        {
            return default;
        }
    }
}
