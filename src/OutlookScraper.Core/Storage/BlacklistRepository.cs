using Dapper;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Storage;

/// <summary>Blacklist rules and the user overrides that rescue tags from them.</summary>
public sealed class BlacklistRepository(Database database)
{
    private readonly Database _database = database;

    /// <summary>
    /// Adds a rule, or returns the existing one for the same (category, key) pair.
    /// Blacklisting the same kind of thing twice is a no-op, not an error.
    /// </summary>
    public async Task<BlacklistEntry> UpsertAsync(BlacklistEntry entry, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var existingId = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Id FROM BlacklistTags WHERE Category = @Category AND TopicTagKey = @TopicTagKey;",
            new { entry.Category, entry.TopicTagKey },
            cancellationToken: ct));

        if (existingId is not null)
        {
            // Re-enable it and refresh the embedding — the user asking again is a
            // clear signal they still want this suppressed.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE BlacklistTags
                SET Enabled = 1, Embedding = @Embedding, EmbedModel = @EmbedModel, EmbedDim = @EmbedDim
                WHERE Id = @Id;
                """,
                new
                {
                    Id = existingId,
                    Embedding = VectorMath.ToBytes(entry.Embedding),
                    entry.EmbedModel,
                    entry.EmbedDim,
                },
                cancellationToken: ct));

            return (await GetAsync(Guid.Parse(existingId), ct))!;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO BlacklistTags
                (Id, Category, TopicTag, TopicTagKey, Reason, Embedding, EmbedModel, EmbedDim,
                 SourceEntryId, Enabled, HitCount, CreatedUtc)
            VALUES
                (@Id, @Category, @TopicTag, @TopicTagKey, @Reason, @Embedding, @EmbedModel, @EmbedDim,
                 @SourceEntryId, @Enabled, @HitCount, @CreatedUtc);
            """,
            new
            {
                entry.Id,
                entry.Category,
                entry.TopicTag,
                entry.TopicTagKey,
                entry.Reason,
                Embedding = VectorMath.ToBytes(entry.Embedding),
                entry.EmbedModel,
                entry.EmbedDim,
                entry.SourceEntryId,
                entry.Enabled,
                entry.HitCount,
                entry.CreatedUtc,
            },
            cancellationToken: ct));

        return entry;
    }

    public async Task<BlacklistEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT * FROM BlacklistTags WHERE Id = @id;",
            new { id },
            cancellationToken: ct));

        return row?.ToEntry();
    }

    public async Task<IReadOnlyList<BlacklistEntry>> GetEnabledAsync(CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT * FROM BlacklistTags WHERE Enabled = 1;",
            cancellationToken: ct));

        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task<IReadOnlyList<BlacklistEntry>> GetAllAsync(CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT * FROM BlacklistTags ORDER BY CreatedUtc DESC;",
            cancellationToken: ct));

        return rows.Select(r => r.ToEntry()).ToList();
    }

    /// <summary>Rules whose embedding is absent or was produced by a different model.</summary>
    public async Task<IReadOnlyList<BlacklistEntry>> GetNeedingEmbeddingAsync(
        string currentModel, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT * FROM BlacklistTags
            WHERE Embedding IS NULL OR EmbedModel IS NULL OR EmbedModel <> @currentModel;
            """,
            new { currentModel },
            cancellationToken: ct));

        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task SetEmbeddingAsync(
        Guid id, float[] embedding, string model, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE BlacklistTags
            SET Embedding = @embedding, EmbedModel = @model, EmbedDim = @dim
            WHERE Id = @id;
            """,
            new { id, embedding = VectorMath.ToBytes(embedding), model, dim = embedding.Length },
            cancellationToken: ct));
    }

    public async Task IncrementHitCountAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE BlacklistTags SET HitCount = HitCount + 1 WHERE Id = @id;",
            new { id },
            cancellationToken: ct));
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE BlacklistTags SET Enabled = @enabled WHERE Id = @id;",
            new { id, enabled },
            cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM BlacklistTags WHERE Id = @id;",
            new { id },
            cancellationToken: ct));
    }

    /// <summary>
    /// Records that this rule over-matched a particular tag. Written when the user
    /// rescues a soft-suppressed suggestion, and honoured on every later match.
    /// </summary>
    public async Task AddExceptionAsync(
        Guid tagId, string topicTagKey, DateTimeOffset createdUtc, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR IGNORE INTO BlacklistExceptions (TagId, TopicTagKey, CreatedUtc)
            VALUES (@tagId, @topicTagKey, @createdUtc);
            """,
            new { tagId, topicTagKey, createdUtc },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<BlacklistException>> GetExceptionsAsync(CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var rows = await connection.QueryAsync<BlacklistException>(new CommandDefinition(
            "SELECT TagId, TopicTagKey, CreatedUtc FROM BlacklistExceptions;",
            cancellationToken: ct));

        return rows.ToList();
    }

    /// <summary>
    /// Dapper cannot map the BLOB column straight onto <c>float[]</c>, so rows land
    /// here first and get converted explicitly.
    /// </summary>
    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Category { get; init; } = "";
        public string TopicTag { get; init; } = "";
        public string TopicTagKey { get; init; } = "";
        public string Reason { get; init; } = "";
        public byte[]? Embedding { get; init; }
        public string? EmbedModel { get; init; }
        public int? EmbedDim { get; init; }
        public string? SourceEntryId { get; init; }
        public bool Enabled { get; init; }
        public int HitCount { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }

        public BlacklistEntry ToEntry() => new()
        {
            Id = Id,
            Category = Category,
            TopicTag = TopicTag,
            TopicTagKey = TopicTagKey,
            Reason = Reason,
            Embedding = VectorMath.FromBytes(Embedding),
            EmbedModel = EmbedModel,
            EmbedDim = EmbedDim,
            SourceEntryId = SourceEntryId,
            Enabled = Enabled,
            HitCount = HitCount,
            CreatedUtc = CreatedUtc,
        };
    }
}
