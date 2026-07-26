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
    /// Shown in the review window so the user can sanity-check what the model saw.
    /// </summary>
    public string Excerpt(int maxChars = 600) =>
        Body.Length <= maxChars ? Body : Body[..maxChars] + "…";
}
