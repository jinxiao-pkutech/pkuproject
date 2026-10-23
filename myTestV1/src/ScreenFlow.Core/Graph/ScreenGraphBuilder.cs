using System.Text.Json.Nodes;
using ScreenFlow.Core.Breaks;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Extraction;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Pipeline;
using ScreenFlow.Core.Resolution;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Graph;

/// <summary>
/// Builds the screen graph (pipeline phase P8, 02 §10).
/// <para>
/// This is the answer the whole tool exists for: from each entry point, follow what the code does
/// until it reaches another screen or stops being followable. Where it stops, a break says why and
/// where to resume. The core does this knowing only the shapes of the domain edges (02 §9) — never
/// a POS name — which is what keeps the traversal reusable for another project.
/// </para>
/// </summary>
public sealed class ScreenGraphBuilder : IDomainStage
{
    public const string GraphPhase = "P8";

    /// <summary>Raised when the result contradicts 02 §10.4; that is a defect, not a finding.</summary>
    public const string InvariantCode = "E_GRAPH_INVARIANT";

    public string Phase => GraphPhase;

    public string Name => "screen-graph";

    public void Run(DomainStageContext stage)
    {
        var run = new GraphRun(stage);
        run.Build();
    }

    /// <summary>One build of the graph. A class so the tables it reads are loaded once.</summary>
    private sealed class GraphRun
    {
        private readonly DomainStageContext _stage;
        private readonly IndexReader _reader;
        private readonly TraversalConfig _limits;
        private readonly BreakFactory _breaks = new();

        private readonly Dictionary<string, List<DomainEdgeRow>> _edgesByCallsite = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScreenRow> _screens = new(StringComparer.Ordinal);
        private readonly List<MessageHandler> _handlers = new();
        private readonly Dictionary<string, KeySeqRow> _transitions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BoundaryRow> _boundaries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SymbolRec?> _symbols = new(StringComparer.Ordinal);
        private readonly List<ScreenEdge> _edges = new();

        public GraphRun(DomainStageContext stage)
        {
            _stage = stage;
            _reader = stage.Reader;
            _limits = stage.Domain.Traversal;
        }

        public void Build()
        {
            Load();

            foreach (var entry in Entries())
            {
                _stage.Ct.ThrowIfCancellationRequested();
                _stage.Progress.Report(new ProgressEvent(GraphPhase, entry.ScreenId));

                var from = new ScreenRef { Terminal = entry.Terminal, ScreenId = entry.ScreenId };
                var trigger = new EdgeTrigger
                {
                    Kind = entry.Trigger,
                    ControlId = entry.ControlId,
                    EntryMethodId = entry.MethodId
                };

                var state = new TraversalState { MethodId = entry.MethodId, Terminal = entry.Terminal };
                Walk(state, from, trigger, new HashSet<string>(StringComparer.Ordinal));
            }

            // The invariant is checked before anything is written: a contradictory graph is a
            // defect, and writing it would put the contradiction in the index (T18 §9).
            CheckInvariants();

            using var writer = new DomainWriter(_stage.Db);
            _writer = writer;

            WriteEdges();
            _breaks.WriteAll(writer);
            writer.Flush();
        }

        private DomainWriter? _writer;

        private DomainWriter Writer
            => _writer ?? throw new InvalidOperationException("the graph writer is only open during Build.");

        // ------------------------------------------------------------------ traversal

        private void Walk(TraversalState state, ScreenRef from, EdgeTrigger trigger, HashSet<string> visited)
        {
            if (state.Depth > _limits.MaxChainDepth)
            {
                var truncated = _breaks.Create(
                    BreakCategories.Unknown,
                    BreakReasons.TruncatedByDepthLimit,
                    SiteOf(state.MethodId, null),
                    new BreakContract { Kind = "unknown" },
                    new WhyTemplates.Fields { Count = _limits.MaxChainDepth });

                Emit(state.WithBreak(truncated.Id), from, null, trigger, truncated.Id);
                return;
            }

            if (!visited.Add(state.VisitKey)) return;

            var walking = state;

            foreach (var call in _reader.CallsitesIn(walking.MethodId)
                         .Where(call => call.Reachable)
                         .OrderBy(call => call.Order))
            {
                var filtered = Filter(walking, call);
                if (filtered is null) continue;

                // The guards on the call are conditions of what that call leads to, so they are
                // added per callsite rather than carried on to the next one (02 §10.4).
                var current = filtered;
                foreach (var condition in call.Conditions)
                    current = current.WithCondition(condition.Text);

                var found = new List<string>();
                var edges = _edgesByCallsite.GetValueOrDefault(call.Id);

                if (edges is { Count: > 0 })
                {
                    foreach (var edge in edges.OrderBy(edge => edge.Kind, StringComparer.Ordinal))
                        Handle(edge, current, call, from, trigger, visited, found);
                }
                else
                {
                    Dispatch(current, call, from, trigger, visited, found);
                }

                // A break found here governs everything after it in this method too: the next
                // statement runs on a result the index could not determine (02 §10.4).
                foreach (var id in found) walking = walking.WithBreak(id);
            }
        }

        /// <summary>
        /// 02 §10.3: inside a receiver, only the branches the keys can reach are followed. A branch
        /// only some keys reach is followed as a candidate.
        /// </summary>
        private TraversalState? Filter(TraversalState state, CallsiteRec call)
        {
            if (state.BranchFilter is null) return state;

            var constraints = EventPathMatcher.ConstraintsOf(call.Conditions, EventPathKey);
            if (constraints.Count == 0) return state;

            var match = EventPathMatcher.Match(state.BranchFilter.Keys, constraints);
            if (!match.Matched) return null;

            return match.AllKeys ? state : state.AsCandidate();
        }

        private void Handle(
            DomainEdgeRow edge,
            TraversalState state,
            CallsiteRec call,
            ScreenRef from,
            EdgeTrigger trigger,
            HashSet<string> visited,
            List<string> found)
        {
            var json = JsonNode.Parse(edge.Json) as JsonObject ?? new JsonObject();

            switch (edge.Kind)
            {
                case DomainEdgeKinds.Navigate:
                    Navigate(state, call, from, trigger, json, found);
                    break;

                case DomainEdgeKinds.ClosePopup:
                    // Recorded on the chain; closing a popup is not a transition (02 §10.2).
                    break;

                case DomainEdgeKinds.MessageSend:
                    Send(state, call, from, trigger, json, visited, found);
                    break;

                case DomainEdgeKinds.KeySeqDispatch:
                    KeySeq(state, call, from, trigger, json, visited, found);
                    break;

                case DomainEdgeKinds.Enqueue:
                    Enqueue(state, call, from, trigger, json, visited, found);
                    break;

                case DomainEdgeKinds.Boundary:
                    Boundary(state, call, from, trigger, json, visited, found);
                    break;

                case DomainEdgeKinds.ServiceResolve:
                case DomainEdgeKinds.TableAccess:
                    // Neither changes where the traversal goes (02 §10.2).
                    break;
            }
        }

        /// <summary>02 §10.2: a local navigation is an edge to the named screen on this terminal.</summary>
        private void Navigate(
            TraversalState state,
            CallsiteRec call,
            ScreenRef from,
            EdgeTrigger trigger,
            JsonObject json,
            List<string> found)
        {
            var screenIds = Strings(json["screenIds"]);

            if (screenIds.Count == 0)
            {
                var unresolved = _breaks.Create(
                    BreakCategories.Unknown,
                    BreakReasons.UnresolvedConstantArgument,
                    SiteOf(state.MethodId, call),
                    new BreakContract { Kind = "unknown" },
                    new WhyTemplates.Fields { Text = json["unresolvedText"]?.GetValue<string>() ?? string.Empty });

                found.Add(unresolved.Id);
                Emit(state.WithBreak(unresolved.Id).With(Step(state, call, ChainStepKinds.Navigate, null)),
                    from, null, trigger, unresolved.Id);
                return;
            }

            foreach (var screenId in screenIds)
            {
                var step = Step(state, call, ChainStepKinds.Navigate, screenId);
                var to = new ScreenRef { Terminal = state.Terminal, ScreenId = screenId };

                if (!_screens.ContainsKey(Key(to)))
                {
                    // The code navigates to a screen no registry declares (E-09): a real edge that
                    // reaches nothing, which is reported rather than dropped.
                    var missing = _breaks.Create(
                        BreakCategories.Unknown,
                        BreakReasons.ScreenNotRegistered,
                        SiteOf(state.MethodId, call),
                        new BreakContract { Kind = "unknown", Key = screenId },
                        new WhyTemplates.Fields { Key = screenId });

                    found.Add(missing.Id);
                    Emit(state.WithBreak(missing.Id).With(step), from, null, trigger, missing.Id);
                    continue;
                }

                Emit(state.With(step), from, to, trigger, null);
            }
        }

        /// <summary>
        /// 02 §10.2: a TCP send drives the peer terminal. Where it names a screen it is an edge;
        /// where it does not, the receiver is whatever screen happens to be shown — a break with
        /// the candidates (03 §10.3).
        /// </summary>
        private void Send(
            TraversalState state,
            CallsiteRec call,
            ScreenRef from,
            EdgeTrigger trigger,
            JsonObject json,
            HashSet<string> visited,
            List<string> found)
        {
            var peer = _stage.Domain.PeerOf(state.Terminal) ?? state.Terminal;
            var keys = Strings(json["keys"]);
            var complete = json["keysComplete"]?.GetValue<bool>() ?? false;
            var targets = Strings(json["displayTargets"]);
            var sendStep = Step(state, call, ChainStepKinds.SocketSend, string.Join(", ", keys));
            var sent = state.With(sendStep);

            if (keys.Count == 0 || !complete)
            {
                var unresolved = _breaks.Create(
                    BreakCategories.Unknown,
                    BreakReasons.UnresolvedConstantArgument,
                    SiteOf(state.MethodId, call),
                    new BreakContract { Kind = "tcpEvent" },
                    new WhyTemplates.Fields { Text = json["unresolvedText"]?.GetValue<string>() ?? string.Empty });

                found.Add(unresolved.Id);
                Emit(sent.WithBreak(unresolved.Id), from, null, trigger, unresolved.Id);
                return;
            }

            // A display event names the screen it brings up on the peer (02 §9.7).
            foreach (var target in targets)
            {
                var to = new ScreenRef { Terminal = peer, ScreenId = target };
                var display = Step(sent, call, ChainStepKinds.RemoteDisplay, target);

                if (!_screens.ContainsKey(Key(to)))
                {
                    var missing = _breaks.Create(
                        BreakCategories.Unknown,
                        BreakReasons.ScreenNotRegistered,
                        SiteOf(state.MethodId, call),
                        new BreakContract { Kind = "tcpEvent", Key = target },
                        new WhyTemplates.Fields { Key = target });

                    found.Add(missing.Id);
                    Emit(sent.WithBreak(missing.Id).With(display), from, null, trigger, missing.Id);
                    continue;
                }

                Emit(sent.With(display), from, to, trigger, null);
            }

            var receivers = Receivers(peer, keys, targets);

            if (receivers.Count == 0)
            {
                var unmatched = _breaks.Create(
                    BreakCategories.TerminalTcpCurrentScreen,
                    BreakReasons.StringKeyedDispatchUnmatched,
                    SiteOf(state.MethodId, call),
                    new BreakContract { Kind = "tcpEvent", Key = string.Join(", ", keys) },
                    new WhyTemplates.Fields { Key = string.Join(", ", keys) });

                found.Add(unmatched.Id);

                // Nothing on the peer answers this event: the operation leads nowhere (E-29).
                if (targets.Count == 0) Emit(sent.WithBreak(unmatched.Id), from, null, trigger, unmatched.Id);
                return;
            }

            if (targets.Count == 0)
            {
                // Which screen receives depends on what is displayed at the time (03 §10.3).
                var runtime = _breaks.Create(
                    BreakCategories.TerminalTcpCurrentScreen,
                    BreakReasons.RuntimeStateDependentDispatch,
                    SiteOf(state.MethodId, call),
                    new BreakContract { Kind = "tcpEvent", Key = string.Join(", ", keys) },
                    new WhyTemplates.Fields { Key = string.Join(", ", keys), Count = receivers.Count },
                    receivers.Select(receiver => Resume(receiver.HandlerMethodId, ScreenCondition(peer, receiver)))
                        .ToList());

                found.Add(runtime.Id);
                sent = sent.WithBreak(runtime.Id);
            }

            foreach (var receiver in receivers)
            {
                var step = Step(sent, call, ChainStepKinds.SocketReceive, receiver.ScreenId);

                var next = sent
                    .WithCondition(ScreenCondition(peer, receiver))
                    .AsCandidate()
                    .Into(receiver.HandlerMethodId, step with { Terminal = peer, MethodId = receiver.HandlerMethodId })
                    with
                    {
                        Terminal = peer,
                        BranchFilter = new BranchFilter { Channel = "tcp", Keys = receiver.MatchedKeys }
                    };

                if (ReceiveDeferred) next = next.AsDeferred();

                Walk(next, from, trigger, visited);
            }
        }

        /// <summary>
        /// 02 §10.2: which key sequence handler runs depends on the stage at the time, so every
        /// handler bound to the key code is a candidate and the break says so (03 §11).
        /// </summary>
        private void KeySeq(
            TraversalState state,
            CallsiteRec call,
            ScreenRef from,
            EdgeTrigger trigger,
            JsonObject json,
            HashSet<string> visited,
            List<string> found)
        {
            var psCode = json["psCode"]?.GetValue<string>();
            var ids = Strings(json["transitions"]);

            if (psCode is null)
            {
                var unresolved = _breaks.Create(
                    BreakCategories.KeySeqStageDependent,
                    BreakReasons.UnresolvedConstantArgument,
                    SiteOf(state.MethodId, call),
                    new BreakContract { Kind = "keySeq" },
                    new WhyTemplates.Fields { Text = ArgumentText(call) });

                found.Add(unresolved.Id);

                Emit(
                    state.WithBreak(unresolved.Id).With(Step(state, call, ChainStepKinds.KeySeqDispatch, null)),
                    from, null, trigger, unresolved.Id);

                return;
            }

            var rows = ids.Select(id => _transitions.GetValueOrDefault(id))
                .OfType<KeySeqRow>()
                .Where(row => row.HandlerMethodId is not null)
                .ToList();

            var stageBreak = _breaks.Create(
                BreakCategories.KeySeqStageDependent,
                BreakReasons.RuntimeStateDependentDispatch,
                SiteOf(state.MethodId, call),
                new BreakContract { Kind = "keySeq", Key = psCode },
                new WhyTemplates.Fields { Key = psCode, Count = rows.Count },
                rows.Select(row => Resume(row.HandlerMethodId!, StageCondition(row.Stage))).ToList());

            found.Add(stageBreak.Id);
            var dispatched = state.WithBreak(stageBreak.Id);

            if (rows.Count == 0)
            {
                Emit(
                    dispatched.With(Step(state, call, ChainStepKinds.KeySeqDispatch, psCode)),
                    from, null, trigger, stageBreak.Id);

                return;
            }

            foreach (var row in rows)
            {
                var step = Step(dispatched, call, ChainStepKinds.KeySeqDispatch, psCode + " / " + row.Stage);

                var next = dispatched
                    .WithCondition(StageCondition(row.Stage))
                    .AsCandidate()
                    .Into(row.HandlerMethodId!, step with { MethodId = row.HandlerMethodId! });

                Walk(next, from, trigger, visited);
            }
        }

        /// <summary>02 §10.2: queued work runs later, so the chain continues as deferred.</summary>
        private void Enqueue(
            TraversalState state,
            CallsiteRec call,
            ScreenRef from,
            EdgeTrigger trigger,
            JsonObject json,
            HashSet<string> visited,
            List<string> found)
        {
            var lambdas = Strings(json["lambdaIds"]);
            var queue = json["queue"]?.GetValue<string>() ?? "queue";

            var deferred = _breaks.Create(
                BreakCategories.DeferredExecution,
                BreakReasons.DeferredExecution,
                SiteOf(state.MethodId, call),
                new BreakContract { Kind = "queue", Key = queue },
                new WhyTemplates.Fields { Key = queue },
                lambdas.Select(lambda => Resume(lambda, null)).ToList());

            found.Add(deferred.Id);

            foreach (var lambda in lambdas)
            {
                var step = Step(state, call, ChainStepKinds.Enqueue, queue);

                var next = state
                    .WithBreak(deferred.Id)
                    .AsDeferred()
                    .Into(lambda, step with { MethodId = lambda, Kind = ChainStepKinds.Lambda });

                Walk(next, from, trigger, visited);
            }
        }

        /// <summary>
        /// 02 §10.2: a boundary is where the project ends. Hardware answers come back through a
        /// callback, so those are followed; the others are recorded and the chain stops.
        /// </summary>
        private void Boundary(
            TraversalState state,
            CallsiteRec call,
            ScreenRef from,
            EdgeTrigger trigger,
            JsonObject json,
            HashSet<string> visited,
            List<string> found)
        {
            var boundaryId = json["boundaryId"]?.GetValue<string>();
            var row = boundaryId is null ? null : _boundaries.GetValueOrDefault(boundaryId);
            if (row is null) return;

            var contract = JsonNode.Parse(row.Json) as JsonObject ?? new JsonObject();
            var key = row.ContractKey ?? string.Empty;

            var (category, reason, kind) = row.Category switch
            {
                BreakCategories.ExternalHardware =>
                    (BreakCategories.ExternalHardware, BreakReasons.NoSourceForExternalSystem, ChainStepKinds.DeviceCommand),
                BreakCategories.ExternalMicroservice =>
                    (BreakCategories.ExternalMicroservice, BreakReasons.NoSourceForExternalSystem, ChainStepKinds.ApiCall),
                BreakCategories.NativeInterop =>
                    (BreakCategories.NativeInterop, BreakReasons.NativeInterop, ChainStepKinds.Call),
                BreakCategories.DatabaseMediated =>
                    (BreakCategories.DatabaseMediated, BreakReasons.DataMediatedByDatabase, ChainStepKinds.DbRead),
                _ => (BreakCategories.DiAmbiguous, BreakReasons.ReflectionConstruction, ChainStepKinds.Call)
            };

            var writers = Strings(contract["writers"]);
            var callbacks = Strings(contract["callbacks"]);
            var resumeIds = category == BreakCategories.DatabaseMediated ? writers : callbacks;

            var record = _breaks.Create(
                category,
                reason,
                SiteOf(state.MethodId, call),
                new BreakContract
                {
                    Kind = row.ContractKind ?? "unknown",
                    Key = key,
                    RequestTypes = Strings(contract["requestTypes"]),
                    ResponseType = contract["responseType"]?.GetValue<string>(),
                    TopicTemplate = contract["topicTemplate"]?.GetValue<string>()
                },
                new WhyTemplates.Fields
                {
                    Key = key,
                    Topic = contract["topicTemplate"]?.GetValue<string>() ?? string.Empty,
                    Count = resumeIds.Count
                },
                resumeIds.Select(id => Resume(id, HardwareCondition(category))).ToList());

            found.Add(record.Id);
            var crossed = state.WithBreak(record.Id).With(Step(state, call, kind, key));

            // Only the hardware answer comes back into this project (03 §17).
            if (category != BreakCategories.ExternalHardware) return;

            foreach (var callback in callbacks)
            {
                var step = Step(crossed, call, ChainStepKinds.DeviceCallback, key);

                var next = crossed
                    .WithCondition(HardwareCondition(category)!)
                    .AsCandidate()
                    .AsDeferred()
                    .Into(callback, step with { MethodId = callback });

                Walk(next, from, trigger, visited);
            }
        }

        /// <summary>02 §10.2 step 2: an ordinary call, followed into the source it can reach.</summary>
        private void Dispatch(
            TraversalState state,
            CallsiteRec call,
            ScreenRef from,
            EdgeTrigger trigger,
            HashSet<string> visited,
            List<string> found)
        {
            if (call.TargetId is null)
            {
                var unbound = _breaks.Create(
                    BreakCategories.Unknown,
                    BreakReasons.UnresolvedSymbol,
                    SiteOf(state.MethodId, call),
                    new BreakContract { Kind = "unknown" },
                    new WhyTemplates.Fields { Text = ArgumentText(call) });

                // Recorded, but the chain is not ended: the call simply cannot be followed.
                found.Add(unbound.Id);
                return;
            }

            var dispatch = _stage.Dispatcher.Dispatch(call, Resolve(state));
            var candidates = dispatch.Candidates;

            if (dispatch.Truncated)
            {
                var truncated = _breaks.Create(
                    BreakCategories.Unknown,
                    BreakReasons.TruncatedByCandidateLimit,
                    SiteOf(state.MethodId, call),
                    new BreakContract { Kind = "unknown" },
                    new WhyTemplates.Fields { Count = _limits.MaxCandidatesPerDispatch });

                found.Add(truncated.Id);
                state = state.WithBreak(truncated.Id);
            }

            var entered = false;

            foreach (var candidate in candidates)
            {
                var target = Symbol(candidate);
                if (target is null || !target.IsSource) continue;
                if (DoNotDescend(target)) continue;

                entered = true;

                var next = (candidates.Count > 1 ? state.AsCandidate() : state)
                    .Into(candidate, Step(state, call, ChainStepKinds.Call, target.Name) with { MethodId = candidate });

                Walk(next, from, trigger, visited);
            }

            if (entered) return;

            // The target is outside the project, but a lambda handed to it is not: that is how a
            // deferred invoker and a callback-taking framework method continue the chain (02 §10.2).
            foreach (var argument in call.Args)
            {
                var methodId = argument.Value switch
                {
                    LambdaValue lambda => lambda.MethodId,
                    MethodRefValue method => method.MethodId,
                    _ => null
                };

                if (methodId is null) continue;
                var target = Symbol(methodId);
                if (target is null || !target.IsSource) continue;

                var next = state
                    .AsCandidate()
                    .Into(methodId, Step(state, call, ChainStepKinds.Lambda, target.Name) with { MethodId = methodId });

                Walk(next, from, trigger, visited);
            }
        }

        // ------------------------------------------------------------------ edges

        private void Emit(
            TraversalState state,
            ScreenRef from,
            ScreenRef? to,
            EdgeTrigger trigger,
            string? endsAtBreak)
        {
            var chain = state.Chain;
            var candidate = state.Candidate || state.BreakIds.Count > 0
                            || chain.Any(step => step.Confidence == Confidences.Candidate);

            var edge = new ScreenEdge
            {
                EdgeId = ScreenEdge.IdOf(from, to, trigger, chain.Select(step => step.MethodId), endsAtBreak),
                From = from,
                To = to,
                Trigger = trigger,
                Confidence = candidate ? Confidences.Candidate : Confidences.Certain,
                Conditions = state.Conditions,
                Chain = chain,
                BreakIds = state.BreakIds,
                EndsAtBreak = endsAtBreak,
                Cost = ScreenEdge.CostOf(candidate, state.Deferred, state.BreakIds.Count, to is null)
            };

            _edges.Add(edge);
        }

        /// <summary>
        /// Keeps the cheapest chains per transition (02 §10.4) and marks the rest truncated, so a
        /// reader is told that more ways exist rather than shown an arbitrary subset.
        /// </summary>
        private void WriteEdges()
        {
            var groups = _edges
                .GroupBy(edge => (
                    From: edge.From.ToString(),
                    To: edge.To?.ToString() ?? string.Empty,
                    edge.Trigger.Kind,
                    Control: edge.Trigger.ControlId ?? string.Empty))
                .OrderBy(group => group.Key.From, StringComparer.Ordinal)
                .ThenBy(group => group.Key.To, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Kind, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Control, StringComparer.Ordinal);

            foreach (var group in groups)
            {
                var ordered = group
                    .GroupBy(edge => edge.EdgeId, StringComparer.Ordinal)
                    .Select(byId => byId.First())
                    .OrderBy(edge => edge.Cost)
                    .ThenBy(edge => edge.EdgeId, StringComparer.Ordinal)
                    .ToList();

                var kept = ordered.Take(_limits.MaxChainsPerScreenEdge).ToList();
                var truncated = ordered.Count > kept.Count;

                foreach (var edge in kept)
                {
                    var written = truncated ? edge with { Truncated = true } : edge;

                    Writer.Add(new ScreenEdgeRow
                    {
                        Id = written.EdgeId,
                        FromTerminal = written.From.Terminal,
                        FromScreen = written.From.ScreenId,
                        ToTerminal = written.To?.Terminal,
                        ToScreen = written.To?.ScreenId,
                        Trigger = written.Trigger.Kind,
                        ControlId = written.Trigger.ControlId,
                        Confidence = written.Confidence,
                        Cost = written.Cost,
                        EndsAtBreak = written.EndsAtBreak,
                        Json = System.Text.Json.JsonSerializer.Serialize(written, IrJson.Options)
                    });
                }
            }
        }

        /// <summary>
        /// 02 §10.4 and T18 §9: an edge that passed a break cannot be certain. A violation is an
        /// implementation defect, so it fails the build rather than being written out.
        /// </summary>
        private void CheckInvariants()
        {
            var offenders = _edges
                .Where(edge => edge.BreakIds.Count > 0 && edge.Confidence == Confidences.Certain)
                .ToList();

            if (offenders.Count == 0) return;

            throw new InvalidOperationException(
                $"{InvariantCode}: {offenders.Count} screen edge(s) carry a break but are marked certain " +
                $"(02 §10.4). First: {offenders[0].EdgeId}.");
        }

        // ------------------------------------------------------------------ helpers

        private const string EventPathKey = "EventPath";

        private bool ReceiveDeferred { get; set; } = true;

        private ChainStep Step(TraversalState state, CallsiteRec call, string kind, string? detail)
        {
            var method = Symbol(state.MethodId);

            return new ChainStep
            {
                Order = state.Chain.Count,
                Kind = kind,
                Terminal = state.Terminal,
                MethodId = state.MethodId,
                Namespace = method?.Namespace,
                Type = method?.ContainerId is null ? null : Symbol(method.ContainerId)?.Name,
                Method = method?.Name,
                File = method?.File,
                Line = call.Span.StartLine,
                Confidence = state.Candidate ? Confidences.Candidate : Confidences.Certain,
                Deferred = state.Deferred,
                Detail = detail,
                EvidenceId = Evidence(method?.File, call.Span)
            };
        }

        private BreakSite SiteOf(string methodId, CallsiteRec? call)
        {
            var method = Symbol(methodId);

            return new BreakSite
            {
                CallsiteId = call?.Id,
                MethodId = methodId,
                Namespace = method?.Namespace,
                Type = method?.ContainerId is null ? null : Symbol(method.ContainerId)?.Name,
                Method = method?.Name,
                File = method?.File,
                Line = call?.Span.StartLine ?? method?.Span?.StartLine,
                EvidenceId = Evidence(method?.File, call?.Span ?? method?.Span)
            };
        }

        private ResumeCandidate Resume(string methodId, string? condition)
        {
            var method = Symbol(methodId);

            return new ResumeCandidate
            {
                MethodId = methodId,
                Type = method?.ContainerId is null ? null : Symbol(method.ContainerId)?.Name,
                Method = method?.Name,
                File = method?.File,
                Line = method?.Span?.StartLine,
                Condition = condition
            };
        }

        private string? Evidence(string? file, Span? span)
        {
            if (file is null || span is null) return null;

            var id = Ids.EvidenceId(file, span);
            return _reader.Evidence(id) is null ? null : id;
        }

        private SymbolRec? Symbol(string id)
        {
            if (_symbols.TryGetValue(id, out var cached)) return cached;

            var symbol = _reader.Symbol(id);
            _symbols[id] = symbol;
            return symbol;
        }

        private bool DoNotDescend(SymbolRec method)
            => method.Namespace is not null
               && _limits.DoNotDescendNamespaces.Any(prefix =>
                   method.Namespace.Equals(prefix, StringComparison.Ordinal)
                   || method.Namespace.StartsWith(prefix + ".", StringComparison.Ordinal));

        private ResolveContext Resolve(TraversalState state)
            => ResolveContext.FromConfig(
                _limits,
                _stage.Providers.ToList(),
                Symbol(state.MethodId)?.ContainerId);

        /// <summary>
        /// 02 §10.4: the condition a remote receiver runs under, in the terminal's own words. The
        /// two POS terminals have fixed names (T18 §4); any other terminal is named by its id.
        /// </summary>
        private static string ScreenCondition(string terminal, MessageHandler receiver)
            => $"{TerminalName(terminal)}当前画面 ∈ {{{receiver.ScreenId}}}";

        private static string StageCondition(string stage) => $"当前 Stage = {stage}";

        private static string? HardwareCondition(string category)
            => category == BreakCategories.ExternalHardware ? "硬件响应到达" : null;

        public static string TerminalName(string terminal) => terminal switch
        {
            "employee" => "员工面",
            "customer" => "客面",
            _ => terminal
        };

        private static string ArgumentText(CallsiteRec call)
            => call.Args.FirstOrDefault()?.Value.Text ?? string.Empty;

        private static string Key(ScreenRef screen) => screen.ToString();

        private static IReadOnlyList<string> Strings(JsonNode? node)
            => node is JsonArray array
                ? array.Select(item => item!.GetValue<string>()).ToList()
                : Array.Empty<string>();

        /// <summary>
        /// The receivers on the peer terminal an event reaches (02 §10.2). When the send names a
        /// screen, only that screen's handlers are considered.
        /// </summary>
        private IReadOnlyList<MessageHandler> Receivers(
            string peer,
            IReadOnlyList<string> keys,
            IReadOnlyList<string> targets)
        {
            var matched = new List<MessageHandler>();

            foreach (var handler in _handlers.Where(handler => handler.Terminal == peer))
            {
                if (targets.Count > 0 && !targets.Contains(handler.ScreenId, StringComparer.Ordinal)) continue;

                var keysMatched = keys
                    .Where(key => handler.Literals.Any(literal =>
                        EventPathMatcher.Satisfies(key, new StringTest
                        {
                            Subject = UnknownValue.Of(),
                            Op = literal.Op,
                            Literal = literal.Literal
                        })))
                    .ToList();

                if (keysMatched.Count == 0) continue;

                matched.Add(handler with { MatchedKeys = keysMatched });
            }

            return matched
                .OrderBy(handler => handler.ScreenId, StringComparer.Ordinal)
                .ThenBy(handler => handler.HandlerMethodId, StringComparer.Ordinal)
                .ToList();
        }

        // ------------------------------------------------------------------ loading

        private void Load()
        {
            foreach (var kind in DomainEdgeKinds.All)
            foreach (var edge in _reader.DomainEdges(kind))
            {
                if (edge.CallsiteId is null) continue;

                if (!_edgesByCallsite.TryGetValue(edge.CallsiteId, out var list))
                {
                    list = new List<DomainEdgeRow>();
                    _edgesByCallsite[edge.CallsiteId] = list;
                }

                list.Add(edge);
            }

            foreach (var screen in _reader.Screens().Where(screen => screen.Registered))
                _screens[screen.Terminal + "/" + screen.ScreenId] = screen;

            LoadHandlers();
            LoadTransitions();
            LoadBoundaries();
            LoadTcpOptions();
        }

        private void LoadHandlers()
        {
            using var command = _reader.Db.Command(
                "SELECT terminal, screen_id, handler_method_id, key_literals_json FROM message_handlers ORDER BY id;");

            using var rows = command.ExecuteReader();

            while (rows.Read())
            {
                if (rows.IsDBNull(0) || rows.IsDBNull(1)) continue;

                var literals = new List<(string Op, string Literal)>();

                if (!rows.IsDBNull(3) && JsonNode.Parse(rows.GetString(3)) is JsonArray array)
                {
                    foreach (var entry in array)
                    {
                        literals.Add((
                            entry!["op"]!.GetValue<string>(),
                            entry["literal"]!.GetValue<string>()));
                    }
                }

                _handlers.Add(new MessageHandler
                {
                    Terminal = rows.GetString(0),
                    ScreenId = rows.GetString(1),
                    HandlerMethodId = rows.GetString(2),
                    Literals = literals
                });
            }
        }

        private void LoadTransitions()
        {
            using var command = _reader.Db.Command(
                "SELECT id, stage, ps_code, handler_method_id FROM keyseq_transitions ORDER BY id;");

            using var rows = command.ExecuteReader();

            while (rows.Read())
            {
                _transitions[rows.GetString(0)] = new KeySeqRow
                {
                    Id = rows.GetString(0),
                    Stage = rows.GetString(1),
                    PsCode = rows.GetString(2),
                    HandlerMethodId = rows.IsDBNull(3) ? null : rows.GetString(3)
                };
            }
        }

        private void LoadBoundaries()
        {
            using var command = _reader.Db.Command(
                "SELECT id, category, contract_kind, contract_key, json FROM boundaries ORDER BY id;");

            using var rows = command.ExecuteReader();

            while (rows.Read())
            {
                _boundaries[rows.GetString(0)] = new BoundaryRow
                {
                    Id = rows.GetString(0),
                    Category = rows.GetString(1),
                    ContractKind = rows.IsDBNull(2) ? null : rows.GetString(2),
                    ContractKey = rows.IsDBNull(3) ? null : rows.GetString(3),
                    Json = rows.GetString(4)
                };
            }
        }

        /// <summary>Whether a received event runs later; the core reads only this one flag (02 §9.7).</summary>
        private void LoadTcpOptions()
        {
            if (_stage.Domain.Tcp.ValueKind != System.Text.Json.JsonValueKind.Object) return;

            if (_stage.Domain.Tcp.TryGetProperty("receiveDeferred", out var deferred)
                && deferred.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                ReceiveDeferred = deferred.GetBoolean();
            }
        }

        private IReadOnlyList<EntryPoint> Entries()
        {
            using var command = _reader.Db.Command(
                "SELECT id, terminal, screen_id, trigger, control_id, method_id FROM entries ORDER BY id;");

            var entries = new List<EntryPoint>();
            using var rows = command.ExecuteReader();

            while (rows.Read())
            {
                entries.Add(new EntryPoint
                {
                    Id = rows.GetString(0),
                    Terminal = rows.GetString(1),
                    ScreenId = rows.GetString(2),
                    Trigger = rows.GetString(3),
                    ControlId = rows.IsDBNull(4) ? null : rows.GetString(4),
                    MethodId = rows.GetString(5)
                });
            }

            return entries;
        }

        private sealed record EntryPoint
        {
            public required string Id { get; init; }
            public required string Terminal { get; init; }
            public required string ScreenId { get; init; }
            public required string Trigger { get; init; }
            public string? ControlId { get; init; }
            public required string MethodId { get; init; }
        }

        private sealed record MessageHandler
        {
            public required string Terminal { get; init; }
            public required string ScreenId { get; init; }
            public required string HandlerMethodId { get; init; }
            public IReadOnlyList<(string Op, string Literal)> Literals { get; init; }
                = Array.Empty<(string, string)>();

            public IReadOnlyList<string> MatchedKeys { get; init; } = Array.Empty<string>();
        }

        private sealed record KeySeqRow
        {
            public required string Id { get; init; }
            public required string Stage { get; init; }
            public required string PsCode { get; init; }
            public string? HandlerMethodId { get; init; }
        }
    }
}

/// <summary>
/// The domain edge kinds the traversal understands (02 §9). The core knows these names and the
/// shape of each edge's json, and nothing else about the domain.
/// </summary>
public static class DomainEdgeKinds
{
    public const string Navigate = "navigate";
    public const string ClosePopup = "closePopup";
    public const string MessageSend = "messageSend";
    public const string KeySeqDispatch = "keySeqDispatch";
    public const string Enqueue = "enqueue";
    public const string Boundary = "boundary";
    public const string ServiceResolve = "serviceResolve";
    public const string TableAccess = "tableAccess";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Navigate, ClosePopup, MessageSend, KeySeqDispatch, Enqueue, Boundary, ServiceResolve, TableAccess
    };
}
