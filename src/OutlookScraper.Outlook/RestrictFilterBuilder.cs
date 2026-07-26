using System.Globalization;
using System.Runtime.Versioning;

namespace OutlookScraper.Outlook;

/// <summary>
/// Builds the DASL/Jet filter strings passed to <c>Items.Restrict</c>.
/// </summary>
/// <remarks>
/// The date format is not negotiable. Outlook parses restriction dates using US
/// formatting regardless of the machine's locale, so building the string with the
/// current culture silently returns zero results on a machine set to, say, German.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RestrictFilterBuilder
{
    private static readonly CultureInfo OutlookCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Restriction on <c>ReceivedTime</c> comparisons truncate to whole minutes, so the
    /// caller must overlap the window rather than using a strict boundary. See
    /// <see cref="SweepOverlap"/>.
    /// </summary>
    public static readonly TimeSpan SweepOverlap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Everything received at or after <paramref name="sinceLocal"/>. Always inclusive:
    /// an exclusive comparison combined with minute truncation drops messages.
    /// </summary>
    public static string ReceivedSince(DateTime sinceLocal) =>
        $"[ReceivedTime] >= '{sinceLocal.ToString("g", OutlookCulture)}'";

    /// <summary>Applies the overlap so a minute-truncated boundary cannot skip mail.</summary>
    public static string ReceivedSinceWithOverlap(DateTimeOffset sinceLocal) =>
        ReceivedSince(sinceLocal.LocalDateTime - SweepOverlap);
}
