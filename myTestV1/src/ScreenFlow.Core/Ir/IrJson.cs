using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Ir;

/// <summary>
/// An IR line is not valid for schema version 1. Carries the stdout line number when it came from
/// an external extractor, so the offending record can be found (T02 §3).
/// </summary>
public sealed class IrSchemaException : Exception
{
    public IrSchemaException(string message, int? lineNumber = null, IReadOnlyList<string>? problems = null, Exception? inner = null)
        : base(Compose(message, lineNumber, problems), inner)
    {
        LineNumber = lineNumber;
        Problems = problems ?? Array.Empty<string>();
    }

    public int? LineNumber { get; }
    public IReadOnlyList<string> Problems { get; }

    private static string Compose(string message, int? lineNumber, IReadOnlyList<string>? problems)
    {
        var builder = new StringBuilder();
        if (lineNumber is not null) builder.Append("line ").Append(lineNumber.Value).Append(": ");
        builder.Append(message);
        if (problems is { Count: > 0 })
        {
            builder.AppendLine();
            builder.Append(string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }
        return builder.ToString();
    }
}

/// <summary>
/// NDJSON serialisation of the IR (02 §5.1): one record per line as
/// <c>{"k":"&lt;kind&gt;","v":{…}}</c>.
/// <para>
/// Nulls are omitted, arrays never are: an absent array and an empty array would otherwise be
/// indistinguishable, and "no candidates found" has to be expressible.
/// </para>
/// </summary>
public static class IrJson
{
    /// <summary>The envelope's discriminator property.</summary>
    public const string KindProperty = "k";

    /// <summary>The envelope's payload property.</summary>
    public const string ValueProperty = "v";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Same shape, indented. Used only where a human reads the output.</summary>
    public static JsonSerializerOptions PrettyOptions { get; } = CreateOptions(indented: true);

    private static JsonSerializerOptions CreateOptions(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            NumberHandling = JsonNumberHandling.Strict,
            // The IR carries Japanese source text; escaping it would bloat every line and make the
            // files unreadable during diagnosis.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Writes one record as an envelope object. Does not write a line terminator.</summary>
    public static void Write(IrRecord record, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString(KindProperty, record.K);
        writer.WritePropertyName(ValueProperty);
        WritePayload(record, writer);
        writer.WriteEndObject();
    }

    private static void WritePayload(IrRecord record, Utf8JsonWriter writer)
    {
        switch (record)
        {
            case MetaRec meta: JsonSerializer.Serialize(writer, meta, Options); break;
            case FileRec file: JsonSerializer.Serialize(writer, file, Options); break;
            case SymbolRec symbol: JsonSerializer.Serialize(writer, symbol, Options); break;
            case ImplRec impl: JsonSerializer.Serialize(writer, impl, Options); break;
            case CallsiteRec callsite: JsonSerializer.Serialize(writer, callsite, Options); break;
            case AssignRec assign: JsonSerializer.Serialize(writer, assign, Options); break;
            case CreationRec creation: JsonSerializer.Serialize(writer, creation, Options); break;
            case CfgRec cfg: JsonSerializer.Serialize(writer, cfg, Options); break;
            case MarkerRec marker: JsonSerializer.Serialize(writer, marker, Options); break;
            case DiagRec diag: JsonSerializer.Serialize(writer, diag, Options); break;
            default:
                throw new IrSchemaException($"No serialiser for record type {record.GetType().Name}.");
        }
    }

    /// <summary>One record as a single NDJSON line, without the terminator.</summary>
    public static string ToLine(IrRecord record)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            Write(record, writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Parses one NDJSON line. The line number, when known, lands in the exception.</summary>
    public static IrRecord ReadLine(string line, int? lineNumber = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new IrSchemaException("not valid JSON: " + ex.Message, lineNumber, inner: ex);
        }

        if (root is not JsonObject envelope)
            throw new IrSchemaException("record is not a JSON object.", lineNumber);

        if (envelope[KindProperty] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var kind))
            throw new IrSchemaException($"record is missing the '{KindProperty}' discriminator.", lineNumber);

        if (envelope[ValueProperty] is not JsonObject payload)
            throw new IrSchemaException($"record is missing the '{ValueProperty}' payload object.", lineNumber);

        return Deserialize(kind, payload, lineNumber);
    }

    private static IrRecord Deserialize(string kind, JsonObject payload, int? lineNumber)
    {
        try
        {
            IrRecord? record = kind switch
            {
                IrRecordKinds.Meta => payload.Deserialize<MetaRec>(Options),
                IrRecordKinds.File => payload.Deserialize<FileRec>(Options),
                IrRecordKinds.Symbol => payload.Deserialize<SymbolRec>(Options),
                IrRecordKinds.Impl => payload.Deserialize<ImplRec>(Options),
                IrRecordKinds.Callsite => payload.Deserialize<CallsiteRec>(Options),
                IrRecordKinds.Assign => payload.Deserialize<AssignRec>(Options),
                IrRecordKinds.Creation => payload.Deserialize<CreationRec>(Options),
                IrRecordKinds.Cfg => payload.Deserialize<CfgRec>(Options),
                IrRecordKinds.Marker => payload.Deserialize<MarkerRec>(Options),
                IrRecordKinds.Diag => payload.Deserialize<DiagRec>(Options),
                _ => throw new IrSchemaException(
                    $"unknown record kind '{kind}'; expected one of {string.Join(", ", IrRecordKinds.All)}.",
                    lineNumber)
            };

            return record ?? throw new IrSchemaException($"record of kind '{kind}' deserialised to null.", lineNumber);
        }
        catch (JsonException ex)
        {
            throw new IrSchemaException($"record of kind '{kind}' does not match the model: {ex.Message}", lineNumber, inner: ex);
        }
        catch (NotSupportedException ex)
        {
            // Raised when a polymorphic Value carries an unknown kind discriminator.
            throw new IrSchemaException($"record of kind '{kind}' contains an unsupported value: {ex.Message}", lineNumber, inner: ex);
        }
    }

    /// <summary>Reads an NDJSON stream, validating each line. Blank lines are skipped.</summary>
    public static async IAsyncEnumerable<IrRecord> ReadAllAsync(
        TextReader reader,
        bool validate = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var lineNumber = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var record = ReadLine(line, lineNumber);
            if (validate)
            {
                var problems = IrValidator.Validate(record);
                if (problems.Count > 0)
                    throw new IrSchemaException("record is structurally invalid.", lineNumber, problems);
            }
            yield return record;
        }
    }

    public static async Task WriteAllAsync(
        TextWriter writer, IAsyncEnumerable<IrRecord> records, CancellationToken ct = default)
    {
        await foreach (var record in records.WithCancellation(ct).ConfigureAwait(false))
            await writer.WriteLineAsync(ToLine(record).AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}
