using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Anchors;

namespace ScreenFlow.UnityPos.Screens;

/// <summary>The <c>navigation</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record NavigationSection
{
    [JsonPropertyName("managerType")] public required AnchorSpec ManagerType { get; init; }

    [JsonPropertyName("displayMethods")]
    public IReadOnlyList<string> DisplayMethods { get; init; } = Array.Empty<string>();

    [JsonPropertyName("closeMethods")]
    public IReadOnlyList<string> CloseMethods { get; init; } = Array.Empty<string>();

    [JsonPropertyName("screenArgIndex")] public int ScreenArgIndex { get; init; }
}

/// <summary>
/// Local screen transitions: a call to the screen manager (02 §9.6; 03 §8).
/// <para>
/// This is the one kind of transition that is a plain call, so it is also the one where a wrong
/// answer is most visible. An unreachable call produces no edge — the fixture navigates to SSAW0002
/// after a <c>return</c> and inside a <c>#if TeamUI</c> block, and neither is a transition that
/// happens (E-10).
/// </para>
/// </summary>
public sealed class NavigationAdapter : IDomainAdapter
{
    public const string Feature = "navigation";

    public const string NavigateKind = "navigate";
    public const string ClosePopupKind = "closePopup";

    /// <summary>Edge id prefix; derived ids are a prefix plus a hash of the fields (02 §5.6).</summary>
    public const string EdgePrefix = "de_";

    /// <summary>Emitted when a screen argument cannot be resolved to a screen.</summary>
    public const string UnresolvedScreenCode = "I_NAVIGATION_SCREEN_UNRESOLVED";

    public string Id => AdapterOrder.Navigation;

    public void Run(DomainContext context)
    {
        var config = context.Section<NavigationSection>(context.Domain.Navigation, "navigation");

        var anchor = context.Anchors.Resolve(config.ManagerType);
        if (context.AnchorFailed(anchor, Id, Feature)) return;

        var managerType = anchor.Require();

        var screens = context.Providers.OfType<ScreenIdValueProvider>().FirstOrDefault();
        if (screens is null)
        {
            // The screens adapter runs first (02 §2); without it there is nothing to name a screen.
            context.Diagnose(DiagRec.Error(
                AnchorResolver.UnresolvedCode,
                $"[{AdapterOrder.Navigation}] The screens adapter registered no screen id provider, " +
                "so a navigation target cannot be named."));

            context.Disable(Feature);
            return;
        }

        foreach (var method in Methods(context, managerType, config.DisplayMethods))
            foreach (var callsite in Reachable(context, method))
                WriteNavigate(context, config, screens, method, callsite);

        foreach (var method in Methods(context, managerType, config.CloseMethods))
            foreach (var callsite in Reachable(context, method))
                WriteClose(context, method, callsite);
    }

    /// <summary>
    /// The manager methods of those names, declared on the manager type or on a type derived from
    /// it (02 §9.6). Looked up by name within the anchored hierarchy, never by name alone.
    /// </summary>
    private static IEnumerable<SymbolRec> Methods(
        DomainContext context,
        SymbolRec managerType,
        IReadOnlyList<string> names)
        => names
            .SelectMany(name => context.Reader.SymbolsByName(name))
            .Where(symbol => symbol.Kind == SymbolKinds.Method)
            .Where(symbol => symbol.ContainerId is not null
                             && (symbol.ContainerId == managerType.Id
                                 || context.Resolution.IsSubtypeOf(symbol.ContainerId, managerType.Id)))
            .Distinct()
            .OrderBy(symbol => symbol.Id, StringComparer.Ordinal);

    /// <summary>
    /// Callsites that can actually run. An unreachable call is kept in the index as a fact
    /// (02 §5.5) but it is not a transition, so it produces no edge (E-10).
    /// </summary>
    private static IEnumerable<CallsiteRec> Reachable(DomainContext context, SymbolRec method)
        => context.Reader.CallsitesTargeting(method.Id)
            .Where(callsite => callsite.Reachable)
            .OrderBy(callsite => callsite.Id, StringComparer.Ordinal);

    private static void WriteNavigate(
        DomainContext context,
        NavigationSection config,
        ScreenIdValueProvider screens,
        SymbolRec method,
        CallsiteRec callsite)
    {
        var argument = callsite.Args.FirstOrDefault(a => a.ParamIndex == config.ScreenArgIndex);

        var resolved = argument is null
            ? ScreenIdSet.Unresolved(string.Empty)
            : screens.ScreenIdsOf(argument.Value, context.Resolve(context.Resolution.DeclaringTypeOf(callsite.CallerId)));

        var json = new JsonObject
        {
            ["screenIds"] = new JsonArray(resolved.ScreenIds.Select(id => (JsonNode)id!).ToArray()),
            ["mode"] = "self",
            ["popup"] = IsPopup(method.Name),
            ["method"] = method.Name,
            ["complete"] = resolved.Complete
        };

        if (resolved.ScreenIds.Count == 0)
        {
            // Saying which text could not be resolved is what makes the later break link readable
            // (02 §8.3, UnresolvedConstantArgument).
            json["unresolvedText"] = resolved.UnresolvedText ?? argument?.Value.Text ?? string.Empty;

            context.Diagnose(DiagRec.Info(
                UnresolvedScreenCode,
                $"[{AdapterOrder.Navigation}] The screen argument of '{method.Name}' at " +
                $"{Describe(context, callsite)} does not resolve to a screen: " +
                $"'{json["unresolvedText"]!.GetValue<string>()}'.",
                context.Reader.Symbol(callsite.CallerId)?.File,
                callsite.Span.StartLine));
        }

        Write(context, NavigateKind, callsite, json);
    }

    private static void WriteClose(DomainContext context, SymbolRec method, CallsiteRec callsite)
        => Write(
            context,
            ClosePopupKind,
            callsite,
            new JsonObject
            {
                ["mode"] = "self",
                ["method"] = method.Name,
                ["popup"] = true
            });

    private static void Write(DomainContext context, string kind, CallsiteRec callsite, JsonObject json)
    {
        var payload = json.ToJsonString();

        context.Writer.Add(new DomainEdgeRow
        {
            Id = Ids.DerivedId(EdgePrefix, new { kind, callsite = callsite.Id, json = payload }),
            Kind = kind,
            CallsiteId = callsite.Id,
            FromMethodId = callsite.CallerId,
            Json = payload
        });
    }

    /// <summary>A popup transition is told from a screen one by the method's own name (02 §9.6).</summary>
    private static bool IsPopup(string methodName)
        => methodName.Contains("Popup", StringComparison.Ordinal);

    private static string Describe(DomainContext context, CallsiteRec callsite)
    {
        var caller = context.Reader.Symbol(callsite.CallerId);
        return caller?.File is null
            ? callsite.Id
            : $"{caller.File}:{callsite.Span.StartLine}";
    }
}
