using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Graph;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.Core.Query;

/// <summary>A symbol reduced to what a caller can act on (02 §11.2).</summary>
public sealed record SymbolSummary
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("fqName")] public required string FqName { get; init; }
    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("span")] public Span? Span { get; init; }
}

/// <summary><c>screen get</c> (02 §11.2).</summary>
public sealed record ScreenResult
{
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("screenId")] public required string ScreenId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("isPopup")] public bool? IsPopup { get; init; }
    [JsonPropertyName("registered")] public required bool Registered { get; init; }
    [JsonPropertyName("view")] public SymbolSummary? View { get; init; }
    [JsonPropertyName("presenter")] public SymbolSummary? Presenter { get; init; }
    [JsonPropertyName("screenFieldId")] public string? ScreenFieldId { get; init; }
    [JsonPropertyName("evidenceId")] public string? EvidenceId { get; init; }
}

public sealed record ControlText
{
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("collection")] public required string Collection { get; init; }
    [JsonPropertyName("locale")] public required string Locale { get; init; }
    [JsonPropertyName("raw")] public string? Raw { get; init; }
    [JsonPropertyName("plain")] public string? Plain { get; init; }
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }
}

public sealed record ControlHandler
{
    [JsonPropertyName("eventName")] public required string EventName { get; init; }
    [JsonPropertyName("handler")] public SymbolSummary? Handler { get; init; }
    [JsonPropertyName("confidence")] public string? Confidence { get; init; }
    [JsonPropertyName("evidenceId")] public string? EvidenceId { get; init; }
}

public sealed record ControlResult
{
    [JsonPropertyName("controlId")] public required string ControlId { get; init; }
    [JsonPropertyName("fieldId")] public string? FieldId { get; init; }
    [JsonPropertyName("controlType")] public string? ControlType { get; init; }
    [JsonPropertyName("uxmlPath")] public string? UxmlPath { get; init; }
    [JsonPropertyName("hierarchyPath")] public string? HierarchyPath { get; init; }
    [JsonPropertyName("textKey")] public string? TextKey { get; init; }
    [JsonPropertyName("textKeySource")] public string? TextKeySource { get; init; }
    [JsonPropertyName("textKeyConfidence")] public string? TextKeyConfidence { get; init; }
    [JsonPropertyName("text")] public ControlText? Text { get; init; }
    [JsonPropertyName("handlers")] public IReadOnlyList<ControlHandler> Handlers { get; init; } = Array.Empty<ControlHandler>();
    [JsonPropertyName("evidenceId")] public string? EvidenceId { get; init; }
}

public sealed record ScreenControlsResult
{
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("screenId")] public required string ScreenId { get; init; }
    [JsonPropertyName("controls")] public IReadOnlyList<ControlResult> Controls { get; init; } = Array.Empty<ControlResult>();
}

public sealed record ConstraintMessage
{
    [JsonPropertyName("code")] public required int Code { get; init; }
    [JsonPropertyName("locale")] public required string Locale { get; init; }
    [JsonPropertyName("raw")] public string? Raw { get; init; }
    [JsonPropertyName("plain")] public string? Plain { get; init; }
}

public sealed record ConstraintResult
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("errorCodeName")] public string? ErrorCodeName { get; init; }
    [JsonPropertyName("errorMessage")] public ConstraintMessage? ErrorMessage { get; init; }
    [JsonPropertyName("errorConfidence")] public string? ErrorConfidence { get; init; }
    [JsonPropertyName("conditions")] public IReadOnlyList<JsonNode> Conditions { get; init; } = Array.Empty<JsonNode>();
    [JsonPropertyName("method")] public SymbolSummary? Method { get; init; }
    [JsonPropertyName("controlFieldId")] public string? ControlFieldId { get; init; }
    [JsonPropertyName("evidenceId")] public string? EvidenceId { get; init; }
}

public sealed record ScreenConstraintsResult
{
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("screenId")] public required string ScreenId { get; init; }
    [JsonPropertyName("constraints")] public IReadOnlyList<ConstraintResult> Constraints { get; init; } = Array.Empty<ConstraintResult>();
}

public sealed record ScreenTransition
{
    [JsonPropertyName("edgeId")] public required string EdgeId { get; init; }
    [JsonPropertyName("from")] public required ScreenRef From { get; init; }
    [JsonPropertyName("to")] public ScreenRef? To { get; init; }
    [JsonPropertyName("trigger")] public required EdgeTrigger Trigger { get; init; }
    [JsonPropertyName("confidence")] public required string Confidence { get; init; }
    [JsonPropertyName("cost")] public required double Cost { get; init; }
    [JsonPropertyName("endsAtBreak")] public string? EndsAtBreak { get; init; }
}

public sealed record ScreenTransitionsResult
{
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("screenId")] public required string ScreenId { get; init; }
    [JsonPropertyName("direction")] public required string Direction { get; init; }
    [JsonPropertyName("edges")] public IReadOnlyList<ScreenTransition> Edges { get; init; } = Array.Empty<ScreenTransition>();
}

/// <summary>
/// The <c>screen get/controls/constraints/transitions</c> commands (02 §11.2).
/// <para>
/// A screen id can exist on more than one terminal, so every method returns one result per
/// matching terminal and the CLI chooses the array-versus-object shape (Q-17).
/// </para>
/// </summary>
public sealed class ScreenQuery
{
    private readonly IndexReader _reader;
    private readonly DomainConfig _domain;
    private readonly string _defaultLocale;

    public ScreenQuery(IndexReader reader, DomainConfig domain)
    {
        _reader = reader;
        _domain = domain;
        _defaultLocale = DefaultLocale(domain);
    }

    public IReadOnlyList<ScreenResult> Screens(string screenId, string? terminal)
        => _reader.Screens(screenId, terminal)
            .Select(row => new ScreenResult
            {
                Terminal = row.Terminal,
                ScreenId = row.ScreenId,
                Name = row.Name,
                IsPopup = row.IsPopup,
                Registered = row.Registered,
                View = Summary(row.ViewTypeId),
                Presenter = Summary(row.PresenterTypeId),
                ScreenFieldId = row.ScreenFieldId,
                EvidenceId = row.EvidenceId
            })
            .ToList();

    public IReadOnlyList<ScreenControlsResult> Controls(string screenId, string? terminal, string? locale)
    {
        var effectiveLocale = locale ?? _defaultLocale;

        return _reader.Screens(screenId, terminal)
            .Select(row => new ScreenControlsResult
            {
                Terminal = row.Terminal,
                ScreenId = row.ScreenId,
                Controls = _reader.Controls(row.Terminal, row.ScreenId)
                    .Select(control => BuildControl(control, row.Terminal, effectiveLocale))
                    .ToList()
            })
            .ToList();
    }

    public IReadOnlyList<ScreenConstraintsResult> Constraints(string screenId, string? terminal, string? locale)
    {
        var effectiveLocale = locale ?? _defaultLocale;

        return _reader.Screens(screenId, terminal)
            .Select(row => new ScreenConstraintsResult
            {
                Terminal = row.Terminal,
                ScreenId = row.ScreenId,
                Constraints = _reader.InputConstraints(screenId, row.Terminal)
                    .Select(constraint => new ConstraintResult
                    {
                        Id = constraint.Id,
                        Kind = constraint.Kind,
                        Value = constraint.Value,
                        ErrorCodeName = constraint.ErrorCodeName,
                        ErrorMessage = constraint.ErrorCodeValue is { } code
                            ? Message(code, effectiveLocale)
                            : null,
                        ErrorConfidence = constraint.ErrorConfidence,
                        Conditions = ParseArray(constraint.ConditionsJson),
                        Method = Summary(constraint.MethodId),
                        ControlFieldId = constraint.ControlFieldId,
                        EvidenceId = constraint.EvidenceId
                    })
                    .ToList()
            })
            .ToList();
    }

    public IReadOnlyList<ScreenTransitionsResult> Transitions(string screenId, string? terminal, string direction, int limit)
    {
        return _reader.Screens(screenId, terminal)
            .Select(row =>
            {
                var rows = direction == "in"
                    ? _reader.ScreenEdgesTo(row.Terminal, row.ScreenId)
                    : _reader.ScreenEdgesFrom(row.Terminal, row.ScreenId);

                return new ScreenTransitionsResult
                {
                    Terminal = row.Terminal,
                    ScreenId = row.ScreenId,
                    Direction = direction,
                    Edges = rows.Take(limit)
                        .Select(edge => new ScreenTransition
                        {
                            EdgeId = edge.Id,
                            From = new ScreenRef { Terminal = edge.FromTerminal, ScreenId = edge.FromScreen },
                            To = edge.ToTerminal is null || edge.ToScreen is null
                                ? null
                                : new ScreenRef { Terminal = edge.ToTerminal, ScreenId = edge.ToScreen },
                            Trigger = new EdgeTrigger { Kind = edge.Trigger, ControlId = edge.ControlId },
                            Confidence = edge.Confidence,
                            Cost = edge.Cost,
                            EndsAtBreak = edge.EndsAtBreak
                        })
                        .ToList()
                };
            })
            .ToList();
    }

    // ---------------------------------------------------------------- helpers

    private ControlResult BuildControl(ControlRow control, string terminal, string locale)
        => new()
        {
            ControlId = control.ControlId,
            FieldId = control.FieldId,
            ControlType = control.ControlType,
            UxmlPath = control.UxmlPath,
            HierarchyPath = control.HierarchyPath,
            TextKey = control.TextKey,
            TextKeySource = control.TextKeySource,
            TextKeyConfidence = control.TextKeyConfidence,
            Text = control.TextKey is { Length: > 0 } key ? ControlText(key, terminal, locale, control.TextKeyConfidence) : null,
            Handlers = _reader.ControlHandlers(terminal, control.ScreenId)
                .Where(handler => string.Equals(handler.ControlId, control.ControlId, StringComparison.Ordinal))
                .Select(handler => new ControlHandler
                {
                    EventName = handler.EventName,
                    Handler = Summary(handler.HandlerMethodId),
                    Confidence = handler.Confidence,
                    EvidenceId = handler.EvidenceId
                })
                .ToList(),
            EvidenceId = control.EvidenceId
        };

    private ControlText? ControlText(string key, string terminal, string locale, string? confidence)
    {
        foreach (var collection in SearchOrder(terminal))
        {
            var text = _reader.Text(collection, key, locale);
            if (text is null) continue;

            return new ControlText
            {
                Key = key,
                Collection = collection,
                Locale = locale,
                Raw = text.TextRaw,
                Plain = text.TextPlain,
                Confidence = confidence ?? Confidences.Certain
            };
        }

        return null;
    }

    private ConstraintMessage? Message(int code, string locale)
    {
        var row = _reader.Message(code, locale);
        return row is null
            ? null
            : new ConstraintMessage
            {
                Code = row.Code,
                Locale = row.Locale,
                Raw = row.TextRaw,
                Plain = row.TextPlain
            };
    }

    private SymbolSummary? Summary(string? symbolId)
        => symbolId is null ? null : SymbolQuery.Summary(_reader.Symbol(symbolId));

    private static IReadOnlyList<JsonNode> ParseArray(string? json)
    {
        if (json is null) return Array.Empty<JsonNode>();
        if (JsonNode.Parse(json) is not JsonArray array) return Array.Empty<JsonNode>();

        var result = new List<JsonNode>();
        foreach (var item in array)
            if (item is not null) result.Add(item);
        return result;
    }

    private IReadOnlyList<string> SearchOrder(string terminal)
    {
        if (_domain.Resources.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!_domain.Resources.TryGetProperty("stringTableSearchOrder", out var orders)) return Array.Empty<string>();
        if (!orders.TryGetProperty(terminal, out var list)) return Array.Empty<string>();

        return list.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();
    }

    private static string DefaultLocale(DomainConfig domain)
    {
        if (domain.Resources.ValueKind == JsonValueKind.Object
            && domain.Resources.TryGetProperty("defaultLocale", out var value)
            && value.GetString() is { Length: > 0 } locale)
        {
            return locale;
        }

        return "ja";
    }
}
