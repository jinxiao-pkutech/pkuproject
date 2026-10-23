namespace ScreenFlow.Core;

/// <summary>
/// Version identities the whole system is keyed by. They appear in the snapshot row and in every
/// CLI response envelope, so a result can always be traced back to the rules that produced it.
/// </summary>
public static class ScreenFlowVersion
{
    /// <summary>IR schema version (02 §5). Bumped when the record shape changes.</summary>
    public const string IrSchema = "1";

    /// <summary>CLI envelope schema version (02 §11).</summary>
    public const string EnvelopeSchema = "1";

    /// <summary>
    /// Analysis rules version, fixed at <c>"1"</c> by 02 §7.4 and reported in <c>meta</c> and in
    /// every CLI envelope. Bumped whenever extractor or adapter behaviour changes, because it is
    /// part of the snapshot input digest and therefore invalidates existing indexes.
    /// </summary>
    public const string Rules = "1";
}
