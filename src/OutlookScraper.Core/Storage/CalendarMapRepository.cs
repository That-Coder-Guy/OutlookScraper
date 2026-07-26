using Dapper;

namespace OutlookScraper.Core.Storage;

/// <summary>
/// Local record of what has already been booked.
/// </summary>
/// <remarks>
/// This is the fast half of the double-booking guard — it catches the common race
/// where the user hits the toast button and the review window button for the same
/// suggestion. The authoritative half is the deterministic Google event id, which
/// keeps working even if this table is lost entirely.
/// </remarks>
public sealed class CalendarMapRepository(Database database)
{
    private readonly Database _database = database;

    public async Task<CalendarMapping?> GetAsync(Guid suggestionId, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        return await connection.QuerySingleOrDefaultAsync<CalendarMapping>(new CommandDefinition(
            "SELECT * FROM CalendarEventMap WHERE SuggestionId = @suggestionId;",
            new { suggestionId },
            cancellationToken: ct));
    }

    public async Task InsertAsync(CalendarMapping mapping, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR REPLACE INTO CalendarEventMap
                (SuggestionId, CalendarId, GoogleEventId, HtmlLink, CreatedUtc)
            VALUES
                (@SuggestionId, @CalendarId, @GoogleEventId, @HtmlLink, @CreatedUtc);
            """,
            mapping,
            cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid suggestionId, CancellationToken ct = default)
    {
        using var connection = _database.Open();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CalendarEventMap WHERE SuggestionId = @suggestionId;",
            new { suggestionId },
            cancellationToken: ct));
    }
}

public sealed record CalendarMapping(
    Guid SuggestionId,
    string CalendarId,
    string GoogleEventId,
    string? HtmlLink,
    DateTimeOffset CreatedUtc);
