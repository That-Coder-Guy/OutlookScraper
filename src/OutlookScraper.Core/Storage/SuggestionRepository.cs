using Dapper;
using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Storage;

/// <summary>Pending, resolved and suppressed event suggestions.</summary>
public sealed class SuggestionRepository(Database database)
{
    private readonly Database _database = database;

    public async Task InsertAsync(EventSuggestion suggestion, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Suggestions
                (Id, EntryId, Title, FoodDescription, Location, Organization,
                 StartUtc, EndUtc, IanaTimeZone, IsAllDay, DateIsExplicit, NeedsDateReview,
                 Category, TopicTag, TopicTagKey, Reason, Confidence,
                 SenderName, SenderAddress, Subject, BodyExcerpt,
                 State, SuppressedByTagId, SuppressStage, SuppressScore,
                 CreatedUtc, ResolvedUtc)
            VALUES
                (@Id, @EntryId, @Title, @FoodDescription, @Location, @Organization,
                 @StartUtc, @EndUtc, @IanaTimeZone, @IsAllDay, @DateIsExplicit, @NeedsDateReview,
                 @Category, @TopicTag, @TopicTagKey, @Reason, @Confidence,
                 @SenderName, @SenderAddress, @Subject, @BodyExcerpt,
                 @State, @SuppressedByTagId, @SuppressStage, @SuppressScore,
                 @CreatedUtc, @ResolvedUtc);
            """,
            ToParameters(suggestion),
            cancellationToken: ct));
    }

    public async Task<EventSuggestion?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        return await connection.QuerySingleOrDefaultAsync<EventSuggestion>(new CommandDefinition(
            "SELECT * FROM Suggestions WHERE Id = @id;",
            new { id },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<EventSuggestion>> GetByStateAsync(
        SuggestionState state, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var rows = await connection.QueryAsync<EventSuggestion>(new CommandDefinition(
            "SELECT * FROM Suggestions WHERE State = @state ORDER BY CreatedUtc DESC;",
            new { state = state.ToString() },
            cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<int> CountByStateAsync(SuggestionState state, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        return (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Suggestions WHERE State = @state;",
            new { state = state.ToString() },
            cancellationToken: ct));
    }

    /// <summary>
    /// Marks a suggestion resolved. <paramref name="state"/> records which of Add,
    /// Blacklist or Dismiss the user chose.
    /// </summary>
    public async Task SetStateAsync(
        Guid id, SuggestionState state, DateTimeOffset resolvedUtc, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Suggestions SET State = @state, ResolvedUtc = @resolvedUtc WHERE Id = @id;",
            new { id, state = state.ToString(), resolvedUtc },
            cancellationToken: ct));
    }

    /// <summary>
    /// Records a blacklist match against a suggestion, including the stage and score
    /// that produced it. The audit fields are what make a mis-tuned threshold
    /// diagnosable instead of a silent mystery.
    /// </summary>
    public async Task SuppressAsync(
        Guid id, BlacklistMatch match, DateTimeOffset resolvedUtc, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Suggestions
            SET State = @state, SuppressedByTagId = @tagId, SuppressStage = @stage,
                SuppressScore = @score, ResolvedUtc = @resolvedUtc
            WHERE Id = @id;
            """,
            new
            {
                id,
                state = nameof(SuggestionState.Suppressed),
                tagId = match.TagId,
                stage = match.Stage.ToString(),
                score = match.Score,
                resolvedUtc,
            },
            cancellationToken: ct));
    }

    /// <summary>Pulls a suggestion back out of suppression after a user rescue.</summary>
    public async Task UnsuppressAsync(Guid id, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Suggestions
            SET State = @state, SuppressedByTagId = NULL, SuppressStage = 'None',
                SuppressScore = NULL, ResolvedUtc = NULL
            WHERE Id = @id;
            """,
            new { id, state = nameof(SuggestionState.Pending) },
            cancellationToken: ct));
    }

    /// <summary>
    /// Everything suppressed by one rule. Backs the un-suppress sweep that runs when a
    /// blacklist rule is deleted.
    /// </summary>
    public async Task<IReadOnlyList<EventSuggestion>> GetSuppressedByTagAsync(
        Guid tagId, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        var rows = await connection.QueryAsync<EventSuggestion>(new CommandDefinition(
            "SELECT * FROM Suggestions WHERE SuppressedByTagId = @tagId;",
            new { tagId },
            cancellationToken: ct));

        return rows.ToList();
    }

    public async Task UpdateEventDetailsAsync(EventSuggestion suggestion, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Suggestions
            SET Title = @Title, Location = @Location, StartUtc = @StartUtc, EndUtc = @EndUtc,
                IanaTimeZone = @IanaTimeZone, IsAllDay = @IsAllDay, NeedsDateReview = @NeedsDateReview
            WHERE Id = @Id;
            """,
            ToParameters(suggestion),
            cancellationToken: ct));
    }

    private static object ToParameters(EventSuggestion s) => new
    {
        s.Id,
        s.EntryId,
        s.Title,
        s.FoodDescription,
        s.Location,
        s.Organization,
        s.StartUtc,
        s.EndUtc,
        s.IanaTimeZone,
        s.IsAllDay,
        s.DateIsExplicit,
        s.NeedsDateReview,
        s.Category,
        s.TopicTag,
        s.TopicTagKey,
        s.Reason,
        s.Confidence,
        s.SenderName,
        s.SenderAddress,
        s.Subject,
        s.BodyExcerpt,
        State = s.State.ToString(),
        s.SuppressedByTagId,
        SuppressStage = s.SuppressStage.ToString(),
        s.SuppressScore,
        s.CreatedUtc,
        s.ResolvedUtc,
    };
}
