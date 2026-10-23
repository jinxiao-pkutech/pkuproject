using System.Text.Json.Nodes;
using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;
using ScreenFlow.UnityPos.Screens;

namespace ScreenFlow.UnityPos.Boundaries;

/// <summary>
/// Objects created or members reached by reflection (02 §9.8).
/// <para>
/// Reflection decides its target at run time, so the chain cannot continue through it. The
/// exception is the handful of places the domain adapters already understand — the screen factory
/// and the TCP presenter resolve a screen by reflection, and those are answered by 02 §9.1 and
/// §9.7 instead (<c>domainHandledReflectionMethods</c>). Reporting them again would be noise that
/// hides the one place nothing explains.
/// </para>
/// </summary>
public sealed class ReflectionAdapter : IDomainAdapter
{
    public const string Feature = "reflection";

    /// <summary>The reflection entry points of 02 §9.8.</summary>
    private static readonly (string Type, string Member)[] Targets =
    {
        ("System.Type", "GetConstructor"),
        ("System.Type", "GetField"),
        ("System.Type", "GetMethod"),
        ("System.Type", "GetProperty"),

        // Type.GetType is deliberately not here: 02 §9.8 lists the entry points, and adding one
        // would report the same reflection site twice.
        ("System.Reflection.ConstructorInfo", "Invoke"),
        ("System.Reflection.MethodInfo", "Invoke"),
        ("System.Activator", "CreateInstance")
    };

    public string Id => AdapterOrder.Reflection;

    public void Run(DomainContext context)
    {
        var excluded = DomainHandled(context);

        foreach (var method in NativeAdapter.SourceMethods(context))
        {
            if (excluded.Contains(method.Id)) continue;

            // A lambda inside an excluded method is excluded with it: it is the same code.
            if (method.ContainerId is not null && excluded.Contains(method.ContainerId)) continue;

            foreach (var call in context.Reader.CallsitesIn(method.Id)
                         .Where(call => call.Reachable)
                         .OrderBy(call => call.Id, StringComparer.Ordinal))
            {
                var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
                if (target?.ContainerId is null) continue;

                var container = context.Reader.Symbol(target.ContainerId);
                if (container is null) continue;

                if (!Targets.Any(entry =>
                        entry.Member == target.Name
                        && (container.FqName == entry.Type
                            || context.Resolution.Supertypes(container.Id)
                                .Select(id => context.Reader.Symbol(id)?.FqName)
                                .Contains(entry.Type))))
                {
                    continue;
                }

                BoundaryWriter.Write(
                    context,
                    BoundaryCategories.DiAmbiguous,
                    ContractKinds.Service,
                    container.Name + "." + target.Name,
                    new JsonObject
                    {
                        ["reason"] = "ReflectionConstruction",
                        ["text"] = call.Args.FirstOrDefault()?.Value.Text ?? string.Empty,
                        ["method"] = method.FqName
                    },
                    call);
            }
        }
    }

    /// <summary>
    /// Methods whose reflection a domain adapter already explains (02 §4.2,
    /// <c>domainHandledReflectionMethods</c>). <c>member: "*"</c> covers the whole type.
    /// </summary>
    private static IReadOnlySet<string> DomainHandled(DomainContext context)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var screens = context.Section<ScreensSectionWithReflection>(context.Domain.Screens, "screens");

        foreach (var anchor in screens.DomainHandledReflectionMethods)
        {
            var resolution = context.Anchors.Resolve(anchor);

            if (!resolution.Resolved)
            {
                context.Diagnose(DiagRec.Warning(
                    resolution.ErrorCode!,
                    $"[{Feature}] {resolution.Message}"));

                continue;
            }

            foreach (var symbol in resolution.Symbols) excluded.Add(symbol.Id);
        }

        return excluded;
    }

    /// <summary>
    /// The one part of the screens section this adapter needs. Declared here rather than reused
    /// from the screens adapter so each adapter owns the shape it reads (02 §9).
    /// </summary>
    private sealed record ScreensSectionWithReflection
    {
        [System.Text.Json.Serialization.JsonPropertyName("domainHandledReflectionMethods")]
        public IReadOnlyList<AnchorSpec> DomainHandledReflectionMethods { get; init; } = Array.Empty<AnchorSpec>();
    }
}
