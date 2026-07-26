using System.Text;

namespace OutlookScraper.Core.Text;

/// <summary>
/// Collapses the whitespace soup that HTML-to-text conversion and mail clients
/// produce, so the model spends its context on content instead of blank lines.
/// </summary>
public static class WhitespaceNormalizer
{
    // Non-breaking and zero-width spaces are endemic in HTML mail and are not all
    // caught by char.IsWhiteSpace, so they get folded to a plain space explicitly.
    private const char NoBreakSpace = ' ';
    private const char ZeroWidthSpace = '​';
    private const char ByteOrderMark = '﻿';

    public static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        var pendingNewlines = 0;
        var pendingSpace = false;
        var atStart = true;

        foreach (var raw in text)
        {
            var ch = raw is NoBreakSpace or ZeroWidthSpace or ByteOrderMark ? ' ' : raw;

            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                pendingNewlines++;
                pendingSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (!atStart)
            {
                if (pendingNewlines > 0)
                {
                    // At most one blank line survives.
                    builder.Append(pendingNewlines >= 2 ? "\n\n" : "\n");
                }
                else if (pendingSpace)
                {
                    builder.Append(' ');
                }
            }

            builder.Append(ch);
            pendingNewlines = 0;
            pendingSpace = false;
            atStart = false;
        }

        return builder.ToString();
    }
}
