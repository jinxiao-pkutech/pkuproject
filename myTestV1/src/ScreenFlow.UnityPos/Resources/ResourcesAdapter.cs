using System.Text.Json.Serialization;
using ScreenFlow.Core.Config;

namespace ScreenFlow.UnityPos.Resources;

/// <summary>The <c>resources</c> section of <c>unitypos.json</c> (02 §4.2).</summary>
public sealed record ResourcesSection
{
    [JsonPropertyName("uxmlGlob")] public string UxmlGlob { get; init; } = "Assets/**/*.uxml";
    [JsonPropertyName("stringTableRoot")] public string StringTableRoot { get; init; } = "Assets/Localization/StringTable";

    [JsonPropertyName("stringTableSearchOrder")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> StringTableSearchOrder { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    [JsonPropertyName("errorMessageJson")]
    public string ErrorMessageJson { get; init; } = "Assets/StreamingAssets/ErrMsg/ErrMsgType1_Initialize.json";

    [JsonPropertyName("errorMessageLocaleFields")]
    public IReadOnlyDictionary<string, string> ErrorMessageLocaleFields { get; init; }
        = new Dictionary<string, string>();

    [JsonPropertyName("defaultLocale")] public string DefaultLocale { get; init; } = "ja";

    /// <summary>The collections to search for a terminal's text, in order (03 §15).</summary>
    public IReadOnlyList<string> SearchOrderOf(string terminal)
        => StringTableSearchOrder.TryGetValue(terminal, out var order) ? order : Array.Empty<string>();
}

/// <summary>The parts of the <c>ui</c> section the resource readers need (02 §4.2; 03 §14.1).</summary>
public sealed record UiResourcesSection
{
    [JsonPropertyName("initJsonDir")]
    public string InitJsonDir { get; init; } = "Assets/Common/View/Resources/ScreenInitialize";

    [JsonPropertyName("initJsonNamePattern")]
    public string InitJsonNamePattern { get; init; } = "{ViewWithoutSuffix}_Initialize.json";

    [JsonPropertyName("viewSuffix")] public string ViewSuffix { get; init; } = "_View";
    [JsonPropertyName("elementDtoSuffix")] public string ElementDtoSuffix { get; init; } = "_Element_DTO";
}

/// <summary>
/// Everything read from resource files, handed to the adapters that need it (02 §9.9).
/// <para>
/// Kept as one object on the context so the ui-bindings adapter can ask "which control is named
/// this, and what text does its key resolve to" without reading any file again.
/// </para>
/// </summary>
public sealed record ResourceCatalog
{
    public required UxmlIndex Uxml { get; init; }
    public required InitJsonIndex InitJson { get; init; }
    public required LocalizationIndex Localization { get; init; }
    public required ErrorMessageIndex Messages { get; init; }
    public required ResourcesSection Config { get; init; }
    public required UiResourcesSection Ui { get; init; }

    /// <summary>
    /// The text a key resolves to for a terminal, following that terminal's collection order
    /// (03 §15). Returns which collection answered, so a report can cite it.
    /// </summary>
    public (string? Collection, string? Raw, string? Plain) Text(string terminal, string key, string locale)
    {
        var (collection, raw) = Localization.Lookup(Config.SearchOrderOf(terminal), key, locale);
        return (collection, raw, RichText.PlainOrNull(raw));
    }
}

/// <summary>
/// Reads the resource files (pipeline phase P6, 02 §2) and writes the <c>texts</c> and
/// <c>messages</c> tables.
/// <para>
/// Runs before every other adapter because a control's text, a button's label and an error message
/// all come from files rather than from code — without them a screen's text is simply not in the
/// index (02 §9.9).
/// </para>
/// </summary>
public sealed class ResourcesAdapter : IDomainAdapter
{
    public const string Feature = "resources";

    public string Id => AdapterOrder.Resources;

    /// <summary>Resources are phase P6, ahead of the domain adapters (02 §2).</summary>
    public string Phase => "P6";

    public void Run(DomainContext context)
    {
        var config = context.Section<ResourcesSection>(context.Domain.Resources, "resources");
        var ui = context.Section<UiResourcesSection>(context.Domain.Ui, "ui");

        var diagnostics = new List<Core.Ir.DiagRec>();
        var readFiles = new List<string>();

        var uxml = UxmlIndex.Load(context.ProjectRoot, config.UxmlGlob, diagnostics, readFiles);

        var initJson = InitJsonIndex.Load(
            context.ProjectRoot, ui.InitJsonDir, ui.InitJsonNamePattern, diagnostics, readFiles);

        var localization = LocalizationIndex.Load(
            context.ProjectRoot, config.StringTableRoot, diagnostics, readFiles);

        var messages = ErrorMessageIndex.Load(
            context.ProjectRoot, config.ErrorMessageJson, config.ErrorMessageLocaleFields, diagnostics, readFiles);

        foreach (var row in localization.Rows()) context.Writer.Add(row);
        foreach (var row in messages.Rows()) context.Writer.Add(row);

        foreach (var diagnostic in diagnostics) context.Diagnose(diagnostic);

        // The resource files count towards the staleness fingerprint (02 §7.6). The list comes from
        // ResourceFiles rather than from what was parsed, so that a query recomputing the
        // fingerprint arrives at exactly the same set — a file that failed to parse is still an
        // input, and a build and a status check must not disagree about it.
        foreach (var file in ResourceFiles.Enumerate(context.ProjectRoot, config, ui))
            context.NoteReadFile(file);

        context.Resources = new ResourceCatalog
        {
            Uxml = uxml,
            InitJson = initJson,
            Localization = localization,
            Messages = messages,
            Config = config,
            Ui = ui
        };
    }
}

/// <summary>
/// The resource files of a project (02 §7.6, §9.9).
/// <para>
/// One definition shared by the build, which records the fingerprint, and by a query, which
/// recomputes it to decide whether the index is stale. Two definitions would make every index look
/// stale, or worse, look fresh when it is not.
/// </para>
/// </summary>
public static class ResourceFiles
{
    public static IReadOnlyList<string> Enumerate(
        string projectRoot,
        ResourcesSection resources,
        UiResourcesSection ui)
    {
        var files = new List<string>();

        files.AddRange(Matching(projectRoot, "*.uxml", resources.UxmlGlob));
        files.AddRange(Under(projectRoot, resources.StringTableRoot, "*.asset"));
        files.AddRange(InitJson(projectRoot, ui));

        if (File.Exists(Path.Combine(projectRoot, resources.ErrorMessageJson)))
            files.Add(resources.ErrorMessageJson);

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> Matching(string projectRoot, string searchPattern, string glob)
    {
        if (!Directory.Exists(projectRoot)) return Array.Empty<string>();

        var pattern = Anchors.AnchorResolver.GlobRegex(glob);

        return Directory.EnumerateFiles(projectRoot, searchPattern, SearchOption.AllDirectories)
            .Select(absolute => LocalizationIndex.Relative(projectRoot, absolute))
            .Where(relative => pattern.IsMatch(relative));
    }

    private static IEnumerable<string> Under(string projectRoot, string directory, string searchPattern)
    {
        var root = Path.Combine(projectRoot, directory);
        if (!Directory.Exists(root)) return Array.Empty<string>();

        return Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories)
            .Select(absolute => LocalizationIndex.Relative(projectRoot, absolute));
    }

    private static IEnumerable<string> InitJson(string projectRoot, UiResourcesSection ui)
    {
        var suffix = ui.InitJsonNamePattern.Replace(
            InitJsonIndex.NamePlaceholder, string.Empty, StringComparison.Ordinal);

        return Under(projectRoot, ui.InitJsonDir, "*.json")
            .Where(relative => suffix.Length == 0
                               || Path.GetFileName(relative).EndsWith(suffix, StringComparison.Ordinal));
    }
}
