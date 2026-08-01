namespace OutlookScraper.Core.Models;

/// <summary>
/// An email after HTML extraction, quoted-reply/footer stripping and truncation —
/// i.e. exactly what gets handed to the model.
/// </summary>
public sealed record CleanedEmail(
    string EntryId,
    string Subject,
    string SenderName,
    string SenderAddress,
    DateTimeOffset ReceivedLocal,
    string Body,
    string BodyHash)
{
    /// <summary>
    /// How much text the model actually receives — subject included, because the prompt
    /// includes it. A three-line announcement with everything in the subject line is a
    /// perfectly classifiable email, and measuring only the body throws it away.
    /// </summary>
    public int SignalLength => Subject.Trim().Length + Body.Length;

    /// <summary>
    /// Shown in the review window so the user can sanity-check what the model saw.
    /// </summary>
    public string Excerpt(int maxChars = 600) =>
        Body.Length <= maxChars ? Body : Body[..maxChars] + "…";
}
