using System.Text.Json;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Pipeline;
using ScreenFlow.Core.Resolution;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Anchors;
using ScreenFlow.UnityPos.Resources;

namespace ScreenFlow.UnityPos;

/// <summary>
/// What an adapter is given (02 §9): the index to read, the domain tables to write, the anchors to
/// address POS source by, the resolver, and a place to put diagnostics.
/// <para>
/// An adapter that cannot resolve its anchor disables its feature through <see cref="Disable"/>
/// instead of guessing, and the build report then says which questions the index cannot answer
/// (02 §4.2).
/// </para>
/// </summary>
public sealed class DomainContext
{
    private readonly DomainStageContext _stage;

    public DomainContext(DomainStageContext stage, DomainWriter writer, AnchorResolver anchors)
    {
        _stage = stage;
        Writer = writer;
        Anchors = anchors;
        Fields = new FieldPointsTo(stage.Reader, stage.Resolution);
    }

    public IndexReader Reader => _stage.Reader;

    public DomainConfig Domain => _stage.Domain;

    public ProfileConfig Profile => _stage.Profile;

    public string ProjectRoot => _stage.ProjectRoot;

    public ValueResolver Values => _stage.Values;

    public Dispatcher Dispatcher => _stage.Dispatcher;

    public ResolutionIndex Resolution => _stage.Resolution;

    public FieldPointsTo Fields { get; }

    public DomainWriter Writer { get; }

    public AnchorResolver Anchors { get; }

    /// <summary>
    /// What the resource readers loaded (02 §9.9), set by the P6 adapter and read by the ones after
    /// it. Null when the resources adapter did not run, which is not the same as "no resources".
    /// </summary>
    public ResourceCatalog? Resources { get; set; }

    public CancellationToken Ct => _stage.Ct;

    /// <summary>Providers registered so far, including the ones earlier adapters added.</summary>
    public IReadOnlyList<IValueProvider> Providers => _stage.Providers;

    /// <summary>Reads one section of <c>unitypos.json</c> into its typed shape (02 §4.2).</summary>
    public T Section<T>(JsonElement element, string name)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidOperationException($"unitypos.json has no '{name}' section.");

        return element.Deserialize<T>(ConfigLoader.SectionOptions)
               ?? throw new InvalidOperationException($"the '{name}' section of unitypos.json is empty.");
    }

    public void Diagnose(DiagRec diagnostic) => _stage.Diagnostics.Add(diagnostic);

    /// <summary>
    /// Records that a feature is off. Listed in <c>meta.disabledFeatures</c> and in the build
    /// report, so a caller can tell "nothing found" from "not analysed" (02 §4.2).
    /// </summary>
    public void Disable(string feature)
    {
        if (!_stage.DisabledFeatures.Contains(feature, StringComparer.Ordinal))
            _stage.DisabledFeatures.Add(feature);
    }

    public bool IsDisabled(string feature) => _stage.DisabledFeatures.Contains(feature, StringComparer.Ordinal);

    public void Register(IValueProvider provider)
    {
        if (!_stage.Providers.Contains(provider)) _stage.Providers.Add(provider);
    }

    /// <summary>Notes a file an adapter read, which the staleness fingerprint covers (02 §7.6).</summary>
    public void NoteReadFile(string path) => _stage.ReadFiles.Add(path);

    /// <summary>
    /// A resolve context carrying every provider registered so far. Built fresh per use because the
    /// cycle-guard set inside it belongs to one resolution, not to the whole adapter run.
    /// </summary>
    public ResolveContext Resolve(string? currentTypeId = null)
        => ResolveContext.FromConfig(Domain.Traversal, Providers.ToList(), currentTypeId);

    /// <summary>
    /// Reports an anchor that did not resolve and disables the features that depend on it. Returns
    /// false so the caller can stop in one line.
    /// </summary>
    public bool AnchorFailed(AnchorResolution resolution, string adapterId, params string[] features)
    {
        if (resolution.Resolved) return false;

        Diagnose(DiagRec.Error(
            resolution.ErrorCode!,
            $"[{adapterId}] {resolution.Message}"));

        foreach (var feature in features.Length == 0 ? new[] { adapterId } : features) Disable(feature);
        return true;
    }

    /// <summary>
    /// The evidence id for a span, or null when the index holds no evidence row for it. Never
    /// invented: a dangling evidence id would make a report point at nothing.
    /// </summary>
    public string? Evidence(string? file, Span? span)
    {
        if (file is null || span is null) return null;

        var id = Ids.EvidenceId(file, span);
        return Reader.Evidence(id) is null ? null : id;
    }

    public string? EvidenceOf(SymbolRec symbol) => Evidence(symbol.File, symbol.Span);

    /// <summary>The evidence id of a callsite, whose file comes from the method that contains it.</summary>
    public string? EvidenceOf(CallsiteRec callsite)
        => Evidence(Reader.Symbol(callsite.CallerId)?.File, callsite.Span);
}
