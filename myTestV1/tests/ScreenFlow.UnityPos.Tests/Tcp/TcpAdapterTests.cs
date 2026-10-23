using System.Text.Json.Nodes;
using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Tcp;
using Xunit;
using Xunit.Abstractions;

namespace ScreenFlow.UnityPos.Tests.Tcp;

/// <summary>
/// The TCP adapters (02 §9.7) and the branch matcher (02 §10.3).
/// Covers E-16, E-17 and E-18.
/// </summary>
public sealed class TcpAdapterTests
{
    private readonly ITestOutputHelper _output;

    public TcpAdapterTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// E-16: the event path of a send often arrives as a parameter (03 §10.1). Following it back to
    /// the caller is what turns "this wrapper sends something" into the two paths it can send, and
    /// the caller is recorded as the effective site.
    /// </summary>
    [Fact]
    [Trait("Task", "T15")]
    public void Tcp_Sends_Resolved()
    {
        var index = DomainIndex.Editor;
        var sends = Sends(index);

        foreach (var send in sends)
            _output.WriteLine($"{string.Join(" | ", send.Keys)} -> {send.EventId} {string.Join(",", send.DisplayTargets)}");

        // Every send in the fixture resolved to at least one event path.
        Assert.Equal(10, sends.Count);
        Assert.All(sends, send => Assert.NotEmpty(send.Keys));
        Assert.All(sends, send => Assert.True(send.KeysComplete));

        // The wrapper's path comes from its caller and is both strings it can be (E-16, 03 §13.2).
        var wrapped = sends.Single(send => send.Keys.Contains("ToSSAW0907"));
        Assert.Equal(new[] { "ToSSAW0907", "ToSSAW0907.employee" }, wrapped.Keys);

        // The effective site is the caller that decided the path, not the send itself.
        var effective = Assert.Single(wrapped.EffectiveSites);
        var callsite = index.Reader.Callsite(effective);
        Assert.NotNull(callsite);

        var caller = index.Reader.Symbol(callsite!.CallerId);
        Assert.Equal("OnClickBtnOther", caller!.Name);
        Assert.NotEqual(effective, wrapped.CallsiteId);

        // The payload is read through the same wrapper: its TargetID is the screen the caller
        // passed, which only resolves by following that parameter too (02 §9.1 step 4).
        Assert.Equal("Common.DisplayPopup", wrapped.EventId);
        Assert.Equal(EventIdKinds.Display, wrapped.EventIdKind);
        Assert.Equal(new[] { "SSAW0907" }, wrapped.DisplayTargets);
        Assert.Equal("popup", wrapped.DisplayMode);

        // A send with no payload has no event id, and that is not a display event.
        var plain = sends.Single(send => send.Keys.Contains("PosRegister.SSAW0907.Close"));
        Assert.Null(plain.EventId);
        Assert.Null(plain.EventIdKind);
        Assert.Empty(plain.DisplayTargets);
    }

    /// <summary>
    /// E-17: the commented-out request in <c>OnClickBtnEdy</c> would have targeted SSAW0907
    /// (05 §3.3). No send may carry it — a comment is never a parsing fact (CLAUDE.md §4).
    /// </summary>
    [Fact]
    [Trait("Task", "T15")]
    public void Tcp_CommentedCode_Ignored()
    {
        var index = DomainIndex.Editor;
        var sends = Sends(index);

        var fromEdy = sends.Single(send => send.Keys.Contains("Customer.NextDispReq.SSAW0903"));

        // The live request targets SSAW0903; the commented one named SSAW0907.
        Assert.Equal(new[] { "SSAW0903" }, fromEdy.DisplayTargets);
        Assert.DoesNotContain("SSAW0907", fromEdy.DisplayTargets);

        // And nothing else in that method sends anything at all.
        var method = index.Symbol(
            SymbolKinds.Method,
            "Assets.ScenePosRegister.Payment.Presenter.Script.Payment_SSAW0089_Presenter.OnClickBtnEdy()");

        var fromMethod = sends.Where(send => send.FromMethodId == method.Id).ToList();
        Assert.Single(fromMethod);
    }

    /// <summary>
    /// E-18: a receiver is a method implementing the socket interface on a type that registered
    /// itself, and the literals it branches on are what pair it with a send (02 §9.7 step 2).
    /// </summary>
    [Fact]
    [Trait("Task", "T15")]
    public void Tcp_Handlers()
    {
        var index = DomainIndex.Editor;
        var handlers = Handlers(index);

        foreach (var handler in handlers)
            _output.WriteLine($"{handler.Terminal}/{handler.ScreenId}: {string.Join(", ", handler.Literals.Select(l => l.Literal))}");

        Assert.Equal(4, handlers.Count);

        // Each handler belongs to the screen whose presenter it is (02 §9.7 step 2).
        Assert.Equal(
            new[] { "SSAW0089", "SSAW0281", "SSAW0903", "SSAW0907" },
            handlers.Select(handler => handler.ScreenId).OrderBy(id => id, StringComparer.Ordinal));

        var customer0907 = handlers.Single(handler => handler.ScreenId == "SSAW0907");
        Assert.Equal("customer", customer0907.Terminal);

        // Contains and Equals are different tests and both are kept (03 §10.5).
        Assert.Equal(
            new[] { ("contains", "ToSSAW0907"), ("equals", "SSAW0903.To.SSAW0907") },
            customer0907.Literals
                .Select(literal => (literal.Op, literal.Literal))
                .OrderBy(literal => literal.Op, StringComparer.Ordinal)
                .ToArray());

        // The employee-side payment screen answers two paths.
        var payment = handlers.Single(handler => handler.ScreenId == "SSAW0089");
        Assert.Equal("employee", payment.Terminal);
        Assert.Equal(2, payment.Literals.Count);

        // Every handler method really implements the interface method.
        foreach (var handler in handlers)
        {
            var method = index.Reader.Symbol(handler.HandlerMethodId);
            Assert.NotNull(method);
            Assert.Equal("OnReceiveSocket", method!.Name);
        }

        // A send's key reaches the handler that tests for it, which is the pairing T18 will use.
        var sent = Sends(index).Single(send => send.Keys.Contains("SSAW0903.To.SSAW0907"));
        var constraints = customer0907.Literals
            .Select(literal => new EventPathConstraint
            {
                Tests = new[]
                {
                    new StringTest { Subject = UnknownValue.Of(), Op = literal.Op, Literal = literal.Literal }
                }
            })
            .ToList();

        Assert.Contains(constraints, constraint => EventPathMatcher.Match(sent.Keys, constraint).Matched);
    }

    /// <summary>
    /// 02 §10.3: the four polarity and mode combinations, and the partial match that makes a hop a
    /// candidate rather than a fact.
    /// </summary>
    [Fact]
    [Trait("Task", "T15")]
    public void EventPathMatcher_PolarityAndModes()
    {
        var keys = new[] { "ToSSAW0907", "ToSSAW0907.employee" };

        var contains = new StringTest { Subject = UnknownValue.Of(), Op = StringTestOps.Contains, Literal = "ToSSAW0907" };
        var employee = new StringTest { Subject = UnknownValue.Of(), Op = StringTestOps.Contains, Literal = "employee" };

        // true/all: a key must satisfy every test.
        var all = EventPathMatcher.Match(keys, new EventPathConstraint
        {
            Polarity = true,
            Mode = ConditionModes.All,
            Tests = new[] { contains, employee }
        });

        Assert.True(all.Matched);
        Assert.False(all.AllKeys);
        Assert.Equal(new[] { "ToSSAW0907.employee" }, all.MatchingKeys);

        // true/any: one test is enough, and here every key passes.
        var any = EventPathMatcher.Match(keys, new EventPathConstraint
        {
            Polarity = true,
            Mode = ConditionModes.Any,
            Tests = new[] { contains, employee }
        });

        Assert.True(any.Matched);
        Assert.True(any.AllKeys);

        // false/all: the key must fail to satisfy all of them.
        var notAll = EventPathMatcher.Match(keys, new EventPathConstraint
        {
            Polarity = false,
            Mode = ConditionModes.All,
            Tests = new[] { contains, employee }
        });

        Assert.Equal(new[] { "ToSSAW0907" }, notAll.MatchingKeys);

        // false/any: the key must satisfy none of them.
        var none = EventPathMatcher.Match(keys, new EventPathConstraint
        {
            Polarity = false,
            Mode = ConditionModes.Any,
            Tests = new[] { employee }
        });

        Assert.Equal(new[] { "ToSSAW0907" }, none.MatchingKeys);

        // Matching is case sensitive: these are protocol strings (02 §10.3).
        Assert.False(EventPathMatcher.Satisfies(
            "tossaw0907",
            new StringTest { Subject = UnknownValue.Of(), Op = StringTestOps.EqualsOp, Literal = "ToSSAW0907" }));

        // A callsite with no event path constraint is always processed.
        var unconstrained = EventPathMatcher.Match(keys, Array.Empty<EventPathConstraint>());
        Assert.True(unconstrained.Matched);
        Assert.True(unconstrained.AllKeys);

        // With no keys there is nothing to match, which is not the same as matching.
        Assert.False(EventPathMatcher.Match(Array.Empty<string>(), new EventPathConstraint
        {
            Tests = new[] { contains }
        }).Matched);
    }

    // ------------------------------------------------------------------ reading

    private sealed record SendEdge(
        string CallsiteId,
        string? FromMethodId,
        IReadOnlyList<string> Keys,
        bool KeysComplete,
        string? EventId,
        string? EventIdKind,
        IReadOnlyList<string> DisplayTargets,
        string? DisplayMode,
        IReadOnlyList<string> EffectiveSites);

    private static IReadOnlyList<SendEdge> Sends(DomainIndex index)
        => index.Reader.DomainEdges(TcpSendAdapter.Kind)
            .Select(edge =>
            {
                var json = JsonNode.Parse(edge.Json)!;

                return new SendEdge(
                    edge.CallsiteId!,
                    edge.FromMethodId,
                    Strings(json["keys"]),
                    json["keysComplete"]!.GetValue<bool>(),
                    json["eventId"]?.GetValue<string>(),
                    json["eventIdKind"]?.GetValue<string>(),
                    Strings(json["displayTargets"]),
                    json["displayMode"]?.GetValue<string>(),
                    Strings(json["effectiveSites"]));
            })
            .ToList();

    private sealed record HandlerRow(
        string? Terminal,
        string? ScreenId,
        string HandlerMethodId,
        IReadOnlyList<(string Op, string Literal)> Literals);

    private static IReadOnlyList<HandlerRow> Handlers(DomainIndex index)
    {
        using var command = index.Db.Command(
            "SELECT terminal, screen_id, handler_method_id, key_literals_json FROM message_handlers ORDER BY id;");

        var rows = new List<HandlerRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var literals = new List<(string, string)>();
            var json = reader.IsDBNull(3) ? null : JsonNode.Parse(reader.GetString(3));

            if (json is JsonArray array)
            {
                foreach (var entry in array)
                    literals.Add((entry!["op"]!.GetValue<string>(), entry["literal"]!.GetValue<string>()));
            }

            rows.Add(new HandlerRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                literals));
        }

        return rows;
    }

    private static IReadOnlyList<string> Strings(JsonNode? node)
        => node is JsonArray array
            ? array.Select(item => item!.GetValue<string>()).ToList()
            : Array.Empty<string>();
}
