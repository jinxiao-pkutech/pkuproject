using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Extraction;

namespace ScreenFlow.Core.Pipeline;

/// <summary>
/// Build progress as NDJSON on stderr (02 §2).
/// <para>
/// stderr, because stdout carries exactly one JSON document (02 §11.3) and the MCP service parses
/// it as a whole: a progress line written there would make every build look like malformed output.
/// </para>
/// </summary>
public sealed class ProgressReporter : IProgress<ProgressEvent>
{
    private readonly TextWriter _writer;
    private readonly Stopwatch _clock;
    private readonly object _gate = new();

    public ProgressReporter(TextWriter writer, Stopwatch? clock = null)
    {
        _writer = writer;
        _clock = clock ?? Stopwatch.StartNew();
    }

    /// <summary>A reporter that writes nothing, for tests and for in-process callers.</summary>
    public static IProgress<ProgressEvent> Null { get; } = new NullReporter();

    public static ProgressReporter Stderr(Stopwatch? clock = null) => new(Console.Error, clock);

    public long ElapsedMs => _clock.ElapsedMilliseconds;

    public void Report(ProgressEvent value)
    {
        var line = new ProgressLine
        {
            Phase = value.Phase,
            Done = value.Completed,
            Total = value.Total,
            ElapsedMs = _clock.ElapsedMilliseconds,
            Item = value.Item,
            Message = value.Message
        };

        var json = JsonSerializer.Serialize(line, Options);

        // Phases run in parallel inside one build, so the writes are serialised; a torn line would
        // be unparseable for the caller reading NDJSON.
        lock (_gate)
        {
            _writer.WriteLine(json);
            _writer.Flush();
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The line shape of 02 §2, verbatim.</summary>
    private sealed record ProgressLine
    {
        [JsonPropertyName("phase")] public required string Phase { get; init; }
        [JsonPropertyName("done")] public required int Done { get; init; }
        [JsonPropertyName("total")] public required int Total { get; init; }
        [JsonPropertyName("elapsedMs")] public required long ElapsedMs { get; init; }
        [JsonPropertyName("item")] public string? Item { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
    }

    private sealed class NullReporter : IProgress<ProgressEvent>
    {
        public void Report(ProgressEvent value)
        {
        }
    }
}
