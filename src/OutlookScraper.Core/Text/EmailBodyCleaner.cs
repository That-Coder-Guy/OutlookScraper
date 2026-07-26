using System.Text;
using System.Text.RegularExpressions;

namespace OutlookScraper.Core.Text;

/// <summary>
/// Strips the parts of an email that carry no signal about the event: quoted reply
/// chains, list footers, unsubscribe boilerplate and tracking URLs.
/// </summary>
/// <remarks>
/// This matters more than it looks. Campus listserv mail routinely carries several
/// hundred characters of unsubscribe boilerplate, and a forwarded announcement can
/// bury the actual event under three layers of quoting. Everything left in the body
/// competes for the model's attention and context budget.
/// </remarks>
public static partial class EmailBodyCleaner
{
    /// <summary>Lines that begin a quoted reply chain — everything from here down goes.</summary>
    private static readonly string[] QuoteMarkers =
    [
        "-----Original Message-----",
        "-------- Original Message --------",
        "________________________________",
        "--- Forwarded message ---",
        "---------- Forwarded message ----------",
    ];

    /// <summary>Footer boilerplate — truncate from the first one that appears.</summary>
    private static readonly string[] FooterMarkers =
    [
        "to unsubscribe",
        "unsubscribe from this list",
        "you are receiving this email because",
        "you received this message because",
        "this email was sent to",
        "manage your subscription",
        "update your preferences",
        "view this email in your browser",
        "confidentiality notice",
        "this message and any attachments",
    ];

    public static string Clean(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        var text = WhitespaceNormalizer.Collapse(body);

        text = TruncateAtFirst(text, QuoteMarkers, StringComparison.OrdinalIgnoreCase);
        text = RemoveQuotedLines(text);
        text = TruncateAtFirstLineStartingWith(text, FooterMarkers);
        text = ReplaceUrls(text);

        return WhitespaceNormalizer.Collapse(text);
    }

    /// <summary>
    /// Replaces URLs with a bare <c>[link:domain]</c>. Tracking links routinely run to
    /// hundreds of characters of query string and are pure noise to the model, but the
    /// domain itself occasionally tells it something useful.
    /// </summary>
    private static string ReplaceUrls(string text) =>
        UrlPattern().Replace(text, match =>
        {
            var url = match.Value;

            return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                ? $"[link:{parsed.Host}]"
                : "[link]";
        });

    private static string TruncateAtFirst(string text, string[] markers, StringComparison comparison)
    {
        var cut = -1;

        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, comparison);

            if (index >= 0 && (cut < 0 || index < cut))
            {
                cut = index;
            }
        }

        return cut < 0 ? text : text[..cut];
    }

    /// <summary>
    /// Footer markers only count at the start of a line. "This email was sent to" in
    /// the middle of a sentence is prose; on its own line it is boilerplate.
    /// </summary>
    private static string TruncateAtFirstLineStartingWith(string text, string[] markers)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            foreach (var marker in markers)
            {
                if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return builder.ToString();
                }
            }

            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Drops classic ">" quoted lines and "On ... wrote:" attributions.</summary>
    private static string RemoveQuotedLines(string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('>'))
            {
                continue;
            }

            if (WroteAttributionPattern().IsMatch(trimmed))
            {
                break;
            }

            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"https?://[^\s<>""\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^On .{4,80}\bwrote:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WroteAttributionPattern();
}
