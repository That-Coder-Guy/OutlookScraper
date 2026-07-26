using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace OutlookScraper.Core.Text;

/// <summary>
/// Extracts readable text from an HTML mail body.
/// </summary>
/// <remarks>
/// Only used when Outlook's plain-text <c>Body</c> is empty, which happens on
/// HTML-only mail — exactly the kind of thing campus marketing lists send.
/// </remarks>
public static class HtmlToText
{
    private static readonly HashSet<string> IgnoredNodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "meta", "link", "noscript",
    };

    private static readonly HashSet<string> BlockNodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6",
        "table", "section", "article", "header", "footer", "blockquote",
    };

    public static string Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);

        var builder = new StringBuilder();
        Walk(document.DocumentNode, builder);

        return WhitespaceNormalizer.Collapse(WebUtility.HtmlDecode(builder.ToString()));
    }

    private static void Walk(HtmlNode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            if (IgnoredNodes.Contains(child.Name))
            {
                continue;
            }

            if (child.NodeType == HtmlNodeType.Text)
            {
                builder.Append(child.InnerText);
                continue;
            }

            var isBlock = BlockNodes.Contains(child.Name);

            if (isBlock)
            {
                builder.Append('\n');
            }

            Walk(child, builder);

            if (isBlock)
            {
                builder.Append('\n');
            }
        }
    }
}
