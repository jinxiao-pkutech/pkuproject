namespace ScreenFlow.Core.Storage;

// Row shapes for the domain and result tables (02 §7.3). The adapters in ScreenFlow.UnityPos and
// the graph layer in the core both write through DomainWriter, so these are the only definition of
// what a domain row contains.

public sealed record ScreenRow
{
    public required string Terminal { get; init; }
    public required string ScreenId { get; init; }
    public string? Name { get; init; }
    public bool? IsPopup { get; init; }
    public string? ViewTypeId { get; init; }
    public string? PresenterTypeId { get; init; }

    /// <summary>
    /// False for a screen declared in the screen id table but absent from every registry. Such a
    /// screen is kept with a break rather than dropped (01 anti-fabrication rules).
    /// </summary>
    public required bool Registered { get; init; }

    /// <summary>The <c>ScreenID</c> member this screen was declared by.</summary>
    public string? ScreenFieldId { get; init; }

    public string? EvidenceId { get; init; }
}

public sealed record EnumMemberRow
{
    public required string TypeId { get; init; }
    public required string MemberId { get; init; }
    public required string Name { get; init; }
    public int? ValueId { get; init; }
    public string? ValueName { get; init; }
}

public sealed record ControlRow
{
    public required string Terminal { get; init; }
    public required string ScreenId { get; init; }
    public required string ControlId { get; init; }
    public string? FieldId { get; init; }
    public string? ControlType { get; init; }
    public string? UxmlPath { get; init; }
    public string? HierarchyPath { get; init; }
    public string? TextKey { get; init; }

    /// <summary>Where the text key came from, e.g. the initialize JSON or the UXML.</summary>
    public string? TextKeySource { get; init; }

    public string? TextKeyConfidence { get; init; }
    public string? EvidenceId { get; init; }
}

public sealed record ControlHandlerRow
{
    public required string Terminal { get; init; }
    public required string ScreenId { get; init; }
    public required string ControlId { get; init; }
    public required string EventName { get; init; }
    public required string HandlerMethodId { get; init; }
    public string? Confidence { get; init; }
    public string? EvidenceId { get; init; }
}

public sealed record TextRow
{
    public required string Collection { get; init; }
    public required string Key { get; init; }
    public required string Locale { get; init; }
    public string? TextRaw { get; init; }

    /// <summary>Rich-text markup stripped, so a comparison against a screen's text matches.</summary>
    public string? TextPlain { get; init; }

    public string? File { get; init; }
}

public sealed record MessageRow
{
    public required int Code { get; init; }
    public required string Locale { get; init; }
    public string? TextRaw { get; init; }
    public string? TextPlain { get; init; }
}

public sealed record InputConstraintRow
{
    public required string Id { get; init; }
    public required string Terminal { get; init; }
    public required string ScreenId { get; init; }
    public string? MethodId { get; init; }
    public string? ControlFieldId { get; init; }
    public required string Kind { get; init; }

    /// <summary>Null when the limit is not a compile-time constant; never a plausible substitute.</summary>
    public string? Value { get; init; }

    public string? ErrorCodeName { get; init; }
    public int? ErrorCodeValue { get; init; }
    public string? ErrorConfidence { get; init; }
    public string? ConditionsJson { get; init; }
    public string? EvidenceId { get; init; }
}

public sealed record KeySeqTransitionRow
{
    public required string Id { get; init; }
    public required string Stage { get; init; }
    public required string PsCode { get; init; }
    public string? HandlerMethodId { get; init; }
    public string? NextStage { get; init; }
    public string? SubTable { get; init; }
    public string? EvidenceId { get; init; }
}

public sealed record DiRegistrationRow
{
    public required string Id { get; init; }
    public required string ServiceTypeId { get; init; }
    public string? ImplTypeId { get; init; }
    public string? Lifetime { get; init; }

    /// <summary>How the registration was found, e.g. a direct call or a factory.</summary>
    public string? Via { get; init; }

    public string? EvidenceId { get; init; }
}

public sealed record DomainEdgeRow
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? CallsiteId { get; init; }
    public string? FromMethodId { get; init; }
    public required string Json { get; init; }
}

public sealed record MessageHandlerRow
{
    public required string Id { get; init; }
    public required string Channel { get; init; }
    public string? Terminal { get; init; }
    public string? ScreenId { get; init; }
    public required string HandlerMethodId { get; init; }

    /// <summary>The literals this handler compares against, which is how a sender is paired to it.</summary>
    public string? KeyLiteralsJson { get; init; }

    public string? EvidenceId { get; init; }
}

public sealed record EntryRow
{
    public required string Id { get; init; }
    public required string Terminal { get; init; }
    public required string ScreenId { get; init; }
    public required string Trigger { get; init; }
    public string? ControlId { get; init; }
    public required string MethodId { get; init; }
    public string? EvidenceId { get; init; }
}

public sealed record BoundaryRow
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public string? ContractKind { get; init; }
    public string? ContractKey { get; init; }
    public required string Json { get; init; }
}

public sealed record BreakRow
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Reason { get; init; }
    public string? AtCallsiteId { get; init; }
    public string? AtMethodId { get; init; }
    public string? ContractKey { get; init; }

    /// <summary>single, multiple or none (02 §8.3).</summary>
    public string? ResumeConfidence { get; init; }

    public required string Json { get; init; }
}

public sealed record ScreenEdgeRow
{
    public required string Id { get; init; }
    public required string FromTerminal { get; init; }
    public required string FromScreen { get; init; }

    /// <summary>Null for a dangling edge: one that ends at a break with no resume point.</summary>
    public string? ToTerminal { get; init; }

    public string? ToScreen { get; init; }

    public required string Trigger { get; init; }
    public string? ControlId { get; init; }
    public required string Confidence { get; init; }
    public required double Cost { get; init; }

    /// <summary>The break this edge ends at, when it does not reach a screen.</summary>
    public string? EndsAtBreak { get; init; }

    public required string Json { get; init; }
}
