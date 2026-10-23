using Microsoft.Data.Sqlite;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Storage;

/// <summary>
/// Writes IR records into the base tables (02 §7.2, pipeline phase P4).
/// <para>
/// Records are buffered, sorted by id and written in transactions of
/// <see cref="DefaultBatchSize"/>, so the same input produces the same database regardless of the
/// order the extractor happened to emit in. A re-emitted identical record is ignored; the same id
/// carrying different content is fatal (02 §5.6).
/// </para>
/// </summary>
public sealed class IrWriter : IDisposable
{
    public const int DefaultBatchSize = 10_000;

    private readonly IndexDb _db;
    private readonly int _batchSize;
    private readonly Func<string, Span, string?>? _snippetHasher;

    private readonly List<FileRec> _files = new();
    private readonly List<SymbolRec> _symbols = new();
    private readonly List<ImplRec> _impls = new();
    private readonly List<CallsiteRec> _callsites = new();
    private readonly List<AssignRec> _assigns = new();
    private readonly List<CreationRec> _creations = new();
    private readonly List<CfgRec> _cfgs = new();
    private readonly List<MarkerRec> _markers = new();
    private readonly List<DiagRec> _diagnostics = new();
    private readonly Dictionary<string, EvidenceRow> _evidence = new(StringComparer.Ordinal);

    private int _buffered;
    private bool _faulted;

    /// <param name="snippetHasher">
    /// Supplies <c>evidence.snippet_hash</c>, which the IR does not carry: the hash needs the source
    /// text, and only the caller knows the project root. When absent the column is left null, and
    /// <c>evidence get</c> then cannot report staleness for that row.
    /// </param>
    public IrWriter(IndexDb db, int batchSize = DefaultBatchSize, Func<string, Span, string?>? snippetHasher = null)
    {
        _db = db;
        _batchSize = batchSize <= 0 ? DefaultBatchSize : batchSize;
        _snippetHasher = snippetHasher;
    }

    public MetaRec? Meta { get; private set; }

    /// <summary>Phase recorded on diagnostics written from here.</summary>
    public string Phase { get; set; } = "P4";

    public void Write(IrRecord record)
    {
        switch (record)
        {
            case MetaRec meta: Meta = meta; break;
            case FileRec file: _files.Add(file); break;
            case SymbolRec symbol: _symbols.Add(symbol); CollectEvidence(symbol.File, symbol.Span); break;
            case ImplRec impl: _impls.Add(impl); break;
            case CallsiteRec callsite:
                _callsites.Add(callsite);
                CollectEvidence(FileOf(callsite.CallerId, callsite), callsite.Span);
                break;
            case AssignRec assign:
                _assigns.Add(assign);
                CollectEvidence(FileOf(assign.MethodId, assign), assign.Span);
                break;
            case CreationRec creation:
                _creations.Add(creation);
                CollectEvidence(FileOf(creation.MethodId, creation), creation.Span);
                break;
            case CfgRec cfg: _cfgs.Add(cfg); break;
            case MarkerRec marker: _markers.Add(marker); break;
            case DiagRec diag: _diagnostics.Add(diag); break;
            default: throw new ArgumentException($"Unsupported record type {record.GetType().Name}.", nameof(record));
        }

        if (++_buffered >= _batchSize) Flush();
    }

    public async Task WriteAllAsync(IAsyncEnumerable<IrRecord> records, CancellationToken ct = default)
    {
        await foreach (var record in records.WithCancellation(ct).ConfigureAwait(false))
            Write(record);
        Flush();
    }

    /// <summary>
    /// A callsite, assignment or creation record does not carry its file: it is identified by the
    /// method it sits in. The file is resolved from the symbol table at flush time, so evidence can
    /// still be keyed by (file, span) as 02 §5.6 requires.
    /// </summary>
    private string? FileOf(string methodId, IrRecord _)
        => _symbols.FirstOrDefault(s => s.Id == methodId)?.File ?? LookupSymbolFile(methodId);

    private string? LookupSymbolFile(string symbolId)
    {
        using var command = _db.Command("SELECT file FROM symbols WHERE id = $id;");
        command.Parameters.AddWithValue("$id", symbolId);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private sealed record EvidenceRow(string Id, string File, Span Span);

    private void CollectEvidence(string? file, Span? span)
    {
        if (file is null || span is null) return;
        var id = Ids.EvidenceId(file, span);
        if (_evidence.ContainsKey(id)) return;
        _evidence[id] = new EvidenceRow(id, file, span);
    }

    /// <summary>
    /// Writes everything buffered so far, sorted by id, in one transaction.
    /// <para>
    /// A failure clears the buffers and marks the writer faulted, so the offending batch is not
    /// retried — notably not from <see cref="Dispose"/>, where the exception would replace the real
    /// one. The build that owns this writer discards its whole database anyway (02 §3).
    /// </para>
    /// </summary>
    public void Flush()
    {
        if (_buffered == 0 && _evidence.Count == 0) return;

        try
        {
            FlushCore();
        }
        catch
        {
            _faulted = true;
            ClearBuffers();
            throw;
        }
    }

    private void FlushCore()
    {
        using var transaction = _db.BeginTransaction();

        foreach (var file in _files.OrderBy(f => f.Path, StringComparer.Ordinal)) InsertFile(transaction, file);
        foreach (var symbol in _symbols.OrderBy(s => s.Id, StringComparer.Ordinal)) InsertSymbol(transaction, symbol);
        foreach (var impl in _impls
                     .OrderBy(i => i.TypeId, StringComparer.Ordinal)
                     .ThenBy(i => i.InterfaceMemberId, StringComparer.Ordinal)) InsertImpl(transaction, impl);
        foreach (var callsite in _callsites.OrderBy(c => c.Id, StringComparer.Ordinal)) InsertCallsite(transaction, callsite);
        foreach (var assign in _assigns.OrderBy(a => a.Id, StringComparer.Ordinal)) InsertAssign(transaction, assign);
        foreach (var creation in _creations.OrderBy(c => c.Id, StringComparer.Ordinal)) InsertCreation(transaction, creation);
        foreach (var cfg in _cfgs.OrderBy(c => c.MethodId, StringComparer.Ordinal)) InsertCfg(transaction, cfg);
        foreach (var marker in _markers
                     .OrderBy(m => m.File, StringComparer.Ordinal)
                     .ThenBy(m => m.Line)) InsertMarker(transaction, marker);
        foreach (var evidence in _evidence.Values.OrderBy(e => e.Id, StringComparer.Ordinal)) InsertEvidence(transaction, evidence);
        foreach (var diagnostic in _diagnostics) InsertDiagnostic(transaction, diagnostic);

        transaction.Commit();
        ClearBuffers();
    }

    private void ClearBuffers()
    {
        _files.Clear();
        _symbols.Clear();
        _impls.Clear();
        _callsites.Clear();
        _assigns.Clear();
        _creations.Clear();
        _cfgs.Clear();
        _markers.Clear();
        _diagnostics.Clear();
        _evidence.Clear();
        _buffered = 0;
    }

    // ---------------------------------------------------------------- inserts

    private void InsertFile(SqliteTransaction transaction, FileRec file)
    {
        using var command = _db.Command("""
            INSERT INTO files (path, assembly, encoding, line_count, content_hash)
            VALUES ($p, $a, $e, $l, $h);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$p", file.Path);
        command.Parameters.AddWithValue("$a", file.Assembly);
        command.Parameters.AddWithValue("$e", file.Encoding);
        command.Parameters.AddWithValue("$l", file.LineCount);
        command.Parameters.AddWithValue("$h", file.ContentHash);
        ExecuteIdempotent(command, "files", file.Path, IrJson.ToLine(file),
            "SELECT content_hash FROM files WHERE path = $key;", file.Path, file.ContentHash);
    }

    private void InsertSymbol(SqliteTransaction transaction, SymbolRec symbol)
    {
        var json = IrJson.ToLine(symbol);
        using var command = _db.Command("""
            INSERT INTO symbols (id, kind, name, fq_name, container_id, namespace, assembly, is_source,
                                 file, sl, sc, el, ec, type_kind, base_type_id, json)
            VALUES ($id, $kind, $name, $fq, $container, $ns, $asm, $src, $file, $sl, $sc, $el, $ec, $tk, $base, $json);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", symbol.Id);
        command.Parameters.AddWithValue("$kind", symbol.Kind);
        command.Parameters.AddWithValue("$name", symbol.Name);
        command.Parameters.AddWithValue("$fq", symbol.FqName);
        command.Parameters.AddWithValue("$container", Nullable(symbol.ContainerId));
        command.Parameters.AddWithValue("$ns", Nullable(symbol.Namespace));
        command.Parameters.AddWithValue("$asm", symbol.Assembly);
        command.Parameters.AddWithValue("$src", symbol.IsSource ? 1 : 0);
        command.Parameters.AddWithValue("$file", Nullable(symbol.File));
        command.Parameters.AddWithValue("$sl", Nullable(symbol.Span?.StartLine));
        command.Parameters.AddWithValue("$sc", Nullable(symbol.Span?.StartColumn));
        command.Parameters.AddWithValue("$el", Nullable(symbol.Span?.EndLine));
        command.Parameters.AddWithValue("$ec", Nullable(symbol.Span?.EndColumn));
        command.Parameters.AddWithValue("$tk", Nullable(symbol.TypeKind));
        command.Parameters.AddWithValue("$base", Nullable(symbol.BaseTypeId));
        command.Parameters.AddWithValue("$json", json);
        ExecuteIdempotent(command, "symbols", symbol.Id, json);
    }

    private void InsertImpl(SqliteTransaction transaction, ImplRec impl)
    {
        using var command = _db.Command("""
            INSERT INTO impls (type_id, interface_member_id, impl_member_id) VALUES ($t, $i, $m);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$t", impl.TypeId);
        command.Parameters.AddWithValue("$i", impl.InterfaceMemberId);
        command.Parameters.AddWithValue("$m", impl.ImplMemberId);
        ExecuteIdempotent(command, "impls", impl.TypeId + "/" + impl.InterfaceMemberId, IrJson.ToLine(impl),
            "SELECT impl_member_id FROM impls WHERE type_id || '/' || interface_member_id = $key;",
            impl.TypeId + "/" + impl.InterfaceMemberId, impl.ImplMemberId);
    }

    private void InsertCallsite(SqliteTransaction transaction, CallsiteRec callsite)
    {
        var json = IrJson.ToLine(callsite);
        using var command = _db.Command("""
            INSERT INTO callsites (id, caller_id, target_id, target_is_virtual, reachable, ord, file, sl, json)
            VALUES ($id, $caller, $target, $virtual, $reach, $ord, $file, $sl, $json);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", callsite.Id);
        command.Parameters.AddWithValue("$caller", callsite.CallerId);
        command.Parameters.AddWithValue("$target", Nullable(callsite.TargetId));
        command.Parameters.AddWithValue("$virtual", callsite.TargetIsVirtual ? 1 : 0);
        command.Parameters.AddWithValue("$reach", callsite.Reachable ? 1 : 0);
        command.Parameters.AddWithValue("$ord", callsite.Order);
        command.Parameters.AddWithValue("$file", Nullable(FileOf(callsite.CallerId, callsite)));
        command.Parameters.AddWithValue("$sl", callsite.Span.StartLine);
        command.Parameters.AddWithValue("$json", json);
        ExecuteIdempotent(command, "callsites", callsite.Id, json);
    }

    private void InsertAssign(SqliteTransaction transaction, AssignRec assign)
    {
        var json = IrJson.ToLine(assign);
        using var command = _db.Command("""
            INSERT INTO assigns (id, method_id, target_symbol_id, operator, reachable, ord, file, sl, json)
            VALUES ($id, $method, $target, $op, $reach, $ord, $file, $sl, $json);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", assign.Id);
        command.Parameters.AddWithValue("$method", assign.MethodId);
        command.Parameters.AddWithValue("$target", Nullable(TargetSymbolId(assign.Target)));
        command.Parameters.AddWithValue("$op", assign.Operator);
        command.Parameters.AddWithValue("$reach", assign.Reachable ? 1 : 0);
        command.Parameters.AddWithValue("$ord", assign.Order);
        command.Parameters.AddWithValue("$file", Nullable(FileOf(assign.MethodId, assign)));
        command.Parameters.AddWithValue("$sl", assign.Span.StartLine);
        command.Parameters.AddWithValue("$json", json);
        ExecuteIdempotent(command, "assigns", assign.Id, json);
    }

    /// <summary>
    /// The symbol an assignment target ultimately points at (02 §7.2): a member access or static
    /// member names it directly, and an element access is unwrapped to its receiver.
    /// <para>
    /// A <c>local</c> target yields null: the IR's local value carries a name but no symbol id, so
    /// there is nothing to record, and inventing one would make the column lie.
    /// </para>
    /// </summary>
    internal static string? TargetSymbolId(Value target) => target switch
    {
        MemberAccessValue memberAccess => memberAccess.SymbolId,
        StaticMemberValue staticMember => staticMember.SymbolId,
        ElementAccessValue elementAccess => TargetSymbolId(elementAccess.Receiver),
        _ => null
    };

    private void InsertCreation(SqliteTransaction transaction, CreationRec creation)
    {
        var json = IrJson.ToLine(creation);
        using var command = _db.Command("""
            INSERT INTO creations (id, method_id, type_id, json) VALUES ($id, $method, $type, $json);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", creation.Id);
        command.Parameters.AddWithValue("$method", creation.MethodId);
        command.Parameters.AddWithValue("$type", Nullable(creation.ObjectCreation?.TypeId));
        command.Parameters.AddWithValue("$json", json);
        ExecuteIdempotent(command, "creations", creation.Id, json);
    }

    private void InsertCfg(SqliteTransaction transaction, CfgRec cfg)
    {
        var json = IrJson.ToLine(cfg);
        using var command = _db.Command("INSERT INTO cfgs (method_id, json) VALUES ($m, $json);");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$m", cfg.MethodId);
        command.Parameters.AddWithValue("$json", json);
        ExecuteIdempotent(command, "cfgs", cfg.MethodId, json,
            "SELECT json FROM cfgs WHERE method_id = $key;", cfg.MethodId, json);
    }

    private void InsertMarker(SqliteTransaction transaction, MarkerRec marker)
    {
        var json = IrJson.ToLine(marker);
        using var command = _db.Command("""
            INSERT INTO markers (file, line, dir, kind, date, author, issue_refs, text)
            VALUES ($f, $l, $d, $k, $date, $a, $refs, $t);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$f", marker.File);
        command.Parameters.AddWithValue("$l", marker.Line);
        command.Parameters.AddWithValue("$d", marker.Dir);
        command.Parameters.AddWithValue("$k", marker.Kind);
        command.Parameters.AddWithValue("$date", Nullable(marker.Date));
        command.Parameters.AddWithValue("$a", Nullable(marker.Author));
        command.Parameters.AddWithValue("$refs", string.Join(",", marker.IssueRefs));
        command.Parameters.AddWithValue("$t", marker.Text);
        ExecuteIdempotent(command, "markers", marker.File + ":" + marker.Line, json,
            "SELECT text FROM markers WHERE file = $key AND line = $line;",
            marker.File, marker.Text, ("$line", marker.Line));
    }

    private void InsertEvidence(SqliteTransaction transaction, EvidenceRow evidence)
    {
        // The hash is computed here, at flush time, so the snippet hasher can resolve the file's
        // recorded encoding from the files table inserted earlier in the same transaction.
        var hash = _snippetHasher?.Invoke(evidence.File, evidence.Span);

        using var command = _db.Command("""
            INSERT INTO evidence (id, file, sl, sc, el, ec, snippet_hash)
            VALUES ($id, $f, $sl, $sc, $el, $ec, $hash);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", evidence.Id);
        command.Parameters.AddWithValue("$f", evidence.File);
        command.Parameters.AddWithValue("$sl", evidence.Span.StartLine);
        command.Parameters.AddWithValue("$sc", evidence.Span.StartColumn);
        command.Parameters.AddWithValue("$el", evidence.Span.EndLine);
        command.Parameters.AddWithValue("$ec", evidence.Span.EndColumn);
        command.Parameters.AddWithValue("$hash", Nullable(hash));

        // Evidence ids are derived from (file, span), so a repeat is always the same row.
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            // Already recorded; identical by construction.
        }
    }

    private void InsertDiagnostic(SqliteTransaction transaction, DiagRec diagnostic)
    {
        using var command = _db.Command("""
            INSERT INTO diagnostics (phase, severity, code, message, file, line)
            VALUES ($p, $s, $c, $m, $f, $l);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$p", Phase);
        command.Parameters.AddWithValue("$s", diagnostic.Severity);
        command.Parameters.AddWithValue("$c", diagnostic.Code);
        command.Parameters.AddWithValue("$m", diagnostic.Message);
        command.Parameters.AddWithValue("$f", Nullable(diagnostic.File));
        command.Parameters.AddWithValue("$l", Nullable(diagnostic.Line));
        command.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- collision handling

    /// <summary>
    /// Inserts, treating a repeat of the same content as a no-op and a repeat with different
    /// content as fatal. The comparison is on the stored <c>json</c> column, which is the whole
    /// record, so "same id, same facts" is decided on the facts and not on a subset of columns.
    /// </summary>
    private void ExecuteIdempotent(
        SqliteCommand command, string table, string id, string json,
        string? existingQuery = null, string? key = null, string? expected = null,
        (string Name, object Value)? extraParameter = null)
    {
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsUniqueViolation(ex))
        {
            var existing = existingQuery is null
                ? ReadJson(table, id, command.Transaction)
                : ReadScalar(existingQuery, key!, command.Transaction, extraParameter);

            var incoming = existingQuery is null ? json : expected;

            if (string.Equals(existing, incoming, StringComparison.Ordinal)) return;
            throw new IdCollisionException(table, id, existing ?? "(absent)", incoming ?? "(absent)");
        }
    }

    private string? ReadJson(string table, string id, SqliteTransaction? transaction)
    {
        using var command = _db.Command($"SELECT json FROM {table} WHERE id = $id;");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private string? ReadScalar(
        string query, string key, SqliteTransaction? transaction, (string Name, object Value)? extraParameter)
    {
        using var command = _db.Command(query);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$key", key);
        if (extraParameter is { } extra) command.Parameters.AddWithValue(extra.Name, extra.Value);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private static bool IsUniqueViolation(SqliteException ex)
        => ex.SqliteErrorCode == 19; // SQLITE_CONSTRAINT

    private static object Nullable(object? value) => value ?? DBNull.Value;

    /// <summary>Flushes what is left, unless a previous flush already failed.</summary>
    public void Dispose()
    {
        if (_faulted) return;
        Flush();
    }
}
