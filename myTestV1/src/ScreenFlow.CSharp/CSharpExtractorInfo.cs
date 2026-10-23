namespace ScreenFlow.CSharp;

/// <summary>
/// Identity of the C#/Roslyn extractor. The id travels with every snapshot so a reader of the
/// index knows which extractor produced its facts, which is what makes a second extractor
/// (Java, Python) addable later without touching the core (02 §1.1).
/// </summary>
public static class CSharpExtractorInfo
{
    public const string ExtractorId = "csharp-roslyn";

    /// <summary>The IR schema version this extractor emits; the core refuses a mismatch.</summary>
    public const string IrSchemaVersion = Core.ScreenFlowVersion.IrSchema;
}
