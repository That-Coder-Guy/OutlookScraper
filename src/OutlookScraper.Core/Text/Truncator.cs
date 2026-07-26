namespace OutlookScraper.Core.Text;

/// <summary>
/// Trims a cleaned body down to what fits comfortably in the model's context.
/// </summary>
/// <remarks>
/// Head *and* tail, not just head. Announcement emails habitually put the logistics —
/// date, room, "food provided" — in a sign-off block at the very bottom, so a plain
/// head truncation throws away the fields this whole app is trying to extract.
/// </remarks>
public static class Truncator
{
    public const string Marker = "\n…[truncated]…\n";

    public static string HeadAndTail(string text, int headChars, int tailChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(headChars);
        ArgumentOutOfRangeException.ThrowIfNegative(tailChars);

        if (string.IsNullOrEmpty(text) || text.Length <= headChars + tailChars)
        {
            return text ?? "";
        }

        if (tailChars == 0)
        {
            return text[..headChars] + Marker;
        }

        return text[..headChars] + Marker + text[^tailChars..];
    }
}
