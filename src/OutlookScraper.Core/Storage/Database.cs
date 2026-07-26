using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace OutlookScraper.Core.Storage;

/// <summary>
/// Connection factory. Applies the PRAGMAs that make concurrent access painless and
/// registers the Dapper type handlers the repositories rely on.
/// </summary>
/// <remarks>
/// WAL is the important one: the STA thread, the classification worker and the UI
/// thread all touch this database, and WAL lets readers proceed while a write is in
/// flight instead of throwing SQLITE_BUSY at each other.
/// </remarks>
public sealed class Database : IDisposable
{
    private readonly string _connectionString;
    private readonly bool _isMemory;

    /// <summary>
    /// A shared in-memory database is destroyed the moment its *last* connection
    /// closes, and the repositories deliberately open and close per call. This holds
    /// one connection open for the lifetime of the instance so the schema survives
    /// between calls in tests.
    /// </summary>
    private readonly SqliteConnection? _keepAlive;

    private static readonly object HandlerGate = new();
    private static bool _handlersRegistered;

    public Database(string databasePath)
        : this(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
            }.ToString(),
            isMemory: false)
    {
    }

    private Database(string connectionString, bool isMemory)
    {
        _connectionString = connectionString;
        _isMemory = isMemory;

        RegisterTypeHandlers();

        if (_isMemory)
        {
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
    }

    /// <summary>An isolated in-memory database, for tests.</summary>
    public static Database InMemory()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"memdb-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        return new Database(connectionString, isMemory: true);
    }

    public IDbConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();

        // journal_mode is a no-op (and reports "memory") for an in-memory database,
        // so it is only worth setting for the real file.
        pragma.CommandText = _isMemory
            ? "PRAGMA foreign_keys=ON;"
            : """
              PRAGMA journal_mode=WAL;
              PRAGMA synchronous=NORMAL;
              PRAGMA foreign_keys=ON;
              PRAGMA busy_timeout=5000;
              """;
        pragma.ExecuteNonQuery();

        return connection;
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private static void RegisterTypeHandlers()
    {
        lock (HandlerGate)
        {
            if (_handlersRegistered)
            {
                return;
            }

            SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
            SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
            SqlMapper.AddTypeHandler(new GuidHandler());
            SqlMapper.AddTypeHandler(new NullableGuidHandler());
            _handlersRegistered = true;
        }
    }

    /// <summary>
    /// Round-trips through ISO-8601 in UTC. Sortable as text, which matters because
    /// several queries order and range-filter on these columns.
    /// </summary>
    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.ToUniversalTime().ToString("O");

        public override DateTimeOffset Parse(object value) =>
            DateTimeOffset.Parse(
                (string)value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value) =>
            parameter.Value = value?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value;

        public override DateTimeOffset? Parse(object value) =>
            value is null or DBNull
                ? null
                : DateTimeOffset.Parse(
                    (string)value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private sealed class GuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(IDbDataParameter parameter, Guid value) =>
            parameter.Value = value.ToString("D");

        public override Guid Parse(object value) => Guid.Parse((string)value);
    }

    private sealed class NullableGuidHandler : SqlMapper.TypeHandler<Guid?>
    {
        public override void SetValue(IDbDataParameter parameter, Guid? value) =>
            parameter.Value = value?.ToString("D") ?? (object)DBNull.Value;

        public override Guid? Parse(object value) =>
            value is null or DBNull ? null : Guid.Parse((string)value);
    }
}
