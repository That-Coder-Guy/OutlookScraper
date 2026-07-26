namespace OutlookScraper.Core.Blacklist;

/// <summary>
/// Turns a free-form LLM topic tag into a canonical, order-independent key.
/// </summary>
/// <remarks>
/// This is what makes stage 1 of the cascade work: <c>free-pizza-club-meeting</c> and
/// <c>club-meeting-with-free-pizza</c> both reduce to the same key, so the obvious
/// "same thing, different word order" case is caught deterministically without
/// needing an embedding model at all.
///
/// The synonym map is deliberately small and high-confidence. Genuine semantic
/// equivalence (<c>boba-social</c> vs <c>bubble-tea-social</c>) is stage 3's job —
/// trying to enumerate synonyms here would be a losing game.
/// </remarks>
public static class TagNormalizer
{
    /// <summary>
    /// Words carrying no discriminating power in this domain. "free" is in here for a
    /// reason: essentially every tag in a free-food classifier contains it, so it
    /// separates nothing.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "for", "with", "at", "on", "to", "in",
        "is", "are", "be", "by", "from", "our", "your", "you", "we", "us", "this",
        "that", "free", "event", "events",
    };

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["frat"] = "fraternity",
        ["frats"] = "fraternity",
        ["greeklife"] = "greek",
        ["rush"] = "recruitment",
        ["recruiting"] = "recruitment",
        ["info"] = "information",
        ["infosession"] = "information",
        ["dept"] = "department",
        ["grad"] = "graduate",
        ["undergrad"] = "undergraduate",
        ["assoc"] = "association",
        ["org"] = "organization",
        ["mtg"] = "meeting",
        ["lunchtime"] = "lunch",
        ["luncheon"] = "lunch",
        ["soc"] = "social",
        ["intl"] = "international",
        ["stu"] = "student",
        ["stdnt"] = "student",
    };

    /// <summary>
    /// Splits, drops stop words, applies synonyms, stems, de-duplicates and sorts.
    /// Sorting is what makes the result order-independent.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var ch in tag)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        var normalized = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            if (StopWords.Contains(token))
            {
                continue;
            }

            var mapped = Synonyms.TryGetValue(token, out var synonym) ? synonym : token;
            var stemmed = Stem(mapped);

            if (stemmed.Length > 0 && !StopWords.Contains(stemmed))
            {
                normalized.Add(stemmed);
            }
        }

        return normalized.ToList();
    }

    /// <summary>The canonical key stored alongside every tag and compared in stage 1.</summary>
    public static string Normalize(string? tag) => string.Join('-', Tokenize(tag));

    /// <summary>
    /// Conservative suffix stripping — enough to fold plurals and simple verb forms
    /// together without the false merges a full Porter stemmer would introduce on
    /// short tags.
    /// </summary>
    private static string Stem(string word)
    {
        if (word.Length <= 3)
        {
            return word;
        }

        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4)
        {
            return word[..^3] + "y";
        }

        foreach (var suffix in new[] { "ches", "shes", "sses", "xes", "zes" })
        {
            if (word.EndsWith(suffix, StringComparison.Ordinal) && word.Length > 4)
            {
                return word[..^2];
            }
        }

        // Deliberately no "-ing" rule. In this domain "-ing" words are nouns
        // ("meeting", "gathering", "screening"), and stripping it both mangles them and
        // breaks singular/plural agreement: "meetings" reaches only the plural rule and
        // stems to "meeting", so stemming "meeting" to "meet" would split the pair.
        if (word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 4)
        {
            return word[..^2];
        }

        if (word.EndsWith('s') &&
            !word.EndsWith("ss", StringComparison.Ordinal) &&
            word.Length > 3)
        {
            return word[..^1];
        }

        return word;
    }
}
