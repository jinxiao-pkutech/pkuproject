using System.Globalization;

namespace ScreenFlow.Core.Breaks;

/// <summary>Break categories (02 §8.3).</summary>
public static class BreakCategories
{
    public const string ExternalHardware = "ExternalHardware";
    public const string ExternalMicroservice = "ExternalMicroservice";
    public const string TerminalTcpCurrentScreen = "TerminalTcpCurrentScreen";
    public const string NativeInterop = "NativeInterop";
    public const string DatabaseMediated = "DatabaseMediated";
    public const string DiAmbiguous = "DiAmbiguous";
    public const string DeferredExecution = "DeferredExecution";
    public const string KeySeqStageDependent = "KeySeqStageDependent";
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ExternalHardware, ExternalMicroservice, TerminalTcpCurrentScreen, NativeInterop,
        DatabaseMediated, DiAmbiguous, DeferredExecution, KeySeqStageDependent, Unknown
    };
}

/// <summary>Break reasons (02 §8.3).</summary>
public static class BreakReasons
{
    public const string NoSourceForExternalSystem = "NoSourceForExternalSystem";
    public const string RuntimeStateDependentDispatch = "RuntimeStateDependentDispatch";
    public const string MultipleDiRegistrations = "MultipleDiRegistrations";
    public const string ReflectionConstruction = "ReflectionConstruction";
    public const string DeferredExecution = "DeferredExecution";
    public const string DataMediatedByDatabase = "DataMediatedByDatabase";
    public const string StringKeyedDispatchUnmatched = "StringKeyedDispatchUnmatched";
    public const string NativeInterop = "NativeInterop";
    public const string UnresolvedSymbol = "UnresolvedSymbol";
    public const string UnresolvedConstantArgument = "UnresolvedConstantArgument";
    public const string ScreenNotRegistered = "ScreenNotRegistered";
    public const string TruncatedByDepthLimit = "TruncatedByDepthLimit";
    public const string TruncatedByCandidateLimit = "TruncatedByCandidateLimit";

    public static readonly IReadOnlyList<string> All = new[]
    {
        NoSourceForExternalSystem, RuntimeStateDependentDispatch, MultipleDiRegistrations,
        ReflectionConstruction, DeferredExecution, DataMediatedByDatabase,
        StringKeyedDispatchUnmatched, NativeInterop, UnresolvedSymbol,
        UnresolvedConstantArgument, ScreenNotRegistered, TruncatedByDepthLimit,
        TruncatedByCandidateLimit
    };
}

/// <summary>How sure the resume points of a break are (02 §8.3).</summary>
public static class ResumeConfidences
{
    public const string Single = "single";
    public const string Multiple = "multiple";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = new[] { Single, Multiple, None };
}

/// <summary>
/// The explanation texts of 02 §8.3, transcribed.
/// <para>
/// Only these may be produced. A break is the part of an answer a reader acts on, and a free-form
/// sentence per case would make two builds of the same project read differently — and would let an
/// explanation drift away from what the index actually found.
/// </para>
/// </summary>
public static class WhyTemplates
{
    /// <summary>The placeholders a template may carry.</summary>
    public sealed record Fields
    {
        public string? Key { get; init; }
        public string? Topic { get; init; }
        public int? Count { get; init; }
        public string? Text { get; init; }
    }

    /// <summary>
    /// The sentence for a break. Picked by category and reason, exactly as 02 §8.3 tabulates them;
    /// an unlisted combination has no sentence and says so rather than inventing one.
    /// </summary>
    public static string Of(string category, string reason, Fields fields)
    {
        var key = fields.Key ?? string.Empty;
        var topic = fields.Topic ?? string.Empty;
        var count = (fields.Count ?? 0).ToString(CultureInfo.InvariantCulture);
        var text = fields.Text ?? string.Empty;

        return (category, reason) switch
        {
            (BreakCategories.ExternalHardware, _) =>
                $"调用硬件命令 {key}。请求经 IOBOX 发往外部设备，其源码不在本项目中；结果通过 MQTT 异步返回（Topic: {topic}），请求与回调之间不存在静态调用关系。",

            (BreakCategories.ExternalMicroservice, _) =>
                $"调用微服务 API {key}。服务端源码不在本项目中，响应内容由外部决定。",

            (BreakCategories.TerminalTcpCurrentScreen, BreakReasons.StringKeyedDispatchUnmatched) =>
                $"向对端终端发送事件 {key}，但对端没有任何画面处理该事件路径。",

            (BreakCategories.TerminalTcpCurrentScreen, _) =>
                $"向对端终端发送事件 {key}。对端由“当时显示的非共通画面”接收，接收方取决于运行时画面状态；候选 {count} 个。",

            (BreakCategories.NativeInterop, _) =>
                $"调用 Android 原生代码 {key}，其实现不在 C# 源码中。",

            (BreakCategories.DatabaseMediated, _) =>
                $"从数据库表 {key} 读取数据；该数据由其他处理写入（写入方 {count} 处），调用链在此中断。",

            (BreakCategories.DiAmbiguous, BreakReasons.ReflectionConstruction) =>
                $"通过反射创建或取得对象（{key}），目标在运行时决定。",

            (BreakCategories.DiAmbiguous, _) =>
                $"从 DI 取得 {key}，存在 {count} 个注册实现，实际使用哪一个取决于注册顺序。",

            (BreakCategories.DeferredExecution, _) =>
                $"处理被放入 {key} 延迟执行，不在当前调用中同步运行。",

            (BreakCategories.KeySeqStageDependent, BreakReasons.UnresolvedConstantArgument) =>
                $"执行键序事件，但事件码无法静态确定（{text}）。",

            (BreakCategories.KeySeqStageDependent, _) =>
                $"执行键序事件 {key}。实际处理函数由运行时的当前 Stage 决定；候选 {count} 个。",

            (BreakCategories.Unknown, BreakReasons.UnresolvedConstantArgument) =>
                $"参数无法静态确定（{text}），无法确定目标。",

            (BreakCategories.Unknown, BreakReasons.UnresolvedSymbol) =>
                $"调用目标无法绑定（{text}）。",

            (BreakCategories.Unknown, BreakReasons.TruncatedByDepthLimit) =>
                $"调用链超过深度上限 {count}，在此截断。",

            (BreakCategories.Unknown, BreakReasons.TruncatedByCandidateLimit) =>
                $"候选数超过上限 {count}，在此截断。",

            (BreakCategories.Unknown, BreakReasons.ScreenNotRegistered) =>
                $"跳转目标画面 {key} 不在任何画面注册表中。",

            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                $"02 §8.3 has no why template for category '{category}' with reason '{reason}'.")
        };
    }

    /// <summary>True when the pair has a template, so a caller can check before building one.</summary>
    public static bool Has(string category, string reason)
    {
        try
        {
            _ = Of(category, reason, new Fields());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
