using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Resolution;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Screens;

namespace ScreenFlow.UnityPos.Tcp;

/// <summary>The <c>tcp</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record TcpSection
{
    [JsonPropertyName("sendMethods")]
    public IReadOnlyList<AnchorSpec> SendMethods { get; init; } = Array.Empty<AnchorSpec>();

    [JsonPropertyName("eventPathParamName")] public string EventPathParamName { get; init; } = "eventPath";
    [JsonPropertyName("payloadParamName")] public string PayloadParamName { get; init; } = "socketRequestData";
    [JsonPropertyName("payloadType")] public AnchorSpec? PayloadType { get; init; }
    [JsonPropertyName("payloadEventIdProperty")] public string PayloadEventIdProperty { get; init; } = "EventID";
    [JsonPropertyName("payloadTargetIdProperty")] public string PayloadTargetIdProperty { get; init; } = "TargetID";
    [JsonPropertyName("commonEventIdPrefix")] public string CommonEventIdPrefix { get; init; } = "Common";

    /// <summary>Event id to the display mode it causes, e.g. <c>Common.DisplayPopup</c> to popup.</summary>
    [JsonPropertyName("displayEventIds")]
    public IReadOnlyDictionary<string, string> DisplayEventIds { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("closeEventIds")]
    public IReadOnlyList<string> CloseEventIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("displayEventIdsVerifiedTerminals")]
    public IReadOnlyList<string> DisplayEventIdsVerifiedTerminals { get; init; } = Array.Empty<string>();

    [JsonPropertyName("receiverInterfaceMethod")] public string ReceiverInterfaceMethod { get; init; } = "OnReceiveSocket";
    [JsonPropertyName("receiverRegistrationMethod")] public string ReceiverRegistrationMethod { get; init; } = "SetBaseSocketEventHandler";
    [JsonPropertyName("eventPathIndexerKey")] public string EventPathIndexerKey { get; init; } = "EventPath";
    [JsonPropertyName("receiveDeferred")] public bool ReceiveDeferred { get; init; } = true;
}

/// <summary>Event id kinds (02 §9.7 step 1).</summary>
public static class EventIdKinds
{
    public const string Display = "display";
    public const string Close = "close";
    public const string Common = "common";

    public static readonly IReadOnlyList<string> All = new[] { Display, Close, Common };
}

/// <summary>
/// TCP sends between the two terminals (02 §9.7 step 1; 03 §10).
/// <para>
/// This is how one terminal drives the other's screen, and the event path is the whole content of
/// the hop. It often arrives as a parameter (03 §10.1), so each caller that supplies one becomes
/// its own send: a wrapper called from two places sends two different paths, and merging them would
/// invent a transition from each caller to every path.
/// </para>
/// </summary>
public sealed class TcpSendAdapter : IDomainAdapter
{
    public const string Feature = "tcp";
    public const string Kind = "messageSend";
    public const string Channel = "tcp";
    public const string EdgePrefix = "de_";

    /// <summary>Emitted when a send's event path cannot be resolved to any string.</summary>
    public const string UnresolvedKeyCode = "I_TCP_EVENT_PATH_UNRESOLVED";

    public string Id => AdapterOrder.Tcp;

    public void Run(DomainContext context)
    {
        var config = context.Section<TcpSection>(context.Domain.Tcp, "tcp");
        var sendMethods = SendMethods(context, config);

        if (sendMethods.Count == 0)
        {
            context.Disable(Feature);
            return;
        }

        foreach (var methodId in sendMethods)
        foreach (var call in context.Reader.CallsitesTargeting(methodId)
                     .Where(call => call.Reachable)
                     .OrderBy(call => call.Id, StringComparer.Ordinal))
        {
            context.Ct.ThrowIfCancellationRequested();
            WriteSend(context, config, call);
        }
    }

    private static void WriteSend(DomainContext context, TcpSection config, CallsiteRec call)
    {
        var payload = Payload(context, config, call);
        var eventId = payload.EventId;
        var eventIdKind = KindOf(config, eventId);

        foreach (var origin in EventPaths(context, config, call))
        {
            var json = new JsonObject
            {
                ["channel"] = Channel,
                ["keys"] = new JsonArray(origin.Keys.Select(key => (JsonNode)key!).ToArray()),
                ["keysComplete"] = origin.Complete,
                ["eventId"] = eventId,
                ["eventIdKind"] = eventIdKind,
                ["displayTargets"] = new JsonArray(payload.Targets.Select(target => (JsonNode)target!).ToArray()),
                ["displayMode"] = DisplayMode(config, eventId),
                ["effectiveSites"] = new JsonArray(
                    origin.EffectiveSites.Select(site => (JsonNode)site!).ToArray())
            };

            if (origin.Keys.Count == 0)
            {
                json["unresolvedText"] = origin.Text;

                context.Diagnose(DiagRec.Info(
                    UnresolvedKeyCode,
                    $"[{Feature}] A send at {Describe(context, call)} has no statically known event " +
                    $"path: '{origin.Text}'.",
                    context.Reader.Symbol(call.CallerId)?.File,
                    call.Span.StartLine));
            }

            var payloadText = json.ToJsonString();

            context.Writer.Add(new DomainEdgeRow
            {
                Id = Ids.DerivedId(EdgePrefix, new
                {
                    kind = Kind,
                    callsite = call.Id,
                    source = origin.SourceCallsiteId ?? string.Empty
                }),
                Kind = Kind,
                CallsiteId = call.Id,
                FromMethodId = call.CallerId,
                Json = payloadText
            });
        }
    }

    /// <summary>One send as seen from one caller (02 §9.7 step 1).</summary>
    private sealed record SendOrigin
    {
        public string? SourceCallsiteId { get; init; }
        public IReadOnlyList<string> Keys { get; init; } = Array.Empty<string>();
        public bool Complete { get; init; }
        public string Text { get; init; } = string.Empty;
        public IReadOnlyList<string> EffectiveSites { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The event paths of a send. A path that arrives as a parameter is resolved once per caller,
    /// because each caller is a separate send (02 §9.7 step 1, T15 §2).
    /// </summary>
    private static IReadOnlyList<SendOrigin> EventPaths(
        DomainContext context,
        TcpSection config,
        CallsiteRec call)
    {
        var argument = call.Args.FirstOrDefault(a => a.ParamName == config.EventPathParamName)
                       ?? call.Args.FirstOrDefault(a => a.ParamIndex == 0);

        if (argument is null)
        {
            return new[]
            {
                new SendOrigin { EffectiveSites = new[] { call.Id }, Complete = false }
            };
        }

        if (argument.Value is ParamValue parameter)
        {
            var origins = new List<SendOrigin>();

            foreach (var source in context.Reader.CallsitesTargeting(parameter.MethodId)
                         .OrderBy(source => source.Id, StringComparer.Ordinal))
            {
                var passed = source.Args.FirstOrDefault(a => a.ParamIndex == parameter.Index);
                if (passed is null) continue;

                var resolved = context.Values.ResolveStrings(passed.Value, context.Resolve());

                origins.Add(new SendOrigin
                {
                    SourceCallsiteId = source.Id,
                    Keys = resolved.Strings,
                    Complete = resolved.Complete,
                    Text = passed.Value.Text,

                    // The caller is where the path is decided, so that is the site a report cites.
                    EffectiveSites = new[] { source.Id }
                });
            }

            if (origins.Count > 0) return origins;
        }

        var direct = context.Values.ResolveStrings(argument.Value, context.Resolve());

        return new[]
        {
            new SendOrigin
            {
                Keys = direct.Strings,
                Complete = direct.Complete,
                Text = argument.Value.Text,
                EffectiveSites = new[] { call.Id }
            }
        };
    }

    /// <summary>One send's payload: the event id and the screens it targets (02 §9.7 step 1).</summary>
    private sealed record PayloadFacts(string? EventId, IReadOnlyList<string> Targets);

    private static PayloadFacts Payload(DomainContext context, TcpSection config, CallsiteRec call)
    {
        var argument = call.Args.FirstOrDefault(a => a.ParamName == config.PayloadParamName);
        if (argument is null) return new PayloadFacts(null, Array.Empty<string>());

        var creation = Creation(argument.Value);
        if (creation is null) return new PayloadFacts(null, Array.Empty<string>());

        var eventId = Member(context, creation, config.PayloadEventIdProperty);
        var targets = Member(context, creation, config.PayloadTargetIdProperty, all: true);

        return new PayloadFacts(eventId.FirstOrDefault(), targets);
    }

    private static ObjectCreationValue? Creation(Value value) => value switch
    {
        ObjectCreationValue creation => creation,
        LocalValue { Origin: not null } local => Creation(local.Origin),
        _ => null
    };

    /// <summary>
    /// A payload member's value as strings. <c>TargetID</c> is usually <c>ScreenID.X.Id</c>, which
    /// only the screens provider can fold into a screen id (02 §9.1 step 4).
    /// </summary>
    private static IReadOnlyList<string> Member(
        DomainContext context,
        ObjectCreationValue creation,
        string name,
        bool all = false)
    {
        var member = creation.Members.FirstOrDefault(m => m.Name == name);
        if (member is null) return Array.Empty<string>();

        var resolved = context.Values.ResolveStrings(member.Value, context.Resolve());
        return all ? resolved.Strings : resolved.Strings.Take(1).ToList();
    }

    private static string? KindOf(TcpSection config, string? eventId)
    {
        if (eventId is null) return null;
        if (config.DisplayEventIds.ContainsKey(eventId)) return EventIdKinds.Display;
        if (config.CloseEventIds.Contains(eventId, StringComparer.Ordinal)) return EventIdKinds.Close;

        return eventId.StartsWith(config.CommonEventIdPrefix, StringComparison.Ordinal)
            ? EventIdKinds.Common
            : null;
    }

    private static string? DisplayMode(TcpSection config, string? eventId)
        => eventId is not null && config.DisplayEventIds.TryGetValue(eventId, out var mode) ? mode : null;

    /// <summary>The send methods the configuration anchors (02 §4.2).</summary>
    private static IReadOnlyList<string> SendMethods(DomainContext context, TcpSection config)
    {
        var methods = new List<string>();

        foreach (var anchor in config.SendMethods)
        {
            var resolution = context.Anchors.Resolve(anchor);

            if (context.AnchorFailed(resolution, AdapterOrder.Tcp, Feature)) continue;

            methods.AddRange(resolution.Symbols
                .Where(symbol => symbol.Kind == SymbolKinds.Method)
                .Select(symbol => symbol.Id));
        }

        return methods.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    internal static string Describe(DomainContext context, CallsiteRec call)
    {
        var caller = context.Reader.Symbol(call.CallerId);
        return caller?.File is null ? call.Id : $"{caller.File}:{call.Span.StartLine}";
    }
}
