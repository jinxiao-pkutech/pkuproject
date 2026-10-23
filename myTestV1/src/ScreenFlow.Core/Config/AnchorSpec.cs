using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Config;

/// <summary>The five anchor shapes of 02 §4.2.</summary>
public enum AnchorKind
{
    /// <summary>A named type declared in one specific file.</summary>
    TypeInFile,

    /// <summary>A named type declared in a glob-matched file; the match must be unique.</summary>
    TypeInFileGlob,

    /// <summary>A type addressed by fully qualified name.</summary>
    TypeByFqName,

    /// <summary>Any type declared in the glob-matched files, used together with a member.</summary>
    AnyTypeInFileGlob,

    /// <summary>A member of a type anchor; all same-named members including every overload.</summary>
    Member,

    /// <summary>A set of types selected by namespace, directory, name pattern and kind.</summary>
    TypeSet
}

/// <summary>
/// A POS source anchor (02 §4.2). Every project-specific type, member and type set the adapters
/// need is addressed through one of these, which is what keeps such names out of the C# code
/// (CLAUDE.md §4).
/// <para>
/// Resolution happens in <c>ScreenFlow.UnityPos</c>: zero matches, or several where the shape
/// requires uniqueness, is a diagnostic that disables the dependent adapter feature. Falling back
/// to a global search by simple name is forbidden.
/// </para>
/// </summary>
public sealed record AnchorSpec
{
    /// <summary>Simple type name, or <c>*</c> for "any type in the glob-matched files".</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>Project-root-relative file the type is declared in.</summary>
    [JsonPropertyName("file")] public string? File { get; init; }

    /// <summary>Glob over project-root-relative paths.</summary>
    [JsonPropertyName("fileGlob")] public string? FileGlob { get; init; }

    /// <summary>Fully qualified type name.</summary>
    [JsonPropertyName("fqName")] public string? FqName { get; init; }

    /// <summary>Member name, or <c>*</c> for every member of the anchored type.</summary>
    [JsonPropertyName("member")] public string? Member { get; init; }

    /// <summary>Namespace of a type-set anchor.</summary>
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }

    /// <summary>Directory of a type-set anchor.</summary>
    [JsonPropertyName("dir")] public string? Dir { get; init; }

    /// <summary>Regex over the simple type name, for a type-set anchor.</summary>
    [JsonPropertyName("typeNamePattern")] public string? TypeNamePattern { get; init; }

    /// <summary>Type kind filter of a type-set anchor, e.g. <c>interface</c>.</summary>
    [JsonPropertyName("typeKind")] public string? TypeKind { get; init; }

    /// <summary><c>*</c> as the type name means the file, not the name, selects the type.</summary>
    public const string Wildcard = "*";

    [JsonIgnore] public bool MatchesAnyType => Type == Wildcard;

    [JsonIgnore] public bool MatchesAnyMember => Member == Wildcard;

    /// <summary>True when the shape addresses exactly one type and so must resolve uniquely.</summary>
    [JsonIgnore]
    public bool RequiresUniqueType
        => Kind is AnchorKind.TypeInFile or AnchorKind.TypeInFileGlob or AnchorKind.TypeByFqName
            || (Kind == AnchorKind.Member && !MatchesAnyType);

    /// <summary>Which of the five shapes this is. Invalid combinations throw; validate first.</summary>
    [JsonIgnore]
    public AnchorKind Kind
    {
        get
        {
            if (TypeNamePattern is not null) return AnchorKind.TypeSet;
            if (Member is not null) return AnchorKind.Member;
            return TypePartKind;
        }
    }

    /// <summary>
    /// The shape of the type part, which a member anchor reuses: a member anchor is a type anchor
    /// plus a member name (02 §4.2).
    /// </summary>
    [JsonIgnore]
    public AnchorKind TypePartKind
    {
        get
        {
            if (FqName is not null) return AnchorKind.TypeByFqName;
            if (MatchesAnyType && FileGlob is not null) return AnchorKind.AnyTypeInFileGlob;
            if (Type is not null && File is not null) return AnchorKind.TypeInFile;
            if (Type is not null && FileGlob is not null) return AnchorKind.TypeInFileGlob;
            throw new InvalidOperationException(
                "Anchor has no recognisable type part; call Validate before reading Kind.");
        }
    }

    /// <summary>
    /// Returns every reason this anchor is not one of the five shapes. An empty result means valid.
    /// Rejecting a malformed anchor at load time is what stops a typo from silently disabling an
    /// adapter much later in the build.
    /// </summary>
    public IReadOnlyList<string> Validate(string path)
    {
        var errors = new List<string>();

        if (TypeNamePattern is not null)
        {
            if (Namespace is null && Dir is null)
                errors.Add($"{path}: a type-set anchor needs 'namespace' or 'dir' alongside 'typeNamePattern'.");
            if (Type is not null || File is not null || FileGlob is not null || FqName is not null || Member is not null)
                errors.Add($"{path}: a type-set anchor must not mix in 'type', 'file', 'fileGlob', 'fqName' or 'member'.");
            if (TypeNamePattern.Length == 0)
                errors.Add($"{path}: 'typeNamePattern' must not be empty.");
            else if (!TryCompile(TypeNamePattern, out var reason))
                errors.Add($"{path}: 'typeNamePattern' is not a valid regular expression: {reason}");
            return errors;
        }

        if (Namespace is not null || Dir is not null || TypeKind is not null)
            errors.Add($"{path}: 'namespace', 'dir' and 'typeKind' belong to a type-set anchor and need 'typeNamePattern'.");

        if (FqName is not null)
        {
            if (Type is not null || File is not null || FileGlob is not null)
                errors.Add($"{path}: 'fqName' must not be combined with 'type', 'file' or 'fileGlob'.");
            if (FqName.Length == 0)
                errors.Add($"{path}: 'fqName' must not be empty.");
        }
        else if (Type is null)
        {
            errors.Add($"{path}: anchor needs 'type', 'fqName' or 'typeNamePattern'.");
        }
        else
        {
            if (File is null && FileGlob is null)
                errors.Add($"{path}: anchor with 'type' needs 'file' or 'fileGlob'.");
            if (File is not null && FileGlob is not null)
                errors.Add($"{path}: anchor must not set both 'file' and 'fileGlob'.");
            if (MatchesAnyType && FileGlob is null)
                errors.Add($"{path}: type '*' selects by file, so it requires 'fileGlob'.");
            if (MatchesAnyType && Member is null)
                errors.Add($"{path}: type '*' is only meaningful together with 'member'.");
        }

        if (Member is { Length: 0 })
            errors.Add($"{path}: 'member' must not be empty.");

        return errors;
    }

    private static bool TryCompile(string pattern, out string reason)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
            reason = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>A short human-readable form, used in diagnostics so an anchor is identifiable.</summary>
    public string Describe() => Kind switch
    {
        AnchorKind.TypeSet => $"types matching /{TypeNamePattern}/ in {Namespace ?? Dir}",
        AnchorKind.Member => $"{DescribeTypePart()}::{Member}",
        _ => DescribeTypePart()
    };

    private string DescribeTypePart() => TypePartKind switch
    {
        AnchorKind.TypeByFqName => FqName!,
        AnchorKind.TypeInFile => $"{Type} in {File}",
        AnchorKind.TypeInFileGlob => $"{Type} in {FileGlob}",
        AnchorKind.AnyTypeInFileGlob => $"any type in {FileGlob}",
        _ => "(invalid anchor)"
    };
}
