using Microsoft.Data.Sqlite;

namespace WinFixPro;

public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS OperationHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedAt TEXT NOT NULL,
                Operation TEXT NOT NULL,
                Status TEXT NOT NULL,
                Summary TEXT NOT NULL,
                Json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    public async Task<long> AddAsync(OperationResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO OperationHistory (CreatedAt, Operation, Status, Summary, Json)
            VALUES ($created, $operation, $status, $summary, $json);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$created", result.FinishedAt.ToString("O"));
        command.Parameters.AddWithValue("$operation", result.Operation);
        command.Parameters.AddWithValue("$status", result.Status);
        command.Parameters.AddWithValue("$summary", result.Summary);
        command.Parameters.AddWithValue("$json", result.Json);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var items = new List<HistoryRecord>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, CreatedAt, Operation, Status, Summary, Json
            FROM OperationHistory ORDER BY Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new HistoryRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return items;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }
}
