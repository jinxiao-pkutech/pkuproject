-- ScreenFlow index schema (02 sections 7.2 and 7.3).
-- Embedded as a resource so the shipped DDL and the design document can be diffed directly.

-- ---------------------------------------------------------------- base layer (02 section 7.2)

CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE files (path TEXT PRIMARY KEY, assembly TEXT, encoding TEXT, line_count INTEGER, content_hash TEXT);
CREATE TABLE symbols (id TEXT PRIMARY KEY, kind TEXT, name TEXT, fq_name TEXT, container_id TEXT, namespace TEXT,
  assembly TEXT, is_source INTEGER, file TEXT, sl INTEGER, sc INTEGER, el INTEGER, ec INTEGER,
  type_kind TEXT, base_type_id TEXT, json TEXT NOT NULL);
CREATE INDEX ix_symbols_name ON symbols(name);
CREATE INDEX ix_symbols_container ON symbols(container_id);
CREATE TABLE impls (type_id TEXT, interface_member_id TEXT, impl_member_id TEXT,
  PRIMARY KEY (type_id, interface_member_id));
CREATE INDEX ix_impls_iface ON impls(interface_member_id);
CREATE TABLE callsites (id TEXT PRIMARY KEY, caller_id TEXT, target_id TEXT, target_is_virtual INTEGER,
  reachable INTEGER, ord INTEGER, file TEXT, sl INTEGER, json TEXT NOT NULL);
CREATE INDEX ix_callsites_caller ON callsites(caller_id, ord);
CREATE INDEX ix_callsites_target ON callsites(target_id);
CREATE TABLE assigns (id TEXT PRIMARY KEY, method_id TEXT, target_symbol_id TEXT, operator TEXT,
  reachable INTEGER, ord INTEGER, file TEXT, sl INTEGER, json TEXT NOT NULL);
CREATE INDEX ix_assigns_target ON assigns(target_symbol_id);
CREATE INDEX ix_assigns_method ON assigns(method_id, ord);
CREATE TABLE creations (id TEXT PRIMARY KEY, method_id TEXT, type_id TEXT, json TEXT NOT NULL);
CREATE INDEX ix_creations_type ON creations(type_id);
CREATE TABLE cfgs (method_id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE markers (file TEXT, line INTEGER, dir TEXT, kind TEXT, date TEXT, author TEXT, issue_refs TEXT, text TEXT,
  PRIMARY KEY (file, line));
CREATE TABLE evidence (id TEXT PRIMARY KEY, file TEXT, sl INTEGER, sc INTEGER, el INTEGER, ec INTEGER, snippet_hash TEXT);
CREATE TABLE diagnostics (seq INTEGER PRIMARY KEY, phase TEXT, severity TEXT, code TEXT, message TEXT, file TEXT, line INTEGER);

-- ---------------------------------------------------------------- domain and result layer (02 section 7.3)

CREATE TABLE screens (terminal TEXT, screen_id TEXT, name TEXT, is_popup INTEGER, view_type_id TEXT, presenter_type_id TEXT,
  registered INTEGER NOT NULL, screen_field_id TEXT, evidence_id TEXT, PRIMARY KEY (terminal, screen_id));
CREATE TABLE enum_members (type_id TEXT, member_id TEXT PRIMARY KEY, name TEXT, value_id INTEGER, value_name TEXT);
CREATE TABLE controls (terminal TEXT, screen_id TEXT, control_id TEXT, field_id TEXT, control_type TEXT, uxml_path TEXT,
  hierarchy_path TEXT, text_key TEXT, text_key_source TEXT, text_key_confidence TEXT, evidence_id TEXT,
  PRIMARY KEY (terminal, screen_id, control_id));
CREATE TABLE control_handlers (terminal TEXT, screen_id TEXT, control_id TEXT, event_name TEXT, handler_method_id TEXT,
  confidence TEXT, evidence_id TEXT, PRIMARY KEY (terminal, screen_id, control_id, event_name, handler_method_id));
CREATE TABLE texts (collection TEXT, key TEXT, locale TEXT, text_raw TEXT, text_plain TEXT, file TEXT, PRIMARY KEY (collection, key, locale));
CREATE TABLE messages (code INTEGER, locale TEXT, text_raw TEXT, text_plain TEXT, PRIMARY KEY (code, locale));
CREATE TABLE input_constraints (id TEXT PRIMARY KEY, terminal TEXT, screen_id TEXT, method_id TEXT, control_field_id TEXT,
  kind TEXT, value TEXT, error_code_name TEXT, error_code_value INTEGER, error_confidence TEXT, conditions_json TEXT, evidence_id TEXT);
CREATE TABLE keyseq_transitions (id TEXT PRIMARY KEY, stage TEXT, ps_code TEXT, handler_method_id TEXT, next_stage TEXT,
  sub_table TEXT, evidence_id TEXT);
CREATE INDEX ix_keyseq_ps ON keyseq_transitions(ps_code);
CREATE TABLE di_registrations (id TEXT PRIMARY KEY, service_type_id TEXT, impl_type_id TEXT, lifetime TEXT, via TEXT, evidence_id TEXT);
CREATE INDEX ix_di_service ON di_registrations(service_type_id);
CREATE TABLE domain_edges (id TEXT PRIMARY KEY, kind TEXT, callsite_id TEXT, from_method_id TEXT, json TEXT NOT NULL);
CREATE INDEX ix_domain_edges_callsite ON domain_edges(callsite_id);
CREATE INDEX ix_domain_edges_from ON domain_edges(from_method_id);
CREATE TABLE message_handlers (id TEXT PRIMARY KEY, channel TEXT, terminal TEXT, screen_id TEXT, handler_method_id TEXT,
  key_literals_json TEXT, evidence_id TEXT);
CREATE TABLE entries (id TEXT PRIMARY KEY, terminal TEXT, screen_id TEXT, trigger TEXT, control_id TEXT, method_id TEXT, evidence_id TEXT);
CREATE TABLE boundaries (id TEXT PRIMARY KEY, category TEXT, contract_kind TEXT, contract_key TEXT, json TEXT NOT NULL);
CREATE TABLE breaks (id TEXT PRIMARY KEY, category TEXT, reason TEXT, at_callsite_id TEXT, at_method_id TEXT,
  contract_key TEXT, resume_confidence TEXT, json TEXT NOT NULL);
CREATE TABLE screen_edges (id TEXT PRIMARY KEY, from_terminal TEXT, from_screen TEXT, to_terminal TEXT, to_screen TEXT,
  trigger TEXT, control_id TEXT, confidence TEXT, cost REAL, ends_at_break TEXT, json TEXT NOT NULL);
CREATE INDEX ix_se_from ON screen_edges(from_terminal, from_screen);
CREATE INDEX ix_se_to ON screen_edges(to_terminal, to_screen);
