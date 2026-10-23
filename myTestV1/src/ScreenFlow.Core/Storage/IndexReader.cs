using Microsoft.Data.Sqlite;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Storage;

/// <summary>
/// Read access to the index (02 §7). Records come back out of the <c>json</c> column, so a reader
/// always sees the whole record rather than the subset the indexed columns happen to hold.
/// <para>
/// There is deliberately no "find one symbol by simple name" method. The target repository has many
/// groups of duplicate simple type names, so a name lookup returns every candidate and the caller
/// chooses by id (CLAUDE.md §4).
/// </para>
/// </summary>
public sealed class IndexReader
{
    private readonly IndexDb _db;

    public IndexReader(IndexDb db) => _db = db;

    public IndexDb Db => _db;

    // ---------------------------------------------------------------- meta

    public IReadOnlyDictionary<string, string> Meta()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = _db.Command("SELECT key, value FROM meta ORDER BY key;");
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    // ---------------------------------------------------------------- symbols

    public SymbolRec? Symbol(string id) => ReadOne<SymbolRec>("SELECT json FROM symbols WHERE id = $id;", ("$id", id));

    public IReadOnlyList<SymbolRec> SymbolsByName(string name)
        => ReadMany<SymbolRec>("SELECT json FROM symbols WHERE name = $n ORDER BY id;", ("$n", name));

    public IReadOnlyList<SymbolRec> SymbolsInContainer(string containerId)
        => ReadMany<SymbolRec>("SELECT json FROM symbols WHERE container_id = $c ORDER BY id;", ("$c", containerId));

    public IReadOnlyList<SymbolRec> SymbolsByKind(string kind)
        => ReadMany<SymbolRec>("SELECT json FROM symbols WHERE kind = $k ORDER BY id;", ("$k", kind));

    /// <summary>Every symbol declared in one file, in source order.</summary>
    public IReadOnlyList<SymbolRec> SymbolsInFile(string file)
        => ReadMany<SymbolRec>("SELECT json FROM symbols WHERE file = $f ORDER BY sl, sc, id;", ("$f", file));

    public IReadOnlyList<SymbolRec> AllSymbols()
        => ReadMany<SymbolRec>("SELECT json FROM symbols ORDER BY id;");

    // ---------------------------------------------------------------- relations

    public IReadOnlyList<ImplRec> ImplementationsOf(string interfaceMemberId)
        => ReadRows("SELECT type_id, interface_member_id, impl_member_id FROM impls WHERE interface_member_id = $i ORDER BY type_id;",
            r => new ImplRec
            {
                TypeId = r.GetString(0),
                InterfaceMemberId = r.GetString(1),
                ImplMemberId = r.GetString(2)
            },
            ("$i", interfaceMemberId));

    public IReadOnlyList<ImplRec> ImplementationsForType(string typeId)
        => ReadRows("SELECT type_id, interface_member_id, impl_member_id FROM impls WHERE type_id = $t ORDER BY interface_member_id;",
            r => new ImplRec
            {
                TypeId = r.GetString(0),
                InterfaceMemberId = r.GetString(1),
                ImplMemberId = r.GetString(2)
            },
            ("$t", typeId));

    // ---------------------------------------------------------------- sites

    public CallsiteRec? Callsite(string id)
        => ReadOne<CallsiteRec>("SELECT json FROM callsites WHERE id = $id;", ("$id", id));

    public IReadOnlyList<CallsiteRec> CallsitesIn(string callerId)
        => ReadMany<CallsiteRec>("SELECT json FROM callsites WHERE caller_id = $c ORDER BY ord, id;", ("$c", callerId));

    public IReadOnlyList<CallsiteRec> CallsitesTargeting(string targetId)
        => ReadMany<CallsiteRec>("SELECT json FROM callsites WHERE target_id = $t ORDER BY id;", ("$t", targetId));

    /// <summary>Callsites within a file and line range, for <c>debug callsites</c> (02 §11.2).</summary>
    public IReadOnlyList<CallsiteRec> CallsitesInRange(string file, int fromLine, int toLine)
        => ReadMany<CallsiteRec>(
            "SELECT json FROM callsites WHERE file = $f AND sl >= $from AND sl <= $to ORDER BY sl, id;",
            ("$f", file), ("$from", fromLine), ("$to", toLine));

    public AssignRec? Assign(string id)
        => ReadOne<AssignRec>("SELECT json FROM assigns WHERE id = $id;", ("$id", id));

    public IReadOnlyList<AssignRec> AssignsIn(string methodId)
        => ReadMany<AssignRec>("SELECT json FROM assigns WHERE method_id = $m ORDER BY ord, id;", ("$m", methodId));

    public IReadOnlyList<AssignRec> AssignsTargeting(string symbolId)
        => ReadMany<AssignRec>("SELECT json FROM assigns WHERE target_symbol_id = $s ORDER BY id;", ("$s", symbolId));

    public IReadOnlyList<AssignRec> AssignsInRange(string file, int fromLine, int toLine)
        => ReadMany<AssignRec>(
            "SELECT json FROM assigns WHERE file = $f AND sl >= $from AND sl <= $to ORDER BY sl, id;",
            ("$f", file), ("$from", fromLine), ("$to", toLine));

    public IReadOnlyList<CreationRec> CreationsIn(string methodId)
        => ReadMany<CreationRec>("SELECT json FROM creations WHERE method_id = $m ORDER BY id;", ("$m", methodId));

    public IReadOnlyList<CreationRec> CreationsOfType(string typeId)
        => ReadMany<CreationRec>("SELECT json FROM creations WHERE type_id = $t ORDER BY id;", ("$t", typeId));

    public CfgRec? Cfg(string methodId)
        => ReadOne<CfgRec>("SELECT json FROM cfgs WHERE method_id = $m;", ("$m", methodId));

    // ---------------------------------------------------------------- files, evidence, markers

    public FileRec? File(string path)
        => ReadRows("SELECT path, assembly, encoding, line_count, content_hash FROM files WHERE path = $p;",
            r => new FileRec
            {
                Path = r.GetString(0),
                Assembly = r.GetString(1),
                Encoding = r.GetString(2),
                LineCount = r.GetInt32(3),
                ContentHash = r.GetString(4)
            },
            ("$p", path)).FirstOrDefault();

    public IReadOnlyList<FileRec> AllFiles()
        => ReadRows("SELECT path, assembly, encoding, line_count, content_hash FROM files ORDER BY path;",
            r => new FileRec
            {
                Path = r.GetString(0),
                Assembly = r.GetString(1),
                Encoding = r.GetString(2),
                LineCount = r.GetInt32(3),
                ContentHash = r.GetString(4)
            });

    public sealed record EvidenceRecord(string Id, string File, Span Span, string? SnippetHash);

    public EvidenceRecord? Evidence(string id)
        => ReadRows("SELECT id, file, sl, sc, el, ec, snippet_hash FROM evidence WHERE id = $id;",
            r => new EvidenceRecord(
                r.GetString(0), r.GetString(1),
                Span.Of(r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)),
                r.IsDBNull(6) ? null : r.GetString(6)),
            ("$id", id)).FirstOrDefault();

    public IReadOnlyList<MarkerRec> MarkersIn(string file)
        => ReadRows("SELECT file, line, dir, kind, date, author, issue_refs, text FROM markers WHERE file = $f ORDER BY line;",
            MapMarker, ("$f", file));

    public IReadOnlyList<MarkerRec> AllMarkers()
        => ReadRows("SELECT file, line, dir, kind, date, author, issue_refs, text FROM markers ORDER BY file, line;",
            MapMarker);

    private static MarkerRec MapMarker(SqliteDataReader r) => new()
    {
        File = r.GetString(0),
        Line = r.GetInt32(1),
        Dir = r.GetString(2),
        Kind = r.GetString(3),
        Date = r.IsDBNull(4) ? null : r.GetString(4),
        Author = r.IsDBNull(5) ? null : r.GetString(5),
        IssueRefs = r.IsDBNull(6) || r.GetString(6).Length == 0
            ? Array.Empty<string>()
            : r.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries),
        Text = r.GetString(7)
    };

    public sealed record DiagnosticRecord(long Seq, string Phase, string Severity, string Code, string Message, string? File, int? Line);

    public IReadOnlyList<DiagnosticRecord> Diagnostics(string? severity = null)
    {
        var sql = severity is null
            ? "SELECT seq, phase, severity, code, message, file, line FROM diagnostics ORDER BY seq;"
            : "SELECT seq, phase, severity, code, message, file, line FROM diagnostics WHERE severity = $s ORDER BY seq;";
        return severity is null
            ? ReadRows(sql, MapDiagnostic)
            : ReadRows(sql, MapDiagnostic, ("$s", severity));
    }

    private static DiagnosticRecord MapDiagnostic(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetInt32(6));

    // ---------------------------------------------------------------- domain tables

    public IReadOnlyList<ScreenRow> Screens(string? screenId = null, string? terminal = null)
    {
        var where = new List<string>();
        var parameters = new List<(string, object)>();
        if (screenId is not null) { where.Add("screen_id = $s"); parameters.Add(("$s", screenId)); }
        if (terminal is not null) { where.Add("terminal = $t"); parameters.Add(("$t", terminal)); }

        var sql = "SELECT terminal, screen_id, name, is_popup, view_type_id, presenter_type_id, registered, " +
                  "screen_field_id, evidence_id FROM screens" +
                  (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) +
                  " ORDER BY terminal, screen_id;";

        return ReadRows(sql, r => new ScreenRow
        {
            Terminal = r.GetString(0),
            ScreenId = r.GetString(1),
            Name = r.IsDBNull(2) ? null : r.GetString(2),
            IsPopup = r.IsDBNull(3) ? null : r.GetInt32(3) != 0,
            ViewTypeId = r.IsDBNull(4) ? null : r.GetString(4),
            PresenterTypeId = r.IsDBNull(5) ? null : r.GetString(5),
            Registered = r.GetInt32(6) != 0,
            ScreenFieldId = r.IsDBNull(7) ? null : r.GetString(7),
            EvidenceId = r.IsDBNull(8) ? null : r.GetString(8)
        }, parameters.ToArray());
    }

    public IReadOnlyList<InputConstraintRow> InputConstraints(string? screenId = null, string? terminal = null)
    {
        var where = new List<string>();
        var parameters = new List<(string, object)>();
        if (screenId is not null) { where.Add("screen_id = $s"); parameters.Add(("$s", screenId)); }
        if (terminal is not null) { where.Add("terminal = $t"); parameters.Add(("$t", terminal)); }

        var sql = "SELECT id, terminal, screen_id, method_id, control_field_id, kind, value, " +
                  "error_code_name, error_code_value, error_confidence, conditions_json, evidence_id " +
                  "FROM input_constraints" +
                  (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) +
                  " ORDER BY id;";

        return ReadRows(sql, r => new InputConstraintRow
        {
            Id = r.GetString(0),
            Terminal = r.GetString(1),
            ScreenId = r.GetString(2),
            MethodId = r.IsDBNull(3) ? null : r.GetString(3),
            ControlFieldId = r.IsDBNull(4) ? null : r.GetString(4),
            Kind = r.GetString(5),
            Value = r.IsDBNull(6) ? null : r.GetString(6),
            ErrorCodeName = r.IsDBNull(7) ? null : r.GetString(7),
            ErrorCodeValue = r.IsDBNull(8) ? null : r.GetInt32(8),
            ErrorConfidence = r.IsDBNull(9) ? null : r.GetString(9),
            ConditionsJson = r.IsDBNull(10) ? null : r.GetString(10),
            EvidenceId = r.IsDBNull(11) ? null : r.GetString(11)
        }, parameters.ToArray());
    }

    /// <summary>Enum members sharing one simple name, ordered by type then member id.</summary>
    public IReadOnlyList<EnumMemberRow> EnumMembersByName(string name)
        => ReadRows("""
            SELECT type_id, member_id, name, value_id, value_name FROM enum_members
            WHERE name = $n ORDER BY type_id, member_id;
            """,
            r => new EnumMemberRow
            {
                TypeId = r.GetString(0),
                MemberId = r.GetString(1),
                Name = r.GetString(2),
                ValueId = r.IsDBNull(3) ? null : r.GetInt32(3),
                ValueName = r.IsDBNull(4) ? null : r.GetString(4)
            },
            ("$n", name));

    /// <summary>Enum members carrying one numeric value, ordered by type then member id.</summary>
    public IReadOnlyList<EnumMemberRow> EnumMembersByValue(int value)
        => ReadRows("""
            SELECT type_id, member_id, name, value_id, value_name FROM enum_members
            WHERE value_id = $v ORDER BY type_id, member_id;
            """,
            r => new EnumMemberRow
            {
                TypeId = r.GetString(0),
                MemberId = r.GetString(1),
                Name = r.GetString(2),
                ValueId = r.IsDBNull(3) ? null : r.GetInt32(3),
                ValueName = r.IsDBNull(4) ? null : r.GetString(4)
            },
            ("$v", value));

    public IReadOnlyList<ControlRow> Controls(string terminal, string screenId)
        => ReadRows("""
            SELECT terminal, screen_id, control_id, field_id, control_type, uxml_path, hierarchy_path,
                   text_key, text_key_source, text_key_confidence, evidence_id
            FROM controls WHERE terminal = $t AND screen_id = $s ORDER BY control_id;
            """,
            r => new ControlRow
            {
                Terminal = r.GetString(0),
                ScreenId = r.GetString(1),
                ControlId = r.GetString(2),
                FieldId = r.IsDBNull(3) ? null : r.GetString(3),
                ControlType = r.IsDBNull(4) ? null : r.GetString(4),
                UxmlPath = r.IsDBNull(5) ? null : r.GetString(5),
                HierarchyPath = r.IsDBNull(6) ? null : r.GetString(6),
                TextKey = r.IsDBNull(7) ? null : r.GetString(7),
                TextKeySource = r.IsDBNull(8) ? null : r.GetString(8),
                TextKeyConfidence = r.IsDBNull(9) ? null : r.GetString(9),
                EvidenceId = r.IsDBNull(10) ? null : r.GetString(10)
            },
            ("$t", terminal), ("$s", screenId));

    public IReadOnlyList<ControlHandlerRow> ControlHandlers(string terminal, string screenId)
        => ReadRows("""
            SELECT terminal, screen_id, control_id, event_name, handler_method_id, confidence, evidence_id
            FROM control_handlers WHERE terminal = $t AND screen_id = $s
            ORDER BY control_id, event_name, handler_method_id;
            """,
            r => new ControlHandlerRow
            {
                Terminal = r.GetString(0),
                ScreenId = r.GetString(1),
                ControlId = r.GetString(2),
                EventName = r.GetString(3),
                HandlerMethodId = r.GetString(4),
                Confidence = r.IsDBNull(5) ? null : r.GetString(5),
                EvidenceId = r.IsDBNull(6) ? null : r.GetString(6)
            },
            ("$t", terminal), ("$s", screenId));

    public TextRow? Text(string collection, string key, string locale)
        => ReadRows("""
            SELECT collection, key, locale, text_raw, text_plain, file FROM texts
            WHERE collection = $c AND key = $k AND locale = $l;
            """,
            r => new TextRow
            {
                Collection = r.GetString(0),
                Key = r.GetString(1),
                Locale = r.GetString(2),
                TextRaw = r.IsDBNull(3) ? null : r.GetString(3),
                TextPlain = r.IsDBNull(4) ? null : r.GetString(4),
                File = r.IsDBNull(5) ? null : r.GetString(5)
            },
            ("$c", collection), ("$k", key), ("$l", locale)).FirstOrDefault();

    /// <summary>Every text matching a key, optionally narrowed by collection and locale.</summary>
    public IReadOnlyList<TextRow> Texts(string key, string? collection = null, string? locale = null)
    {
        var where = new List<string> { "key = $k" };
        var parameters = new List<(string, object)> { ("$k", key) };
        if (collection is not null) { where.Add("collection = $c"); parameters.Add(("$c", collection)); }
        if (locale is not null) { where.Add("locale = $l"); parameters.Add(("$l", locale)); }

        var sql = "SELECT collection, key, locale, text_raw, text_plain, file FROM texts WHERE "
                  + string.Join(" AND ", where) + " ORDER BY collection, key, locale;";

        return ReadRows(sql, r => new TextRow
        {
            Collection = r.GetString(0),
            Key = r.GetString(1),
            Locale = r.GetString(2),
            TextRaw = r.IsDBNull(3) ? null : r.GetString(3),
            TextPlain = r.IsDBNull(4) ? null : r.GetString(4),
            File = r.IsDBNull(5) ? null : r.GetString(5)
        }, parameters.ToArray());
    }

    public MessageRow? Message(int code, string locale)
        => ReadRows("SELECT code, locale, text_raw, text_plain FROM messages WHERE code = $c AND locale = $l;",
            r => new MessageRow
            {
                Code = r.GetInt32(0),
                Locale = r.GetString(1),
                TextRaw = r.IsDBNull(2) ? null : r.GetString(2),
                TextPlain = r.IsDBNull(3) ? null : r.GetString(3)
            },
            ("$c", code), ("$l", locale)).FirstOrDefault();

    public IReadOnlyList<DomainEdgeRow> DomainEdges(string? kind = null, string? fromMethodId = null)
    {
        var where = new List<string>();
        var parameters = new List<(string, object)>();
        if (kind is not null) { where.Add("kind = $k"); parameters.Add(("$k", kind)); }
        if (fromMethodId is not null) { where.Add("from_method_id = $f"); parameters.Add(("$f", fromMethodId)); }

        var sql = "SELECT id, kind, callsite_id, from_method_id, json FROM domain_edges" +
                  (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) + " ORDER BY id;";

        return ReadRows(sql, r => new DomainEdgeRow
        {
            Id = r.GetString(0),
            Kind = r.GetString(1),
            CallsiteId = r.IsDBNull(2) ? null : r.GetString(2),
            FromMethodId = r.IsDBNull(3) ? null : r.GetString(3),
            Json = r.GetString(4)
        }, parameters.ToArray());
    }

    public IReadOnlyList<ScreenEdgeRow> ScreenEdgesFrom(string terminal, string screenId)
        => ReadRows(ScreenEdgeSelect + " WHERE from_terminal = $t AND from_screen = $s ORDER BY id;",
            MapScreenEdge, ("$t", terminal), ("$s", screenId));

    public IReadOnlyList<ScreenEdgeRow> ScreenEdgesTo(string terminal, string screenId)
        => ReadRows(ScreenEdgeSelect + " WHERE to_terminal = $t AND to_screen = $s ORDER BY id;",
            MapScreenEdge, ("$t", terminal), ("$s", screenId));

    public IReadOnlyList<ScreenEdgeRow> AllScreenEdges()
        => ReadRows(ScreenEdgeSelect + " ORDER BY id;", MapScreenEdge);

    private const string ScreenEdgeSelect =
        "SELECT id, from_terminal, from_screen, to_terminal, to_screen, trigger, control_id, confidence, " +
        "cost, ends_at_break, json FROM screen_edges";

    private static ScreenEdgeRow MapScreenEdge(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        FromTerminal = r.GetString(1),
        FromScreen = r.GetString(2),
        ToTerminal = r.IsDBNull(3) ? null : r.GetString(3),
        ToScreen = r.IsDBNull(4) ? null : r.GetString(4),
        Trigger = r.GetString(5),
        ControlId = r.IsDBNull(6) ? null : r.GetString(6),
        Confidence = r.GetString(7),
        Cost = r.GetDouble(8),
        EndsAtBreak = r.IsDBNull(9) ? null : r.GetString(9),
        Json = r.GetString(10)
    };

    public IReadOnlyList<BreakRow> Breaks(string? category = null)
    {
        var sql = "SELECT id, category, reason, at_callsite_id, at_method_id, contract_key, resume_confidence, json " +
                  "FROM breaks" + (category is null ? "" : " WHERE category = $c") + " ORDER BY id;";
        var map = (SqliteDataReader r) => new BreakRow
        {
            Id = r.GetString(0),
            Category = r.GetString(1),
            Reason = r.GetString(2),
            AtCallsiteId = r.IsDBNull(3) ? null : r.GetString(3),
            AtMethodId = r.IsDBNull(4) ? null : r.GetString(4),
            ContractKey = r.IsDBNull(5) ? null : r.GetString(5),
            ResumeConfidence = r.IsDBNull(6) ? null : r.GetString(6),
            Json = r.GetString(7)
        };
        return category is null ? ReadRows(sql, map) : ReadRows(sql, map, ("$c", category));
    }

    // ---------------------------------------------------------------- plumbing

    private T? ReadOne<T>(string sql, params (string Name, object Value)[] parameters) where T : IrRecord
    {
        var json = ReadRows(sql, r => r.GetString(0), parameters).FirstOrDefault();
        return json is null ? null : (T)IrJson.ReadLine(json);
    }

    private IReadOnlyList<T> ReadMany<T>(string sql, params (string Name, object Value)[] parameters) where T : IrRecord
        => ReadRows(sql, r => r.GetString(0), parameters)
            .Select(json => (T)IrJson.ReadLine(json))
            .ToList();

    private IReadOnlyList<TRow> ReadRows<TRow>(
        string sql, Func<SqliteDataReader, TRow> map, params (string Name, object Value)[] parameters)
    {
        using var command = _db.Command(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();

        var rows = new List<TRow>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }
}
