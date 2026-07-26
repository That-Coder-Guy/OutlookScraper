using System.Data;
using Dapper;

namespace OutlookScraper.Core.Storage;

/// <summary>
/// Stepwise schema migrations tracked by <c>PRAGMA user_version</c>.
/// </summary>
/// <remarks>
/// Hand-rolled rather than EF Core. Six tables do not justify a migrations toolchain
/// or the cold-start cost, and this way the whole thing is forty lines and directly
/// testable — including the upgrade path from an existing database.
/// </remarks>
public static class Migrations
{
    /// <summary>Bump this and add a matching case in <see cref="Steps"/>.</summary>
    public const int LatestVersion = 1;

    public static int GetVersion(IDbConnection connection) =>
        connection.ExecuteScalar<int>("PRAGMA user_version;");

    public static void Apply(IDbConnection connection)
    {
        var current = GetVersion(connection);

        for (var next = current + 1; next <= LatestVersion; next++)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(Steps(next), transaction: transaction);
            transaction.Commit();

            // PRAGMA user_version does not accept a parameter placeholder.
            connection.Execute($"PRAGMA user_version = {next};");
        }
    }

    private static string Steps(int version) => version switch
    {
        1 => V1,
        _ => throw new InvalidOperationException($"No migration defined for version {version}."),
    };

    private const string V1 = """
        -- The idempotency backbone. New mail can arrive via ItemAdd, NewMailEx or the
        -- periodic sweep; inserting here is what decides whether work actually happens.
        CREATE TABLE ProcessedMessages (
            EntryId       TEXT PRIMARY KEY,
            StoreId       TEXT NOT NULL,
            BodyHash      TEXT NOT NULL,
            Subject       TEXT NOT NULL,
            SenderAddress TEXT NOT NULL,
            SenderName    TEXT NOT NULL,
            ReceivedUtc   TEXT NOT NULL,
            Status        TEXT NOT NULL,
            SkipReason    TEXT NOT NULL DEFAULT 'None',
            Attempts      INTEGER NOT NULL DEFAULT 0,
            LastError     TEXT NULL,
            ClassifiedUtc TEXT NULL,
            ModelName     TEXT NULL,
            RawLlmJson    TEXT NULL
        );
        CREATE INDEX IX_PM_Received ON ProcessedMessages(ReceivedUtc);
        CREATE INDEX IX_PM_Status   ON ProcessedMessages(Status);
        CREATE INDEX IX_PM_BodyHash ON ProcessedMessages(BodyHash);

        -- Blacklist rules, keyed on the model's topic tag. Embedding may be NULL when
        -- no embedding model is installed; stages 0-2 of the cascade still apply.
        CREATE TABLE BlacklistTags (
            Id            TEXT PRIMARY KEY,
            Category      TEXT NOT NULL,
            TopicTag      TEXT NOT NULL,
            TopicTagKey   TEXT NOT NULL,
            Reason        TEXT NOT NULL DEFAULT '',
            Embedding     BLOB NULL,
            EmbedModel    TEXT NULL,
            EmbedDim      INTEGER NULL,
            SourceEntryId TEXT NULL,
            Enabled       INTEGER NOT NULL DEFAULT 1,
            HitCount      INTEGER NOT NULL DEFAULT 0,
            CreatedUtc    TEXT NOT NULL
        );
        CREATE UNIQUE INDEX UX_BL_Key ON BlacklistTags(Category, TopicTagKey);

        -- Detected events awaiting a decision. Id is the toast correlation key and is
        -- the only thing the toast payload carries.
        CREATE TABLE Suggestions (
            Id                TEXT PRIMARY KEY,
            EntryId           TEXT NOT NULL REFERENCES ProcessedMessages(EntryId) ON DELETE CASCADE,
            Title             TEXT NOT NULL DEFAULT '',
            FoodDescription   TEXT NOT NULL DEFAULT '',
            Location          TEXT NOT NULL DEFAULT '',
            Organization      TEXT NOT NULL DEFAULT '',
            StartUtc          TEXT NULL,
            EndUtc            TEXT NULL,
            IanaTimeZone      TEXT NOT NULL DEFAULT 'UTC',
            IsAllDay          INTEGER NOT NULL DEFAULT 0,
            DateIsExplicit    INTEGER NOT NULL DEFAULT 0,
            NeedsDateReview   INTEGER NOT NULL DEFAULT 0,
            Category          TEXT NOT NULL,
            TopicTag          TEXT NOT NULL,
            TopicTagKey       TEXT NOT NULL,
            Reason            TEXT NOT NULL DEFAULT '',
            Confidence        REAL NOT NULL DEFAULT 0,
            SenderName        TEXT NOT NULL DEFAULT '',
            SenderAddress     TEXT NOT NULL DEFAULT '',
            Subject           TEXT NOT NULL DEFAULT '',
            BodyExcerpt       TEXT NOT NULL DEFAULT '',
            State             TEXT NOT NULL,
            SuppressedByTagId TEXT NULL REFERENCES BlacklistTags(Id) ON DELETE SET NULL,
            SuppressStage     TEXT NOT NULL DEFAULT 'None',
            SuppressScore     REAL NULL,
            CreatedUtc        TEXT NOT NULL,
            ResolvedUtc       TEXT NULL
        );
        CREATE INDEX IX_Sug_State   ON Suggestions(State, CreatedUtc DESC);
        CREATE INDEX IX_Sug_EntryId ON Suggestions(EntryId);

        -- User rescues from the soft-suppression band. Excluded from future matching so
        -- the same rule cannot re-swallow a tag the user explicitly pulled back out.
        CREATE TABLE BlacklistExceptions (
            TagId       TEXT NOT NULL REFERENCES BlacklistTags(Id) ON DELETE CASCADE,
            TopicTagKey TEXT NOT NULL,
            CreatedUtc  TEXT NOT NULL,
            PRIMARY KEY (TagId, TopicTagKey)
        );

        -- Local half of the double-booking guard. The authoritative half is the
        -- deterministic Google event id, which survives losing this table entirely.
        CREATE TABLE CalendarEventMap (
            SuggestionId  TEXT PRIMARY KEY REFERENCES Suggestions(Id) ON DELETE CASCADE,
            CalendarId    TEXT NOT NULL,
            GoogleEventId TEXT NOT NULL,
            HtmlLink      TEXT NULL,
            CreatedUtc    TEXT NOT NULL
        );
        CREATE UNIQUE INDEX UX_Cal_Event ON CalendarEventMap(CalendarId, GoogleEventId);

        -- Machine-managed runtime state only. Anything a human might want to edit
        -- lives in settings.json instead.
        CREATE TABLE AppState (
            Key   TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );
        """;
}
