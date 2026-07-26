namespace OutlookScraper.Core.Blacklist;

/// <summary>
/// Stage 2 of the cascade: overlap between normalized token sets.
/// </summary>
/// <remarks>
/// Catches the near-misses stage 1 cannot — <c>cs-club-pizza-night</c> versus
/// <c>cs-club-pizza</c> — while staying fully deterministic and working with no
/// embedding model installed.
/// </remarks>
public static class TokenSimilarity
{
    /// <summary>Intersection over union. 1.0 means identical sets, 0.0 means disjoint.</summary>
    public static double Jaccard(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        var setB = new HashSet<string>(b, StringComparer.Ordinal);

        var intersection = setA.Intersect(setB, StringComparer.Ordinal).Count();
        var union = setA.Count + setB.Count - intersection;

        return union == 0 ? 0 : (double)intersection / union;
    }

    public static double Jaccard(string? tagA, string? tagB) =>
        Jaccard(TagNormalizer.Tokenize(tagA), TagNormalizer.Tokenize(tagB));
}
