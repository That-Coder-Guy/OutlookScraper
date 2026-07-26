using Dapper;

namespace OutlookScraper.Core.Storage;

/// <summary>
/// Machine-managed runtime state — watermarks and flags the user has no reason to
/// edit. Anything hand-tunable lives in <c>settings.json</c> instead.
/// </summary>
public sealed class StateRepository(Database database)
{
    private readonly Database _database = database;

    /// <summary>
    /// How far the sweep has confirmed processing. Deliberately advanced only after
    /// every message in a batch reaches a terminal state — advancing on enqueue would
    /// permanently lose mail if Ollama went down mid-backfill.
    /// </summary>
    public const string LastSweepUtc = "LastSweepUtc";

    public const string BackfillCompleted = "BackfillCompleted";
    public const string EmbeddingsAvailable = "EmbeddingsAvailable";
    public const string LastRetentionUtc = "LastRetentionUtc";

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Value FROM AppState WHERE Key = @key;",
            new { key },
            cancellationToken: ct));
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO AppState (Key, Value) VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """,
            new { key, value },
            cancellationToken: ct));
    }

    public async Task<DateTimeOffset?> GetTimestampAsync(string key, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);

        return DateTimeOffset.TryParse(
            raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    public Task SetTimestampAsync(string key, DateTimeOffset value, CancellationToken ct = default) =>
        SetAsync(key, value.ToUniversalTime().ToString("O"), ct);

    public async Task<bool> GetFlagAsync(string key, CancellationToken ct = default) =>
        string.Equals(await GetAsync(key, ct), "true", StringComparison.OrdinalIgnoreCase);

    public Task SetFlagAsync(string key, bool value, CancellationToken ct = default) =>
        SetAsync(key, value ? "true" : "false", ct);
}
