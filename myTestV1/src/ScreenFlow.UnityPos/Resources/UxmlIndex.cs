using System.Xml;
using ScreenFlow.Core.Ir;
using ScreenFlow.UnityPos.Anchors;

namespace ScreenFlow.UnityPos.Resources;

/// <summary>
/// One named element of a UXML file (02 §9.9; 03 §14.3).
/// <para>
/// The hierarchy path is what tells two controls with the same <c>name</c> apart — the fixture has
/// three <c>moneyLabel</c> elements, one per amount button, and binding text to the wrong one would
/// report the wrong button's label.
/// </para>
/// </summary>
public sealed record UxmlControl
{
    public required string File { get; init; }
    public required string Name { get; init; }

    /// <summary>The element's local name, which in this project is the full control type name.</summary>
    public required string ElementType { get; init; }

    /// <summary>Ancestor names joined with <c>/</c>; an unnamed ancestor contributes its element name.</summary>
    public required string HierarchyPath { get; init; }

    /// <summary>Path plus own name, which is unique within a file.</summary>
    public string FullPath => HierarchyPath.Length == 0 ? Name : HierarchyPath + "/" + Name;
}

/// <summary>
/// The UXML files, read for what controls exist, of what type, and where in the tree (02 §9.9).
/// <para>
/// UXML carries no displayed text in this project (03 §14.3) — text arrives through the initialize
/// JSON and the string tables — so this index answers only the structural questions.
/// </para>
/// </summary>
public sealed class UxmlIndex
{
    /// <summary>Emitted when a UXML file cannot be read as XML.</summary>
    public const string UnreadableCode = "W_UXML_UNREADABLE";

    private readonly Dictionary<string, List<UxmlControl>> _byFile;

    private UxmlIndex(Dictionary<string, List<UxmlControl>> byFile) => _byFile = byFile;

    public IReadOnlyList<string> Files
        => _byFile.Keys.OrderBy(path => path, StringComparer.Ordinal).ToList();

    public int ControlCount => _byFile.Values.Sum(list => list.Count);

    /// <summary>
    /// Reads every UXML the glob matches. External entity resolution is off: a UXML carries a
    /// schema location, and resolving it would make the build depend on something outside the
    /// project (02 §9.9).
    /// </summary>
    public static UxmlIndex Load(
        string projectRoot,
        string glob,
        ICollection<DiagRec> diagnostics,
        ICollection<string>? readFiles = null)
    {
        var pattern = AnchorResolver.GlobRegex(glob);
        var byFile = new Dictionary<string, List<UxmlControl>>(StringComparer.Ordinal);

        if (!Directory.Exists(projectRoot)) return new UxmlIndex(byFile);

        foreach (var absolute in Directory.EnumerateFiles(projectRoot, "*.uxml", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = LocalizationIndex.Relative(projectRoot, absolute);
            if (!pattern.IsMatch(relative)) continue;

            readFiles?.Add(relative);

            try
            {
                byFile[relative] = ReadControls(absolute, relative);
            }
            catch (XmlException exception)
            {
                diagnostics.Add(DiagRec.Warning(
                    UnreadableCode,
                    $"'{relative}' is not readable as XML ({exception.Message}); its controls are unknown.",
                    relative,
                    exception.LineNumber));
            }
        }

        return new UxmlIndex(byFile);
    }

    private static List<UxmlControl> ReadControls(string absolutePath, string relativePath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,

            // A UXML writes xsi= rather than xmlns:xsi, so its namespace declarations are not valid
            // XML namespaces; the document is read for its structure, not validated (03 §14.3).
            CheckCharacters = false
        };

        var controls = new List<UxmlControl>();
        var ancestors = new List<string>();

        using var stream = File.OpenRead(absolutePath);
        using var reader = XmlReader.Create(stream, settings);

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var elementName = reader.LocalName;
                    var name = reader.GetAttribute("name");
                    var empty = reader.IsEmptyElement;

                    if (name is not null)
                    {
                        controls.Add(new UxmlControl
                        {
                            File = relativePath,
                            Name = name,
                            ElementType = elementName,
                            HierarchyPath = string.Join('/', ancestors)
                        });
                    }

                    // An unnamed element still takes part in the path, under its element name.
                    if (!empty) ancestors.Add(name ?? elementName);
                    break;
                }

                case XmlNodeType.EndElement:
                    if (ancestors.Count > 0) ancestors.RemoveAt(ancestors.Count - 1);
                    break;
            }
        }

        return controls;
    }

    public IReadOnlyList<UxmlControl> Controls(string file)
        => _byFile.TryGetValue(file, out var controls) ? controls : Array.Empty<UxmlControl>();

    public IReadOnlyList<UxmlControl> AllControls()
        => _byFile.Values.SelectMany(list => list)
            .OrderBy(control => control.File, StringComparer.Ordinal)
            .ThenBy(control => control.FullPath, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every control of that name in the file; several is normal (03 §14.3).</summary>
    public IReadOnlyList<UxmlControl> ByName(string file, string name)
        => Controls(file).Where(control => control.Name == name).ToList();

    /// <summary>
    /// The UXML file of a view, found by file name. The view and its UXML share a name by
    /// convention (03 §14.3); when several files match, none is chosen.
    /// </summary>
    public string? FileOfView(string viewTypeName)
    {
        var matches = _byFile.Keys
            .Where(path => string.Equals(
                Path.GetFileNameWithoutExtension(path),
                viewTypeName,
                StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }
}
