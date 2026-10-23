using System.Text.Json.Nodes;
using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Tcp;

/// <summary>
/// The receiving side of a TCP event (02 §9.7 step 2; 03 §10.5).
/// <para>
/// A receiver is a method implementing the socket interface on a type that registered itself as the
/// handler. Which event paths it answers is in the branch tests inside it, and those literals are
/// what pairs a send with a receiver — the <c>Customer.</c> and <c>PosRegister.</c> prefixes are
/// naming habits and say nothing about the terminal (03 §10.5).
/// </para>
/// </summary>
public sealed class TcpReceiverAdapter : IDomainAdapter
{
    public const string HandlerPrefix = "mh_";

    /// <summary>Emitted when a receiver implements the interface but never registers itself.</summary>
    public const string NotRegisteredCode = "I_TCP_RECEIVER_NOT_REGISTERED";

    /// <summary>Emitted when a receiver's type is not the view or presenter of any screen.</summary>
    public const string NoScreenCode = "I_TCP_RECEIVER_HAS_NO_SCREEN";

    public string Id => AdapterOrder.Tcp + ":receivers";

    public void Run(DomainContext context)
    {
        var config = context.Section<TcpSection>(context.Domain.Tcp, "tcp");

        var screensByType = ScreensByType(context);

        foreach (var handler in Handlers(context, config))
        {
            context.Ct.ThrowIfCancellationRequested();

            var type = handler.ContainerId is null ? null : context.Reader.Symbol(handler.ContainerId);
            if (type is null) continue;

            if (!RegistersItself(context, config, type))
            {
                // Implementing the interface is not enough: an unregistered receiver never runs,
                // and claiming it as a handler would invent a hop (02 §9.7 step 2).
                context.Diagnose(DiagRec.Info(
                    NotRegisteredCode,
                    $"[{TcpSendAdapter.Feature}] '{type.FqName}' implements " +
                    $"'{config.ReceiverInterfaceMethod}' but never calls " +
                    $"'{config.ReceiverRegistrationMethod}' with itself, so it is not a receiver.",
                    type.File,
                    type.Span?.StartLine));

                continue;
            }

            var screen = screensByType.GetValueOrDefault(type.Id);

            if (screen is null)
            {
                context.Diagnose(DiagRec.Info(
                    NoScreenCode,
                    $"[{TcpSendAdapter.Feature}] '{type.FqName}' is a registered receiver but is " +
                    "neither the view nor the presenter of a registered screen.",
                    type.File,
                    type.Span?.StartLine));
            }

            var literals = KeyLiterals(context, config, handler);

            context.Writer.Add(new MessageHandlerRow
            {
                Id = Ids.DerivedId(HandlerPrefix, new { channel = TcpSendAdapter.Channel, handler = handler.Id }),
                Channel = TcpSendAdapter.Channel,
                Terminal = screen?.Terminal,
                ScreenId = screen?.ScreenId,
                HandlerMethodId = handler.Id,
                KeyLiteralsJson = literals.ToJsonString(),
                EvidenceId = context.EvidenceOf(handler)
            });
        }
    }

    /// <summary>
    /// Methods implementing the socket interface method (02 §9.7 step 2). Found through the
    /// implementation map rather than by method name, so a same-named method on an unrelated type
    /// is not mistaken for a receiver (CLAUDE.md §4).
    /// </summary>
    private static IReadOnlyList<SymbolRec> Handlers(DomainContext context, TcpSection config)
    {
        var contracts = context.Reader.SymbolsByName(config.ReceiverInterfaceMethod)
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .Where(symbol => symbol.ContainerId is not null && context.Resolution.IsInterface(symbol.ContainerId))
            .ToList();

        var handlers = new List<SymbolRec>();

        foreach (var contract in contracts)
        foreach (var implementation in context.Resolution.ImplementationsOf(contract.Id))
        {
            var symbol = context.Reader.Symbol(implementation);
            if (symbol is not null) handlers.Add(symbol);
        }

        return handlers
            .GroupBy(symbol => symbol.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether the type hands itself to the registration method anywhere (02 §9.7 step 2). The
    /// fixture does it in <c>OnInstanced</c>, through the view (03 §10.5).
    /// </summary>
    private static bool RegistersItself(DomainContext context, TcpSection config, SymbolRec type)
    {
        foreach (var method in context.Reader.SymbolsInContainer(type.Id)
                     .Where(symbol => symbol.Kind == SymbolKinds.Method))
        {
            foreach (var call in context.Reader.CallsitesIn(method.Id))
            {
                var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
                if (target?.Name != config.ReceiverRegistrationMethod) continue;

                if (call.Args.Any(argument => argument.Value is ThisValue)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The event path literals the handler branches on (02 §9.7 step 2). Collected from the guards
    /// of every call it makes, which is where the branch tests end up (02 §5.7).
    /// </summary>
    private static JsonArray KeyLiterals(DomainContext context, TcpSection config, SymbolRec handler)
    {
        var tests = new List<(string Op, string Literal)>();

        foreach (var call in context.Reader.CallsitesIn(handler.Id))
        foreach (var constraint in EventPathMatcher.ConstraintsOf(call.Conditions, config.EventPathIndexerKey))
        foreach (var test in constraint.Tests)
        {
            // Only the positive side is a key this handler answers; a negated test says which keys
            // it does not (02 §10.3), and that is not a key literal.
            if (!constraint.Polarity) continue;

            tests.Add((test.Op, test.Literal));
        }

        var array = new JsonArray();

        foreach (var (op, literal) in tests
                     .Distinct()
                     .OrderBy(test => test.Literal, StringComparer.Ordinal)
                     .ThenBy(test => test.Op, StringComparer.Ordinal))
        {
            array.Add(new JsonObject { ["op"] = op, ["literal"] = literal });
        }

        return array;
    }

    /// <summary>Screens by the id of their view or presenter type (02 §9.1).</summary>
    private static IReadOnlyDictionary<string, ScreenRow> ScreensByType(DomainContext context)
    {
        var byType = new Dictionary<string, ScreenRow>(StringComparer.Ordinal);

        foreach (var screen in context.Reader.Screens().Where(screen => screen.Registered))
        {
            if (screen.PresenterTypeId is not null) byType[screen.PresenterTypeId] = screen;
            if (screen.ViewTypeId is not null && !byType.ContainsKey(screen.ViewTypeId))
                byType[screen.ViewTypeId] = screen;
        }

        return byType;
    }
}
