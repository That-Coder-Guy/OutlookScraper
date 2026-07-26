using System.Security.Cryptography;
using System.Text;

namespace OutlookScraper.Core.Calendar;

/// <summary>
/// Derives a deterministic Google Calendar event id from the source message.
/// </summary>
/// <remarks>
/// This is the half of the double-booking guard that actually matters. The local
/// mapping table catches the common double-click race, but it disappears if the
/// database is lost, the app is reinstalled, or a backup is restored. A deterministic
/// id pushes the guarantee onto Google's side: a second insert with the same id comes
/// back as HTTP 409, which the sink treats as "already booked" rather than an error.
///
/// Google's constraints on event ids: characters from the base32hex alphabet
/// (<c>a-v</c> and <c>0-9</c>) only, and between 5 and 1024 characters.
/// </remarks>
public static class GoogleEventIdFactory
{
    /// <summary>Base32hex, lowercased — exactly Google's permitted alphabet.</summary>
    private const string Base32HexAlphabet = "0123456789abcdefghijklmnopqrstuv";

    /// <summary>
    /// Must itself be inside the base32hex alphabet — w, x, y and z are not, so an
    /// obvious-looking prefix like "ofw" would make every id invalid.
    /// </summary>
    private const string Prefix = "ofs";

    /// <summary>Characters of hash taken. 3 + 26 = 29, comfortably inside the limits.</summary>
    private const int HashChars = 26;

    /// <summary>
    /// The start time is part of the key on purpose: if the user corrects a wrong date
    /// and re-adds, that is a genuinely different booking and should get its own event
    /// rather than colliding with the bad one.
    /// </summary>
    public static string FromEntryId(string outlookEntryId, DateTimeOffset start)
    {
        var material = $"{outlookEntryId}|{start.ToUnixTimeSeconds()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return Prefix + Encode(hash)[..HashChars];
    }

    /// <summary>Validates against Google's documented id rules.</summary>
    public static bool IsValid(string? id) =>
        id is { Length: >= 5 and <= 1024 } && id.All(c => Base32HexAlphabet.Contains(c));

    private static string Encode(byte[] data)
    {
        var builder = new StringBuilder();
        int buffer = 0, bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                builder.Append(Base32HexAlphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            builder.Append(Base32HexAlphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return builder.ToString();
    }
}
