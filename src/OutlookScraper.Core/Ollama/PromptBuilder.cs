using System.Globalization;
using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Ollama;

/// <summary>
/// Assembles the system and user prompts for classification.
/// </summary>
/// <remarks>
/// The date anchoring in the user message is the highest-leverage part of this whole
/// file, and worth more than any amount of schema tuning. Campus mail is full of
/// "this Friday", "tomorrow at noon" and "next week" — without being told both today's
/// date and the date the email was *received*, the model has no way to resolve those
/// and will simply invent a plausible-looking date.
/// </remarks>
public sealed class PromptBuilder(string ianaTimeZone)
{
    private readonly string _ianaTimeZone = ianaTimeZone;

    public const string SystemPrompt = """
        You classify university campus emails. Your job is to decide whether an email
        announces an event where attendees can get food or drink at no cost, and if so,
        to extract the event's details.

        Rules:
        - "Free food" is meant broadly: free pizza, snacks, refreshments, catering,
          coffee, donuts, boba, "lunch provided", "breakfast served", "we'll feed you".
        - Food that must be bought is NOT free. Neither is a discount, nor "free with
          purchase", nor a raffle prize, nor food that is only for organizers or staff.
        - An event needs a gathering people can actually attend. A newsletter that merely
          mentions food in passing, a job posting, or a menu announcement is not an event.
        - Resolve every relative date against the email's received date, which is given
          to you. Never output a timezone or a UTC offset — only naive local time.
        - If a detail is not stated, return an empty string rather than guessing. Set
          date_is_explicit to false if you had to infer the date at all.
        - The topic_tag must describe the recurring TYPE of event so that similar future
          emails produce the same tag. Never put dates, proper names, or room numbers in it.

        Answer only with the JSON object described by the schema.
        """;

    public string BuildUserMessage(CleanedEmail email, DateTimeOffset now)
    {
        var zone = ResolveZone(_ianaTimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var received = TimeZoneInfo.ConvertTime(email.ReceivedLocal, zone);

        return $"""
            Today is {Format(localNow)}. The current local time is {localNow:HH:mm} ({_ianaTimeZone}).
            This email was received on {Format(received)} at {received:HH:mm} local time.
            Resolve all relative dates ("tomorrow", "this Friday", "next week") against the RECEIVED time.

            From: {email.SenderName} <{email.SenderAddress}>
            Subject: {email.Subject}

            {email.Body}
            """;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// Falls back to UTC rather than throwing. A bad zone id in settings should degrade
    /// the date anchoring, not take down classification entirely.
    /// </summary>
    private static TimeZoneInfo ResolveZone(string ianaId)
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
            return TimeZoneInfo.Utc;
        }
    }
}
