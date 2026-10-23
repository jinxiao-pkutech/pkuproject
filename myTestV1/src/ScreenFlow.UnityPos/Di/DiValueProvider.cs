using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Resolution;
using ScreenFlow.Core.Storage;

namespace ScreenFlow.UnityPos.Di;

/// <summary>
/// Resolves <c>GetService&lt;T&gt;()</c> to the registered implementations (02 §8.2, §9.4).
/// <para>
/// With one registration the call has a known target and the chain continues. With two — which the
/// POS really has for <c>IDeviceBrokerMessageEvent</c> (03 §12) — the answer is both of them and
/// the resolution is ambiguous: at run time the order of registration decides, and that is not in
/// the source.
/// </para>
/// </summary>
public sealed class DiValueProvider : IValueProvider
{
    public const string ProviderName = "di";

    private readonly DiSection _config;
    private readonly IndexReader _reader;
    private readonly Dictionary<string, List<DiRegistration>> _byService;

    public DiValueProvider(DiSection config, IReadOnlyList<DiRegistration> registrations, IndexReader reader)
    {
        _config = config;
        _reader = reader;

        _byService = registrations
            .GroupBy(registration => registration.ServiceTypeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        Registrations = registrations;
    }

    public string Name => ProviderName;

    public IReadOnlyList<DiRegistration> Registrations { get; }

    /// <summary>The implementation types registered for a service, in a stable order.</summary>
    public IReadOnlyList<string> ImplementationsOf(string serviceTypeId)
        => _byService.TryGetValue(serviceTypeId, out var found)
            ? found.Select(registration => registration.ImplTypeId)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList()
            : Array.Empty<string>();

    public ValueResolution? TryResolve(Value value, ResolveContext context)
    {
        if (value is not CallResultValue call || call.TargetId is null) return null;

        var target = _reader.Symbol(call.TargetId);
        if (target is null || !IsLocatorMethod(target)) return null;

        var isGet = _config.GetMethods.Contains(target.Name, StringComparer.Ordinal);
        var isGetAll = _config.GetAllMethods.Contains(target.Name, StringComparer.Ordinal);
        if (!isGet && !isGetAll) return null;

        var service = call.TypeArgs.FirstOrDefault()?.Id;
        if (service is null) return null;

        var impls = ImplementationsOf(service);

        if (impls.Count == 0)
        {
            // Nothing is registered for it: the call resolves to nothing, and saying so is the
            // answer (02 §9.4).
            return ValueResolution.Unresolved;
        }

        return new ValueResolution
        {
            Types = impls,

            // Several registrations for one service: complete as a set, but which one runs is a
            // run-time decision (02 §9.4, 03 §12).
            Complete = isGetAll || impls.Count == 1,
            Ambiguous = !isGetAll && impls.Count > 1
        };
    }

    /// <summary>
    /// Whether a method belongs to the locator type. Checked by container rather than by name, so
    /// another type's <c>GetService</c> is not mistaken for this one (CLAUDE.md §4).
    /// </summary>
    public bool IsLocatorMethod(SymbolRec method)
    {
        if (method.ContainerId is null) return false;

        var container = _reader.Symbol(method.ContainerId);
        return container is not null && LocatorTypeIds.Contains(container.Id);
    }

    /// <summary>The locator type ids, filled by the adapter once its anchor resolved.</summary>
    public HashSet<string> LocatorTypeIds { get; } = new(StringComparer.Ordinal);
}
