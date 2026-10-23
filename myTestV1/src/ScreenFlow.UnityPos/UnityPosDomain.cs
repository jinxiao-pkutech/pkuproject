using ScreenFlow.Core.Extraction;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Pipeline;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Anchors;
using ScreenFlow.UnityPos.Resources;
using ScreenFlow.UnityPos.Screens;

namespace ScreenFlow.UnityPos;

/// <summary>
/// The POS domain stage (pipeline phase P7, 02 §2).
/// <para>
/// Runs the adapters in the fixed order of 02 §2 and nothing else. The order matters because each
/// adapter reads what the previous ones wrote and registers the value providers the next ones need:
/// navigation cannot name a screen until the screens adapter has registered the screen id provider.
/// </para>
/// </summary>
public sealed class UnityPosDomain : IDomainStage
{
    /// <summary>Phase of the domain adapters (02 §2).</summary>
    public const string AdapterPhase = "P7";

    /// <summary>Raised when one adapter throws: the build fails rather than storing half a domain.</summary>
    public const string AdapterFailedCode = "E_ADAPTER_FAILED";

    private readonly IReadOnlyList<IDomainAdapter> _adapters;

    public UnityPosDomain(IEnumerable<IDomainAdapter>? adapters = null)
        => _adapters = AdapterOrder.Sort(adapters ?? Default());

    /// <summary>The adapters implemented so far, in any order; the stage sorts them (02 §2).</summary>
    public static IReadOnlyList<IDomainAdapter> Default() => new IDomainAdapter[]
    {
        new ResourcesAdapter(),
        new ScreensAdapter(),
        new EnumerationsAdapter(),
        new Di.DiAdapter(),
        new Ui.ControlsAdapter(),
        new KeySeq.KeySeqAdapter(),
        new NavigationAdapter(),
        new Tcp.TcpSendAdapter(),
        new Tcp.TcpReceiverAdapter(),
        new Boundaries.DeviceAdapter(),
        new Boundaries.ApiAdapter(),
        new Boundaries.NativeAdapter(),
        new Boundaries.SqlAdapter(),
        new Boundaries.QueueAdapter(),
        new Boundaries.ReflectionAdapter(),
        new Entries.EntriesAdapter()
    };

    public string Phase => AdapterPhase;

    public string Name => UnityPosDomainInfo.DomainId;

    public IReadOnlyList<IDomainAdapter> Adapters => _adapters;

    public void Run(DomainStageContext stage)
    {
        using var writer = new DomainWriter(stage.Db);
        var anchors = new AnchorResolver(stage.Reader);
        var context = new DomainContext(stage, writer, anchors);

        for (var i = 0; i < _adapters.Count; i++)
        {
            stage.Ct.ThrowIfCancellationRequested();
            var adapter = _adapters[i];

            stage.Progress.Report(new ProgressEvent(adapter.Phase, adapter.Id, i, _adapters.Count));

            // The phase an adapter belongs to is reported as run, so the build report lists P6 as
            // well as P7 rather than only the stage's own phase (02 §2).
            if (!stage.RanPhases.Contains(adapter.Phase)) stage.RanPhases.Add(adapter.Phase);

            adapter.Run(context);

            // Flushed per adapter, not once at the end: the adapters talk to each other through the
            // domain tables (02 §9), and ui-bindings reads the screens the screens adapter wrote.
            writer.Flush();
        }

        stage.Diagnostics.Add(DiagRec.Info(
            "I_ADAPTERS_RUN",
            $"[{UnityPosDomainInfo.DomainId}] Adapters run: {string.Join(", ", _adapters.Select(a => a.Id))}."));
    }
}
