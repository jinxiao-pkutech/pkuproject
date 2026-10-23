using System.Reflection;
using Microsoft.Data.Sqlite;

namespace ScreenFlow.Core.Storage;

/// <summary>
/// Two different IR records were given the same id (02 §5.6). Fatal: merging them would silently
/// conflate two distinct symbols or sites, which is worse than failing the build.
/// </summary>
public sealed class IdCollisionException : Exception
{
    public const string ErrorCode = "E_ID_COLLISION";

    public IdCollisionException(string table, string id, string existingJson, string incomingJson)
        : base($"{ErrorCode}: id '{id}' in table '{table}' is used by two different records. " +
               $"Existing: {Excerpt(existingJson)}. Incoming: {Excerpt(incomingJson)}.")
    {
        Table = table;
        Id = id;
    }

    public string Table { get; }
    public string Id { get; }

    private static string Excerpt(string json) => json.Length <= 160 ? json : json[..160] + "…";
}

/// <summary>Whether the connection is tuned for bulk writing or for querying (02 §7.1).</summary>
public enum DbMode
{
    /// <summary><c>synchronous=OFF</c>: the file is a throwaway <c>.building</c> copy until sealed.</summary>
    Build,

    /// <summary><c>WAL</c> with <c>synchronous=NORMAL</c>.</summary>
    Query
}

/// <summary>
/// The index database (02 §7.1): one file per analysis profile. Owns the connection, the PRAGMAs
/// and the schema.
/// </summary>
public sealed class IndexDb : IDisposable
{
    private const string SchemaResourceName = "ScreenFlow.Core.Storage.Schema.sql";

    /// <summary>Default location, relative to the project root (02 §7.1).</summary>
    public const string DefaultDirectory = ".screenflow";

    private readonly SqliteConnection _connection;

    private IndexDb(SqliteConnection connection, string path, DbMode mode)
    {
        _connection = connection;
        Path = path;
        Mode = mode;
    }

    public string Path { get; }
    public DbMode Mode { get; private set; }

    internal SqliteConnection Connection => _connection;

    public static string DefaultPath(string projectRoot, string profileId)
        => System.IO.Path.Combine(projectRoot, DefaultDirectory, profileId + ".db");

    /// <summary>Creates a new database file with the schema applied, replacing any existing file.</summary>
    public static IndexDb Create(string path, DbMode mode = DbMode.Build)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(fullPath)) File.Delete(fullPath);

        var db = Open(fullPath, mode, createIfMissing: true);
        db.ApplySchema();
        return db;
    }

    public static IndexDb Open(string path, DbMode mode = DbMode.Query, bool createIfMissing = false)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!createIfMissing && !File.Exists(fullPath))
            throw new FileNotFoundException($"Index database not found: {path}", fullPath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        var db = new IndexDb(connection, fullPath, mode);
        db.ApplyPragmas(mode);
        return db;
    }

    /// <summary>An in-memory database, used by the tests and by fixture runs.</summary>
    public static IndexDb CreateInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Cache=Private;Foreign Keys=True");
        connection.Open();
        var db = new IndexDb(connection, ":memory:", DbMode.Build);
        db.ApplyPragmas(DbMode.Build);
        db.ApplySchema();
        return db;
    }

    private void ApplyPragmas(DbMode mode)
    {
        Execute("PRAGMA foreign_keys=ON;");
        Execute("PRAGMA temp_store=MEMORY;");

        if (mode == DbMode.Build)
        {
            // The build writes to a .building file that is discarded on failure, so durability
            // during the build buys nothing and costs a great deal on a 2.4M-line project.
            Execute("PRAGMA journal_mode=MEMORY;");
            Execute("PRAGMA synchronous=OFF;");
        }
        else
        {
            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
        }

        Mode = mode;
    }

    /// <summary>
    /// Finishes a build: analyses for the query planner, then switches to the query-time PRAGMAs
    /// (02 §7.1, T03 §7).
    /// </summary>
    public void SealForQuerying()
    {
        Execute("ANALYZE;");
        ApplyPragmas(DbMode.Query);
    }

    private void ApplySchema() => Execute(ReadSchema());

    public static string ReadSchema()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{SchemaResourceName}' is missing. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Table names in the schema, in the order the DDL declares them.</summary>
    public static IReadOnlyList<string> SchemaTables { get; } = ParseTableNames(ReadSchema());

    private static IReadOnlyList<string> ParseTableNames(string schema)
        => System.Text.RegularExpressions.Regex
            .Matches(schema, @"CREATE TABLE (?<name>\w+)")
            .Select(m => m.Groups["name"].Value)
            .ToArray();

    public void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public SqliteTransaction BeginTransaction() => _connection.BeginTransaction();

    public long Count(string table)
    {
        using var command = Command($"SELECT COUNT(*) FROM {table};");
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>Row counts for every table, written into <c>meta.counts</c> (02 §7.4).</summary>
    public IReadOnlyDictionary<string, long> AllCounts()
        => SchemaTables.ToDictionary(t => t, Count, StringComparer.Ordinal);

    public void SetMeta(string key, string value)
    {
        using var command = Command("INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v);");
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var command = Command("SELECT value FROM meta WHERE key = $k;");
        command.Parameters.AddWithValue("$k", key);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
        SqliteConnection.ClearPool(_connection);
    }
}

/// <summary>The <c>meta</c> keys of 02 §7.4.</summary>
public static class MetaKeys
{
    public const string IrVersion = "irVersion";
    public const string RulesVersion = "rulesVersion";
    public const string ProfileId = "profileId";
    public const string PlatformDetected = "platformDetected";
    public const string Derivation = "derivation";
    public const string UnityDeclared = "unityDeclared";
    public const string UnityCache = "unityCache";
    public const string BuiltAt = "builtAt";
    public const string Fingerprint = "fingerprint";
    public const string Counts = "counts";
    public const string DisabledFeatures = "disabledFeatures";

    public static readonly IReadOnlyList<string> All = new[]
    {
        IrVersion, RulesVersion, ProfileId, PlatformDetected, Derivation, UnityDeclared,
        UnityCache, BuiltAt, Fingerprint, Counts, DisabledFeatures
    };
}
