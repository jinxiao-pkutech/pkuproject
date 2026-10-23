namespace ScreenFlow.UnityPos;

/// <summary>
/// One POS domain adapter (02 §9).
/// <para>
/// Each adapter turns the language-level facts of the index into one kind of domain fact — screens,
/// enumerations, navigation, TCP — and nothing else. They run in the fixed order of 02 §2, because
/// a later adapter reads what an earlier one wrote.
/// </para>
/// </summary>
public interface IDomainAdapter
{
    /// <summary>Adapter id, as 02 §2 names it in the fixed order.</summary>
    string Id { get; }

    /// <summary>
    /// Pipeline phase this adapter belongs to. The domain adapters are P7; the resource readers are
    /// P6, because every adapter after them reads what they loaded (02 §2).
    /// </summary>
    string Phase => UnityPosDomain.AdapterPhase;

    void Run(DomainContext context);
}

/// <summary>The adapter order of 02 §2. An adapter not listed here would run nowhere.</summary>
public static class AdapterOrder
{
    /// <summary>Not one of the 14 adapters: the P6 resource readers, which run before all of them.</summary>
    public const string Resources = "resources";

    public const string Screens = "screens";
    public const string Enumerations = "enumerations";
    public const string UiBindings = "ui-bindings";
    public const string Di = "di";
    public const string KeySeq = "keyseq";
    public const string Navigation = "navigation";
    public const string Tcp = "tcp";
    public const string Device = "device";
    public const string Api = "api";
    public const string Native = "native";
    public const string Sql = "sql";
    public const string Queue = "queue";
    public const string Reflection = "reflection";
    public const string Entries = "entries";

    /// <summary>
    /// The adapter order of 02 §2, with the P6 resource readers at the head: they are a phase of
    /// their own, and everything after them depends on what they loaded.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Resources,
        Screens, Enumerations, UiBindings, Di, KeySeq, Navigation, Tcp,
        Device, Api, Native, Sql, Queue, Reflection, Entries
    };

    /// <summary>
    /// Sorts adapters into the fixed order. Registration order is then free, which keeps each task
    /// from having to touch the order list in the right place.
    /// </summary>
    public static IReadOnlyList<IDomainAdapter> Sort(IEnumerable<IDomainAdapter> adapters)
        => adapters
            .OrderBy(adapter => IndexOf(adapter.Id))
            .ThenBy(adapter => adapter.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Where an adapter sits in the fixed order. An id may name a sub-step with <c>:</c>, as the
    /// TCP receivers do: they run inside the tcp slot, after the sends they are paired with.
    /// </summary>
    public static int IndexOf(string id)
    {
        var slot = id.Split(':')[0];
        var index = All.ToList().IndexOf(slot);
        return index < 0 ? int.MaxValue : index;
    }
}
