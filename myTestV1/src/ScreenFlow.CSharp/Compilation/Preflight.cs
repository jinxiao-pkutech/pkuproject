using ScreenFlow.Core.Config;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.CSharp.Compilation;

/// <summary>
/// The diagnostic vocabulary of 02 §6.2, and the entry point phase P1 uses.
/// <para>
/// Every one of these is always emitted when its condition holds. A missing reference or an
/// unanalysed source changes what the index can answer, so it is reported rather than left for the
/// reader to notice from an absent result.
/// </para>
/// </summary>
public static class Preflight
{
    public const string NoCompilationInput = NoCompilationInputException.ErrorCode;
    public const string UnityVersionMismatch = "W_UNITY_VERSION_MISMATCH";
    public const string PlatformDerived = "W_PLATFORM_DERIVED";
    public const string ReferenceMissing = "W_REFERENCE_MISSING";
    public const string SourceMissing = "W_SOURCE_MISSING";
    public const string SourceNotInRsp = "I_SOURCE_NOT_IN_RSP";
    public const string NonUtf8 = "I_NON_UTF8";
    public const string RspExcluded = "I_RSP_EXCLUDED";
    public const string RspNotInput = "I_RSP_NOT_INPUT";
    public const string RspCycle = "E_RSP_CYCLE";
    public const string RspUnreadable = "E_RSP_UNREADABLE";
    public const string DuplicateAssemblyRsp = "W_DUPLICATE_ASSEMBLY_RSP";
    public const string AssemblyExcluded = "I_ASSEMBLY_EXCLUDED";
    public const string AssemblyNotIncluded = "I_ASSEMBLY_NOT_INCLUDED";

    public static readonly IReadOnlyList<string> AllCodes = new[]
    {
        NoCompilationInput, UnityVersionMismatch, PlatformDerived, ReferenceMissing, SourceMissing,
        SourceNotInRsp, NonUtf8, RspExcluded, RspNotInput, RspCycle, RspUnreadable,
        DuplicateAssemblyRsp, AssemblyExcluded, AssemblyNotIncluded
    };

    /// <summary>Runs discovery and preflight (pipeline phase P1).</summary>
    public static PreflightResult Run(string projectRoot, ProfileConfig profile)
        => RspDiscovery.Discover(projectRoot, profile);

    /// <summary>
    /// Per-file facts for the <c>files</c> table, read with the detected encoding (02 §6.3, §7.2).
    /// A source listed but absent from disk yields nothing here; it was already reported as
    /// <see cref="SourceMissing"/>.
    /// </summary>
    public static IReadOnlyList<FileRec> FileRecords(string projectRoot, PreflightResult preflight)
    {
        var records = new List<FileRec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The files table describes what was analysed, so a reference-only assembly's sources do
        // not appear here even though they took part in compilation.
        foreach (var input in preflight.ExtractedInputs)
        {
            foreach (var source in input.Sources)
            {
                if (!File.Exists(source)) continue;

                var relative = RspDiscovery.Relative(projectRoot, source);
                if (!seen.Add(relative)) continue;

                var bytes = File.ReadAllBytes(source);
                var decoded = SourceEncoding.Decode(bytes);

                records.Add(new FileRec
                {
                    Path = relative,
                    Assembly = input.Name,
                    Encoding = decoded.EncodingName,
                    LineCount = decoded.LineCount,
                    ContentHash = SourceEncoding.ContentHash(bytes)
                });
            }
        }

        return records.OrderBy(r => r.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Diagnostic counts by code, for the build report (02 §11.2, <c>debug stats</c>).</summary>
    public static IReadOnlyDictionary<string, int> CountByCode(IEnumerable<DiagRec> diagnostics)
        => diagnostics
            .GroupBy(d => d.Code, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
