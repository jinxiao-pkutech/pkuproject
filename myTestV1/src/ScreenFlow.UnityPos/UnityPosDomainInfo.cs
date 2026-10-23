namespace ScreenFlow.UnityPos;

/// <summary>
/// Identity of the POS domain adapter set. Every project-specific type name, method name and path
/// lives in <c>config/unitypos.json</c>, never in this assembly's code (CLAUDE.md §4).
/// </summary>
public static class UnityPosDomainInfo
{
    public const string DomainId = "unitypos";

    /// <summary>Relative path of the anchor file these adapters are driven by (02 §4.2).</summary>
    public const string ConfigRelativePath = "config/unitypos.json";
}
