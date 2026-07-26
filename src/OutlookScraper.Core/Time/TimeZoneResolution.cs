namespace OutlookScraper.Core.Time;

/// <summary>
/// Resolves and validates IANA time zone ids.
/// </summary>
/// <remarks>
/// .NET 8 handles the Windows/IANA split natively — <c>TryConvertWindowsIdToIanaId</c>
/// is built in, and on Windows .NET 6+ accepts IANA ids directly in
/// <c>FindSystemTimeZoneById</c> thanks to ICU. No TimeZoneConverter package needed.
/// </remarks>
public static class TimeZoneResolution
{
    /// <summary>The machine's zone as an IANA id, for seeding settings on first run.</summary>
    public static string LocalIanaId()
    {
        var local = TimeZoneInfo.Local;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana))
        {
            return iana;
        }

        // On Linux and macOS the system id already is an IANA id.
        return local.Id;
    }

    /// <summary>Falls back to the machine zone, then UTC. Never throws.</summary>
    public static TimeZoneInfo Resolve(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    public static bool IsValid(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(ianaId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts a naive wall-clock time in <paramref name="zone"/> into an absolute
    /// instant, handling the two days a year when that is ambiguous or impossible.
    /// </summary>
    /// <remarks>
    /// Spring forward: 02:30 simply does not exist, so it is pushed forward by the DST
    /// delta rather than throwing. Fall back: 01:30 happens twice, and the standard-time
    /// (later) offset is chosen deterministically. Both are rare, but "the app crashed
    /// on one email in March" is a genuinely awful bug to track down.
    /// </remarks>
    public static DateTimeOffset ToOffset(DateTime naiveLocal, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(naiveLocal, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            var adjustment = zone.GetAdjustmentRules()
                .FirstOrDefault(r => unspecified >= r.DateStart && unspecified <= r.DateEnd);

            var delta = adjustment?.DaylightDelta ?? TimeSpan.FromHours(1);
            unspecified = unspecified.Add(delta);
        }

        if (zone.IsAmbiguousTime(unspecified))
        {
            // GetAmbiguousTimeOffsets returns both candidates; the larger offset is
            // daylight time and the smaller is standard. Standard is the later of the
            // two real instants, and picking it consistently is what matters.
            var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
            return new DateTimeOffset(unspecified, offsets.Min());
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
