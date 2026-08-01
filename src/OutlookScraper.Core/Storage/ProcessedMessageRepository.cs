using Dapper;
using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Storage;

/// <summary>Count of messages that failed to classify, plus the most recent reason.</summary>
public sealed record FailureSummary(int Count, string? LastError)
{
    public bool Any => Count > 0;
}

/// <summary>
/// Dedup and per-message state. <see cref="TryBeginAsync"/> is the gate that makes
/// the three redundant mail-delivery paths safe.
/// </summary>
public sealed class ProcessedMessageRepository(Database database)
{
    private readonly Database _database = database;

    /// <summary>
    /// Claims a message for processing. Returns false if it has already been seen,
    /// which is the normal outcome when ItemAdd, NewMailEx and the sweep all deliver
    /// the same mail.
    /// </summary>
    public async Task<bool> TryBeginAsync(RawEmail email, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR IGNORE INTO ProcessedMessages
                (EntryId, StoreId, BodyHash, Subject, SenderAddress, SenderName,
                 ReceivedUtc, Status, SkipReason, Attempts)
            VALUES
                (@EntryId, @StoreId, '', @Subject, @SenderAddress, @SenderName,
                 @ReceivedUtc, @Status, @SkipReason, 0);
            """,
            new
            {
                email.EntryId,
                email.StoreId,
                email.Subject,
                email.SenderAddress,
                email.SenderName,
                ReceivedUtc = email.ReceivedLocal,
                Status = nameof(ProcessedStatus.Queued),
                SkipReason = nameof(Models.SkipReason.None),
            },
            cancellationToken: ct));

        return inserted > 0;
    }

    public async Task<ProcessedMessage?> GetAsync(string entryId, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        return await connection.QuerySingleOrDefaultAsync<ProcessedMessage>(new CommandDefinition(
            "SELECT * FROM ProcessedMessages WHERE EntryId = @entryId;",
            new { entryId },
            cancellationToken: ct));
    }

    public async Task<bool> ExistsAsync(string entryId, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM ProcessedMessages WHERE EntryId = @entryId;",
            new { entryId },
            cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Finds an already-classified message with an identical cleaned body. Lets a
    /// listserv resend reuse the previous verdict instead of paying for the model again.
    /// </summary>
    public async Task<ProcessedMessage?> FindClassifiedByBodyHashAsync(
        string bodyHash, string excludeEntryId, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        return await connection.QueryFirstOrDefaultAsync<ProcessedMessage>(new CommandDefinition(
            """
            SELECT * FROM ProcessedMessages
            WHERE BodyHash = @bodyHash
              AND EntryId <> @excludeEntryId
              AND Status = 'Classified'
            ORDER BY ClassifiedUtc DESC
            LIMIT 1;
            """,
            new { bodyHash, excludeEntryId },
            cancellationToken: ct));
    }

    public async Task MarkSkippedAsync(
        string entryId, SkipReason reason, string bodyHash = "", CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ProcessedMessages
            SET Status = @status, SkipReason = @reason, BodyHash = @bodyHash
            WHERE EntryId = @entryId;
            """,
            new
            {
                entryId,
                status = nameof(ProcessedStatus.Skipped),
                reason = reason.ToString(),
                bodyHash,
            },
            cancellationToken: ct));
    }

    public async Task MarkClassifiedAsync(
        string entryId,
        string bodyHash,
        string modelName,
        string? rawJson,
        DateTimeOffset classifiedUtc,
        CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ProcessedMessages
            SET Status = @status, BodyHash = @bodyHash, ModelName = @modelName,
                RawLlmJson = @rawJson, ClassifiedUtc = @classifiedUtc, LastError = NULL
            WHERE EntryId = @entryId;
            """,
            new
            {
                entryId,
                status = nameof(ProcessedStatus.Classified),
                bodyHash,
                modelName,
                rawJson,
                classifiedUtc,
            },
            cancellationToken: ct));
    }

    public async Task MarkFailedAsync(string entryId, string error, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ProcessedMessages
            SET Status = @status, Attempts = Attempts + 1, LastError = @error
            WHERE EntryId = @entryId;
            """,
            new { entryId, status = nameof(ProcessedStatus.Failed), error },
            cancellationToken: ct));
    }

    /// <summary>
    /// How many messages failed to classify, and why the most recent one did.
    /// </summary>
    /// <remarks>
    /// Exists so the review window can explain an empty list. Without it the UI says
    /// "nothing waiting" whether the inbox was genuinely uninteresting or every single
    /// message failed — which is exactly the case where the user most needs to be told
    /// something, and the one where silence is most misleading.
    /// </remarks>
    public async Task<FailureSummary> GetFailureSummaryAsync(CancellationToken ct = default)
    {
        using var connection = _database.Open();

        // Read as scalars rather than materializing a record: SQLite's COUNT returns
        // INTEGER (Int64), which will not bind to an `int` constructor parameter.
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM ProcessedMessages WHERE Status = 'Failed';",
            cancellationToken: ct));

        if (count == 0)
        {
            return new FailureSummary(0, null);
        }

        var lastError = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT LastError FROM ProcessedMessages
            WHERE Status = 'Failed' AND LastError IS NOT NULL
            ORDER BY ReceivedUtc DESC
            LIMIT 1;
            """,
            cancellationToken: ct));

        return new FailureSummary((int)count, lastError);
    }

    /// <summary>Backs a "retry failed messages" command in the tray menu.</summary>
    public async Task<IReadOnlyList<string>> GetFailedEntryIdsAsync(
        int maxAttempts, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT EntryId FROM ProcessedMessages
            WHERE Status = 'Failed' AND Attempts < @maxAttempts
            ORDER BY ReceivedUtc DESC;
            """,
            new { maxAttempts },
            cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>Requeues a message so the worker picks it up again.</summary>
    public async Task ResetToQueuedAsync(string entryId, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ProcessedMessages SET Status = @status WHERE EntryId = @entryId;",
            new { entryId, status = nameof(ProcessedStatus.Queued) },
            cancellationToken: ct));
    }

    /// <summary>
    /// Retention. Drops old messages whose suggestions are all resolved, and strips
    /// raw model output once it is no longer useful for prompt tuning.
    /// </summary>
    public async Task<int> PruneAsync(
        DateTimeOffset olderThan, DateTimeOffset rawJsonOlderThan, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ProcessedMessages
            SET RawLlmJson = NULL
            WHERE RawLlmJson IS NOT NULL AND ClassifiedUtc < @rawJsonOlderThan;
            """,
            new { rawJsonOlderThan },
            cancellationToken: ct));

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM ProcessedMessages
            WHERE ReceivedUtc < @olderThan
              AND NOT EXISTS (
                  SELECT 1 FROM Suggestions s
                  WHERE s.EntryId = ProcessedMessages.EntryId AND s.State = 'Pending');
            """,
            new { olderThan },
            cancellationToken: ct));
    }
}
