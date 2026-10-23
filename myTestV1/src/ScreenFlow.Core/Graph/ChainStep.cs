using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Graph;

/// <summary>What one step of a chain is (02 §10.4).</summary>
public static class ChainStepKinds
{
    public const string Call = "call";
    public const string Lambda = "lambda";
    public const string Navigate = "navigate";
    public const string SocketSend = "socketSend";
    public const string SocketReceive = "socketReceive";
    public const string RemoteDisplay = "remoteDisplay";
    public const string KeySeqDispatch = "keySeqDispatch";
    public const string Enqueue = "enqueue";
    public const string DeviceCommand = "deviceCommand";
    public const string DeviceCallback = "deviceCallback";
    public const string ApiCall = "apiCall";
    public const string DbRead = "dbRead";
    public const string DbWrite = "dbWrite";
    public const string ClosePopup = "closePopup";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Call, Lambda, Navigate, SocketSend, SocketReceive, RemoteDisplay, KeySeqDispatch,
        Enqueue, DeviceCommand, DeviceCallback, ApiCall, DbRead, DbWrite, ClosePopup
    };
}

/// <summary>How sure a chain or a step is (02 §10.4).</summary>
public static class Confidences
{
    public const string Certain = "certain";
    public const string Candidate = "candidate";

    public static readonly IReadOnlyList<string> All = new[] { Certain, Candidate };
}

/// <summary>
/// One hop of a screen-to-screen chain (02 §10.4).
/// <para>
/// Every step carries where it is in the source and an evidence id, because a chain a reader
/// cannot check is not an answer — it is a claim.
/// </para>
/// </summary>
public sealed record ChainStep
{
    [JsonPropertyName("order")] public required int Order { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("terminal")] public required string Terminal { get; init; }
    [JsonPropertyName("methodId")] public required string MethodId { get; init; }
    [JsonPropertyName("namespace")] public string? Namespace { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("file")] public string? File { get; init; }
    [JsonPropertyName("line")] public int? Line { get; init; }
    [JsonPropertyName("confidence")] public string Confidence { get; init; } = Confidences.Certain;

    /// <summary>True when the step does not run inside the call that scheduled it (02 §10.2).</summary>
    [JsonPropertyName("deferred")] public bool Deferred { get; init; }

    /// <summary>A short, factual note: the event path sent, the screen displayed, the key code.</summary>
    [JsonPropertyName("detail")] public string? Detail { get; init; }

    [JsonPropertyName("evidenceId")] public string? EvidenceId { get; init; }
}
