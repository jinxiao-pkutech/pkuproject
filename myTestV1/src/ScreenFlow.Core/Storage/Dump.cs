using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Storage;

public sealed record DumpResult(string Sha256, long Lines, long Bytes);

/// <summary>
/// Canonical export of an index (02 §7.5): tables in name order, rows in primary-key order, one
/// <c>table\tJSON</c> line each, with <c>meta.builtAt</c> excluded.
/// <para>
/// This is how determinism is actually checked: two builds over the same input must produce the
/// same SHA-256 here. So the export must depend on nothing but the facts — not on insert order, not
/// on the clock, and not on the order a producer discovered things in.
/// </para>
/// </summary>
public static class ScreenFlowDump
{
    /// <summary>Excluded because it records when the build ran, which is not a fact about the code.</summary>
    public const string ExcludedMetaKey = MetaKeys.BuiltAt;

    /// <summary>
    /// <c>diagnostics.seq</c> is a synthetic autoincrement key, so it encodes insert order rather
    /// than content. The column is omitted and the rows are ordered by their content instead, which
    /// is what makes the export genuinely order-independent.
    /// </summary>
    public const string SyntheticKeyTable = "diagnostics";

    public static DumpResult Write(IndexDb db, Stream destination)
    {
        using var hasher = System.Security.Cryptography.SHA256.Create();
        using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);

        long lines = 0;
        long bytes = 0;

        foreach (var table in IndexDb.SchemaTables.OrderBy(t => t, StringComparer.Ordinal))
        {
            foreach (var json in RowsOf(db, table))
            {
                var line = table + "\t" + json + "\n";
                var encoded = Encoding.UTF8.GetBytes(line);
                hasher.TransformBlock(encoded, 0, encoded.Length, null, 0);
                writer.Write(line);
                lines++;
                bytes += encoded.Length;
            }
        }

        writer.Flush();
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return new DumpResult(
            Convert.ToHexString(hasher.Hash ?? Array.Empty<byte>()).ToLowerInvariant(),
            lines,
            bytes);
    }

    /// <summary>Exports to a file and returns its hash, which is what <c>index dump</c> reports.</summary>
    public static DumpResult WriteToFile(IndexDb db, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        return Write(db, stream);
    }

    /// <summary>The hash alone, without keeping the export.</summary>
    public static string Hash(IndexDb db)
    {
        using var stream = new MemoryStream();
        return Write(db, stream).Sha256;
    }

    private static IEnumerable<string> RowsOf(IndexDb db, string table)
    {
        var columns = ColumnsOf(db, table);
        var primaryKey = PrimaryKeyOf(db, table);

        var exported = table == SyntheticKeyTable
            ? columns.Where(c => !string.Equals(c, "seq", StringComparison.Ordinal)).ToList()
            : columns;

        // With no declared key — or a synthetic one — order by every exported column, so the result
        // depends on content only.
        var orderBy = table == SyntheticKeyTable || primaryKey.Count == 0 ? exported : primaryKey;

        var sql = $"SELECT {string.Join(", ", exported.Select(Quote))} FROM {Quote(table)}" +
                  $" ORDER BY {string.Join(", ", orderBy.Select(Quote))};";

        using var command = db.Command(sql);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (table == "meta"
                && string.Equals(reader.GetString(reader.GetOrdinal("key")), ExcludedMetaKey, StringComparison.Ordinal))
                continue;

            yield return RowToJson(reader, exported);
        }
    }

    /// <summary>
    /// A row as canonical JSON: keys sorted, no whitespace. A column holding a JSON document is
    /// inlined and canonicalised too, so a difference in key order inside a <c>json</c> column
    /// cannot change the hash.
    /// </summary>
    private static string RowToJson(SqliteDataReader reader, IReadOnlyList<string> columns)
    {
        var row = new JsonObject();
        for (var i = 0; i < columns.Count; i++)
        {
            var name = columns[i];
            if (reader.IsDBNull(i)) { row[name] = null; continue; }

            row[name] = reader.GetFieldType(i) switch
            {
                var t when t == typeof(long) => JsonValue.Create(reader.GetInt64(i)),
                var t when t == typeof(double) => JsonValue.Create(reader.GetDouble(i)),
                _ => EmbeddedJsonOrString(reader.GetString(i))
            };
        }

        return Ids.CanonicalJson(row);
    }

    private static JsonNode? EmbeddedJsonOrString(string value)
    {
        if (value.Length < 2 || (value[0] != '{' && value[0] != '[')) return JsonValue.Create(value);
        try
        {
            return JsonNode.Parse(value);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    private static IReadOnlyList<string> ColumnsOf(IndexDb db, string table)
    {
        using var command = db.Command($"SELECT name FROM pragma_table_info($t) ORDER BY cid;");
        command.Parameters.AddWithValue("$t", table);
        using var reader = command.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static IReadOnlyList<string> PrimaryKeyOf(IndexDb db, string table)
    {
        using var command = db.Command("SELECT name, pk FROM pragma_table_info($t) WHERE pk > 0 ORDER BY pk;");
        command.Parameters.AddWithValue("$t", table);
        using var reader = command.ExecuteReader();

        var key = new List<string>();
        while (reader.Read()) key.Add(reader.GetString(0));
        return key;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
