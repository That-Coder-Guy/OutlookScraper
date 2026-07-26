namespace OutlookScraper.Core.Blacklist;

/// <summary>
/// Vector helpers for semantic blacklist matching, plus the BLOB round-trip.
/// </summary>
/// <remarks>
/// Brute force on purpose. There will be tens — at most low hundreds — of blacklist
/// rules, and a cosine over that many 768-dimension vectors is measured in
/// microseconds. Adding a vector index or a vector database at this scale would be
/// pure ceremony.
/// </remarks>
public static class VectorMath
{
    /// <summary>Little-endian float32, which is what the BLOB column stores.</summary>
    public static byte[]? ToBytes(float[]? vector)
    {
        if (vector is null || vector.Length == 0)
        {
            return null;
        }

        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[]? FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
        {
            return null;
        }

        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    /// <summary>Scales to unit length so cosine reduces to a dot product.</summary>
    public static float[] Normalize(float[] vector)
    {
        var magnitude = 0.0d;

        foreach (var value in vector)
        {
            magnitude += (double)value * value;
        }

        magnitude = Math.Sqrt(magnitude);

        if (magnitude <= double.Epsilon)
        {
            return vector;
        }

        var result = new float[vector.Length];

        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] / magnitude);
        }

        return result;
    }

    /// <summary>
    /// Cosine similarity. Returns 0 for mismatched or empty vectors rather than
    /// throwing — a dimension mismatch means the vectors came from different
    /// embedding models, which is a "not comparable", not an error.
    /// </summary>
    public static double Cosine(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, magA = 0, magB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            magA += (double)a[i] * a[i];
            magB += (double)b[i] * b[i];
        }

        if (magA <= double.Epsilon || magB <= double.Epsilon)
        {
            return 0;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
