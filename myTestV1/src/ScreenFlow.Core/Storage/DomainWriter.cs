using Microsoft.Data.Sqlite;

namespace ScreenFlow.Core.Storage;

/// <summary>
/// Writes the domain and result tables (02 §7.3). Used by the adapters in
/// <c>ScreenFlow.UnityPos</c> and by the graph and break layers in the core.
/// <para>
/// Every method buffers and sorts by primary key before writing, for the same reason
/// <see cref="IrWriter"/> does: the database must not depend on the order an adapter happened to
/// discover things in.
/// </para>
/// </summary>
public sealed class DomainWriter : IDisposable
{
    private readonly IndexDb _db;
    private readonly int _batchSize;

    private readonly List<ScreenRow> _screens = new();
    private readonly List<EnumMemberRow> _enumMembers = new();
    private readonly List<ControlRow> _controls = new();
    private readonly List<ControlHandlerRow> _controlHandlers = new();
    private readonly List<TextRow> _texts = new();
    private readonly List<MessageRow> _messages = new();
    private readonly List<InputConstraintRow> _inputConstraints = new();
    private readonly List<KeySeqTransitionRow> _keySeqTransitions = new();
    private readonly List<DiRegistrationRow> _diRegistrations = new();
    private readonly List<DomainEdgeRow> _domainEdges = new();
    private readonly List<MessageHandlerRow> _messageHandlers = new();
    private readonly List<EntryRow> _entries = new();
    private readonly List<BoundaryRow> _boundaries = new();
    private readonly List<BreakRow> _breaks = new();
    private readonly List<ScreenEdgeRow> _screenEdges = new();

    private int _buffered;
    private bool _faulted;

    public DomainWriter(IndexDb db, int batchSize = IrWriter.DefaultBatchSize)
    {
        _db = db;
        _batchSize = batchSize <= 0 ? IrWriter.DefaultBatchSize : batchSize;
    }

    public void Add(ScreenRow row) { _screens.Add(row); Advance(); }
    public void Add(EnumMemberRow row) { _enumMembers.Add(row); Advance(); }
    public void Add(ControlRow row) { _controls.Add(row); Advance(); }
    public void Add(ControlHandlerRow row) { _controlHandlers.Add(row); Advance(); }
    public void Add(TextRow row) { _texts.Add(row); Advance(); }
    public void Add(MessageRow row) { _messages.Add(row); Advance(); }
    public void Add(InputConstraintRow row) { _inputConstraints.Add(row); Advance(); }
    public void Add(KeySeqTransitionRow row) { _keySeqTransitions.Add(row); Advance(); }
    public void Add(DiRegistrationRow row) { _diRegistrations.Add(row); Advance(); }
    public void Add(DomainEdgeRow row) { _domainEdges.Add(row); Advance(); }
    public void Add(MessageHandlerRow row) { _messageHandlers.Add(row); Advance(); }
    public void Add(EntryRow row) { _entries.Add(row); Advance(); }
    public void Add(BoundaryRow row) { _boundaries.Add(row); Advance(); }
    public void Add(BreakRow row) { _breaks.Add(row); Advance(); }
    public void Add(ScreenEdgeRow row) { _screenEdges.Add(row); Advance(); }

    private void Advance()
    {
        if (++_buffered >= _batchSize) Flush();
    }

    /// <summary>
    /// Writes everything buffered so far. As in <see cref="IrWriter.Flush"/>, a failure clears the
    /// buffers and marks the writer faulted so <see cref="Dispose"/> cannot mask the real error.
    /// </summary>
    public void Flush()
    {
        if (_buffered == 0) return;

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

        foreach (var row in _screens
                     .OrderBy(r => r.Terminal, StringComparer.Ordinal)
                     .ThenBy(r => r.ScreenId, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO screens (terminal, screen_id, name, is_popup, view_type_id,
                                                presenter_type_id, registered, screen_field_id, evidence_id)
                VALUES ($t, $s, $n, $p, $v, $pr, $r, $f, $e);
                """,
                ("$t", row.Terminal), ("$s", row.ScreenId), ("$n", N(row.Name)),
                ("$p", N(row.IsPopup is null ? null : row.IsPopup.Value ? 1 : 0)),
                ("$v", N(row.ViewTypeId)), ("$pr", N(row.PresenterTypeId)),
                ("$r", row.Registered ? 1 : 0), ("$f", N(row.ScreenFieldId)), ("$e", N(row.EvidenceId)));
        }

        foreach (var row in _enumMembers.OrderBy(r => r.MemberId, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO enum_members (type_id, member_id, name, value_id, value_name)
                VALUES ($t, $m, $n, $vi, $vn);
                """,
                ("$t", row.TypeId), ("$m", row.MemberId), ("$n", row.Name),
                ("$vi", N(row.ValueId)), ("$vn", N(row.ValueName)));
        }

        foreach (var row in _controls
                     .OrderBy(r => r.Terminal, StringComparer.Ordinal)
                     .ThenBy(r => r.ScreenId, StringComparer.Ordinal)
                     .ThenBy(r => r.ControlId, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO controls (terminal, screen_id, control_id, field_id, control_type,
                                                 uxml_path, hierarchy_path, text_key, text_key_source,
                                                 text_key_confidence, evidence_id)
                VALUES ($t, $s, $c, $f, $ct, $u, $h, $k, $ks, $kc, $e);
                """,
                ("$t", row.Terminal), ("$s", row.ScreenId), ("$c", row.ControlId), ("$f", N(row.FieldId)),
                ("$ct", N(row.ControlType)), ("$u", N(row.UxmlPath)), ("$h", N(row.HierarchyPath)),
                ("$k", N(row.TextKey)), ("$ks", N(row.TextKeySource)), ("$kc", N(row.TextKeyConfidence)),
                ("$e", N(row.EvidenceId)));
        }

        foreach (var row in _controlHandlers
                     .OrderBy(r => r.Terminal, StringComparer.Ordinal)
                     .ThenBy(r => r.ScreenId, StringComparer.Ordinal)
                     .ThenBy(r => r.ControlId, StringComparer.Ordinal)
                     .ThenBy(r => r.EventName, StringComparer.Ordinal)
                     .ThenBy(r => r.HandlerMethodId, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO control_handlers (terminal, screen_id, control_id, event_name,
                                                         handler_method_id, confidence, evidence_id)
                VALUES ($t, $s, $c, $ev, $h, $cf, $e);
                """,
                ("$t", row.Terminal), ("$s", row.ScreenId), ("$c", row.ControlId), ("$ev", row.EventName),
                ("$h", row.HandlerMethodId), ("$cf", N(row.Confidence)), ("$e", N(row.EvidenceId)));
        }

        foreach (var row in _texts
                     .OrderBy(r => r.Collection, StringComparer.Ordinal)
                     .ThenBy(r => r.Key, StringComparer.Ordinal)
                     .ThenBy(r => r.Locale, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO texts (collection, key, locale, text_raw, text_plain, file)
                VALUES ($c, $k, $l, $tr, $tp, $f);
                """,
                ("$c", row.Collection), ("$k", row.Key), ("$l", row.Locale),
                ("$tr", N(row.TextRaw)), ("$tp", N(row.TextPlain)), ("$f", N(row.File)));
        }

        foreach (var row in _messages.OrderBy(r => r.Code).ThenBy(r => r.Locale, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO messages (code, locale, text_raw, text_plain)
                VALUES ($c, $l, $tr, $tp);
                """,
                ("$c", row.Code), ("$l", row.Locale), ("$tr", N(row.TextRaw)), ("$tp", N(row.TextPlain)));
        }

        foreach (var row in _inputConstraints.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO input_constraints (id, terminal, screen_id, method_id, control_field_id,
                                                          kind, value, error_code_name, error_code_value,
                                                          error_confidence, conditions_json, evidence_id)
                VALUES ($id, $t, $s, $m, $cf, $k, $v, $en, $ev, $ec, $cj, $e);
                """,
                ("$id", row.Id), ("$t", row.Terminal), ("$s", row.ScreenId), ("$m", N(row.MethodId)),
                ("$cf", N(row.ControlFieldId)), ("$k", row.Kind), ("$v", N(row.Value)),
                ("$en", N(row.ErrorCodeName)), ("$ev", N(row.ErrorCodeValue)),
                ("$ec", N(row.ErrorConfidence)), ("$cj", N(row.ConditionsJson)), ("$e", N(row.EvidenceId)));
        }

        foreach (var row in _keySeqTransitions.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO keyseq_transitions (id, stage, ps_code, handler_method_id, next_stage,
                                                           sub_table, evidence_id)
                VALUES ($id, $st, $ps, $h, $ns, $sub, $e);
                """,
                ("$id", row.Id), ("$st", row.Stage), ("$ps", row.PsCode), ("$h", N(row.HandlerMethodId)),
                ("$ns", N(row.NextStage)), ("$sub", N(row.SubTable)), ("$e", N(row.EvidenceId)));
        }

        foreach (var row in _diRegistrations.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO di_registrations (id, service_type_id, impl_type_id, lifetime, via, evidence_id)
                VALUES ($id, $s, $i, $l, $v, $e);
                """,
                ("$id", row.Id), ("$s", row.ServiceTypeId), ("$i", N(row.ImplTypeId)),
                ("$l", N(row.Lifetime)), ("$v", N(row.Via)), ("$e", N(row.EvidenceId)));
        }

        foreach (var row in _domainEdges.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO domain_edges (id, kind, callsite_id, from_method_id, json)
                VALUES ($id, $k, $c, $f, $j);
                """,
                ("$id", row.Id), ("$k", row.Kind), ("$c", N(row.CallsiteId)),
                ("$f", N(row.FromMethodId)), ("$j", row.Json));
        }

        foreach (var row in _messageHandlers.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO message_handlers (id, channel, terminal, screen_id, handler_method_id,
                                                         key_literals_json, evidence_id)
                VALUES ($id, $c, $t, $s, $h, $k, $e);
                """,
                ("$id", row.Id), ("$c", row.Channel), ("$t", N(row.Terminal)), ("$s", N(row.ScreenId)),
                ("$h", row.HandlerMethodId), ("$k", N(row.KeyLiteralsJson)), ("$e", N(row.EvidenceId)));
        }

        foreach (var row in _entries.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO entries (id, terminal, screen_id, trigger, control_id, method_id, evidence_id)
                VALUES ($id, $t, $s, $tr, $c, $m, $e);
                """,
                ("$id", row.Id), ("$t", row.Terminal), ("$s", row.ScreenId), ("$tr", row.Trigger),
                ("$c", N(row.ControlId)), ("$m", row.MethodId), ("$e", N(row.EvidenceId)));
        }

        foreach (var row in _boundaries.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO boundaries (id, category, contract_kind, contract_key, json)
                VALUES ($id, $c, $ck, $k, $j);
                """,
                ("$id", row.Id), ("$c", row.Category), ("$ck", N(row.ContractKind)),
                ("$k", N(row.ContractKey)), ("$j", row.Json));
        }

        foreach (var row in _breaks.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO breaks (id, category, reason, at_callsite_id, at_method_id,
                                               contract_key, resume_confidence, json)
                VALUES ($id, $c, $r, $ac, $am, $k, $rc, $j);
                """,
                ("$id", row.Id), ("$c", row.Category), ("$r", row.Reason), ("$ac", N(row.AtCallsiteId)),
                ("$am", N(row.AtMethodId)), ("$k", N(row.ContractKey)),
                ("$rc", N(row.ResumeConfidence)), ("$j", row.Json));
        }

        foreach (var row in _screenEdges.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            Insert(transaction, """
                INSERT OR REPLACE INTO screen_edges (id, from_terminal, from_screen, to_terminal, to_screen,
                                                     trigger, control_id, confidence, cost, ends_at_break, json)
                VALUES ($id, $ft, $fs, $tt, $ts, $tr, $c, $cf, $cost, $b, $j);
                """,
                ("$id", row.Id), ("$ft", row.FromTerminal), ("$fs", row.FromScreen),
                ("$tt", N(row.ToTerminal)), ("$ts", N(row.ToScreen)), ("$tr", row.Trigger),
                ("$c", N(row.ControlId)), ("$cf", row.Confidence), ("$cost", row.Cost),
                ("$b", N(row.EndsAtBreak)), ("$j", row.Json));
        }

        transaction.Commit();
        ClearBuffers();
    }

    private void ClearBuffers()
    {
        _screens.Clear(); _enumMembers.Clear(); _controls.Clear(); _controlHandlers.Clear();
        _texts.Clear(); _messages.Clear(); _inputConstraints.Clear(); _keySeqTransitions.Clear();
        _diRegistrations.Clear(); _domainEdges.Clear(); _messageHandlers.Clear(); _entries.Clear();
        _boundaries.Clear(); _breaks.Clear(); _screenEdges.Clear();
        _buffered = 0;
    }

    private void Insert(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _db.Command(sql);
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static object N(object? value) => value ?? DBNull.Value;

    /// <summary>Flushes what is left, unless a previous flush already failed.</summary>
    public void Dispose()
    {
        if (_faulted) return;
        Flush();
    }
}
