using System.Text.Json.Nodes;
using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.UnityPos.Boundaries;
using ScreenFlow.UnityPos.Ui;

namespace ScreenFlow.UnityPos.Entries;

/// <summary>How a screen's code is entered (02 §9.10).</summary>
public static class EntryTriggers
{
    /// <summary>A control was pressed.</summary>
    public const string Click = "click";

    /// <summary>The screen was displayed.</summary>
    public const string Auto = "auto";

    /// <summary>A scanner delivered input.</summary>
    public const string Scan = "scan";

    public static readonly IReadOnlyList<string> All = new[] { Click, Auto, Scan };
}

/// <summary>
/// The entry points of each registered screen (02 §9.10).
/// <para>
/// A path search starts at one of these: a click, the screen being shown, or a scan. The scan case
/// also records a hardware boundary — the input comes from a device outside the project, so the
/// chain starts there rather than being called from anywhere (03 §17).
/// </para>
/// </summary>
public sealed class EntriesAdapter : IDomainAdapter
{
    public const string Feature = "entries";
    public const string EntryPrefix = "en_";

    public string Id => AdapterOrder.Entries;

    public void Run(DomainContext context)
    {
        var config = context.Section<UiSection>(context.Domain.Ui, "ui");

        foreach (var screen in context.Reader.Screens()
                     .Where(screen => screen.Registered)
                     .OrderBy(screen => screen.Terminal, StringComparer.Ordinal)
                     .ThenBy(screen => screen.ScreenId, StringComparer.Ordinal))
        {
            Clicks(context, screen);
            Auto(context, config, screen);
            Scan(context, config, screen);
        }
    }

    /// <summary>Every control handler of the screen is a click entry (02 §9.10).</summary>
    private static void Clicks(DomainContext context, ScreenRow screen)
    {
        foreach (var handler in context.Reader.ControlHandlers(screen.Terminal, screen.ScreenId)
                     .OrderBy(handler => handler.ControlId, StringComparer.Ordinal)
                     .ThenBy(handler => handler.HandlerMethodId, StringComparer.Ordinal))
        {
            Write(context, screen, EntryTriggers.Click, handler.HandlerMethodId, handler.ControlId, handler.EvidenceId);
        }
    }

    /// <summary>The presenter methods that run when the screen is shown (02 §9.10).</summary>
    private static void Auto(DomainContext context, UiSection config, ScreenRow screen)
    {
        foreach (var method in PresenterMethods(context, screen)
                     .Where(method => config.AutoEntryMethods.Contains(method.Name, StringComparer.Ordinal)))
        {
            Write(context, screen, EntryTriggers.Auto, method.Id, null, context.EvidenceOf(method));
        }
    }

    /// <summary>
    /// A scan entry: the presenter implements the scan method and registered itself for it
    /// (02 §9.10). The input arrives from the scanner, which is outside the project, so the entry
    /// also records that boundary with itself as the resume point.
    /// </summary>
    private static void Scan(DomainContext context, UiSection config, ScreenRow screen)
    {
        var methods = PresenterMethods(context, screen).ToList();

        var registered = methods.Any(method => context.Reader.CallsitesIn(method.Id).Any(call =>
        {
            var target = call.TargetId is null ? null : context.Reader.Symbol(call.TargetId);
            return target?.Name == config.ScanRegistrationMethod
                   && call.Args.Any(argument => argument.Value is ThisValue);
        }));

        if (!registered) return;

        foreach (var method in methods
                     .Where(method => config.ScanEntryMethods.Contains(method.Name, StringComparer.Ordinal)))
        {
            Write(context, screen, EntryTriggers.Scan, method.Id, null, context.EvidenceOf(method));

            BoundaryWriter.Write(
                context,
                BoundaryCategories.ExternalHardware,
                ContractKinds.DeviceInput,
                TypeNameOf(context, method) + "." + method.Name,
                new JsonObject
                {
                    ["terminal"] = screen.Terminal,
                    ["screenId"] = screen.ScreenId,
                    ["resume"] = new JsonArray(method.Id),
                    ["confidence"] = "single"
                },
                null);
        }
    }

    private static void Write(
        DomainContext context,
        ScreenRow screen,
        string trigger,
        string methodId,
        string? controlId,
        string? evidenceId)
    {
        context.Writer.Add(new EntryRow
        {
            Id = Ids.DerivedId(EntryPrefix, new
            {
                terminal = screen.Terminal,
                screen = screen.ScreenId,
                trigger,
                control = controlId ?? string.Empty,
                method = methodId
            }),
            Terminal = screen.Terminal,
            ScreenId = screen.ScreenId,
            Trigger = trigger,
            ControlId = controlId,
            MethodId = methodId,
            EvidenceId = evidenceId
        });
    }

    /// <summary>The methods of the screen's presenter, which is where an entry lands.</summary>
    private static IEnumerable<SymbolRec> PresenterMethods(DomainContext context, ScreenRow screen)
        => screen.PresenterTypeId is null
            ? Array.Empty<SymbolRec>()
            : context.Reader.SymbolsInContainer(screen.PresenterTypeId)
                .Where(symbol => symbol.Kind == SymbolKinds.Method)
                .OrderBy(symbol => symbol.Id, StringComparer.Ordinal);

    private static string TypeNameOf(DomainContext context, SymbolRec method)
        => method.ContainerId is null ? "(unknown)" : context.Reader.Symbol(method.ContainerId)?.Name ?? "(unknown)";
}
