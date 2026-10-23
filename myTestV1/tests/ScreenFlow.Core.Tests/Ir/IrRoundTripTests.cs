using ScreenFlow.Core.Ir;
using Xunit;

namespace ScreenFlow.Core.Tests.IrTests;

/// <summary>
/// Round-trip and validation of the IR (02 §5).
/// <para>
/// Equality is asserted on the canonical NDJSON line rather than on object identity. Records hold
/// <c>IReadOnlyList</c> members, for which the compiler-generated record equality is reference
/// equality, so object comparison would report a difference where none exists. The line is also the
/// thing that actually has to be stable: CLAUDE.md §5 requires deterministic output.
/// </para>
/// </summary>
public sealed class IrRoundTripTests
{
    [Fact]
    [Trait("Task", "T02")]
    public void Ir_RoundTrip_AllRecordKinds()
    {
        // Every kind declared by the model has an example, so a kind added later cannot slip
        // through untested.
        Assert.Equal(
            IrRecordKinds.All.OrderBy(k => k, StringComparer.Ordinal),
            IrSamples.AllRecords.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (kind, record) in IrSamples.AllRecords)
        {
            Assert.Equal(kind, record.K);

            var problems = IrValidator.Validate(record);
            Assert.True(problems.Count == 0,
                $"sample record '{kind}' is not valid: {string.Join("; ", problems)}");

            var line = IrJson.ToLine(record);
            Assert.StartsWith($"{{\"k\":\"{kind}\",\"v\":", line, StringComparison.Ordinal);

            var readBack = IrJson.ReadLine(line);
            Assert.Equal(record.GetType(), readBack.GetType());
            Assert.Equal(kind, readBack.K);
            Assert.Equal(line, IrJson.ToLine(readBack));
        }
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_RoundTrip_AllValueKinds()
    {
        Assert.Equal(
            ValueKinds.All.OrderBy(k => k, StringComparer.Ordinal),
            IrSamples.AllValues.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (kind, value) in IrSamples.AllValues)
        {
            Assert.Equal(kind, value.Kind);

            // A value travels inside a record, so it is round-tripped the way it is really used.
            var carrier = new AssignRec
            {
                Id = IrSamples.AssignId,
                MethodId = IrSamples.MethodId,
                Target = new LocalValue { Name = "target", Text = "target" },
                Operator = AssignRec.Operators.Assign,
                Value = value,
                Order = 0,
                Span = Span.Of(1, 1, 1, 2)
            };

            var problems = IrValidator.Validate(carrier);
            Assert.True(problems.Count == 0,
                $"sample value '{kind}' is not valid: {string.Join("; ", problems)}");

            var line = IrJson.ToLine(carrier);
            Assert.Contains($"\"kind\":\"{kind}\"", line, StringComparison.Ordinal);

            var readBack = Assert.IsType<AssignRec>(IrJson.ReadLine(line));
            Assert.Equal(kind, readBack.Value.Kind);
            Assert.Equal(line, IrJson.ToLine(readBack));
        }
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_RoundTrip_OmitsNullsAndKeepsEmptyArrays()
    {
        var record = new SymbolRec
        {
            Id = IrSamples.TypeId,
            Kind = SymbolKinds.Type,
            Name = "Sample",
            FqName = "Fixture.Sample",
            Assembly = IrSamples.Assembly,
            IsSource = false
        };

        var line = IrJson.ToLine(record);

        // Nulls are omitted (02 §5.1 / T02 §2).
        Assert.DoesNotContain("\"containerId\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("null", line, StringComparison.Ordinal);

        // Arrays are not: "no interfaces" and "interfaces unknown" must stay distinguishable.
        Assert.Contains("\"interfaceIds\":[]", line, StringComparison.Ordinal);
        Assert.Contains("\"modifiers\":[]", line, StringComparison.Ordinal);
        Assert.Contains("\"declarations\":[]", line, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Validator_RejectsMissingId()
    {
        var record = new SymbolRec
        {
            Id = string.Empty,
            Kind = SymbolKinds.Type,
            Name = "Sample",
            FqName = "Fixture.Sample",
            Assembly = IrSamples.Assembly,
            IsSource = false
        };

        var problems = IrValidator.Validate(record);
        Assert.Contains(problems, p => p.Contains("symbol.id is required", StringComparison.Ordinal));

        // A plausible-looking id with the wrong prefix is rejected too, because the prefix is what
        // tells a reader which table an id belongs to.
        var wrongPrefix = record with { Id = "c_0123456789abcdef0123" };
        Assert.Contains(
            IrValidator.Validate(wrongPrefix),
            p => p.Contains("expected prefix 's_'", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Validator_RejectsDeepValue()
    {
        // Seven levels of nesting: one more than 02 §5.4 allows.
        Value nested = ConstValue.Of("leaf");
        for (var depth = 0; depth < 6; depth++)
        {
            nested = new MemberAccessValue { Receiver = nested, Text = "chain" };
        }
        Assert.Equal(7, IrValidator.DepthOf(nested));

        var record = new AssignRec
        {
            Id = IrSamples.AssignId,
            MethodId = IrSamples.MethodId,
            Target = new LocalValue { Name = "t", Text = "t" },
            Operator = AssignRec.Operators.Assign,
            Value = nested,
            Order = 0,
            Span = Span.Of(1, 1, 1, 2)
        };

        Assert.Contains(
            IrValidator.Validate(record),
            p => p.Contains($"exceeds the limit of {IrValidator.MaxValueDepth}", StringComparison.Ordinal));

        // Exactly at the limit is accepted.
        Value atLimit = ConstValue.Of("leaf");
        for (var depth = 0; depth < 5; depth++)
            atLimit = new MemberAccessValue { Receiver = atLimit, Text = "chain" };
        Assert.Equal(IrValidator.MaxValueDepth, IrValidator.DepthOf(atLimit));
        Assert.Empty(IrValidator.Validate(record with { Value = atLimit }));
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Validator_RejectsUnsortedOrOversizedStrings()
    {
        AssignRec Carrier(Value value) => new()
        {
            Id = IrSamples.AssignId,
            MethodId = IrSamples.MethodId,
            Target = new LocalValue { Name = "t", Text = "t" },
            Operator = AssignRec.Operators.Assign,
            Value = value,
            Order = 0,
            Span = Span.Of(1, 1, 1, 2)
        };

        var unsorted = new StringsValue { Values = new[] { "b", "a" }, Text = "x" };
        Assert.Contains(
            IrValidator.Validate(Carrier(unsorted)),
            p => p.Contains("is not sorted", StringComparison.Ordinal));

        var duplicated = new StringsValue { Values = new[] { "a", "a" }, Text = "x" };
        Assert.Contains(
            IrValidator.Validate(Carrier(duplicated)),
            p => p.Contains("contains duplicates", StringComparison.Ordinal));

        var tooMany = new StringsValue
        {
            Values = Enumerable.Range(0, StringsValue.MaxValues + 1)
                .Select(i => i.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .ToArray(),
            Text = "x"
        };
        Assert.Contains(
            IrValidator.Validate(Carrier(tooMany)),
            p => p.Contains($"limit is {StringsValue.MaxValues}", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_Validator_RejectsAbsolutePathsAndSourceSymbolWithoutLocation()
    {
        var absolute = new FileRec
        {
            Path = "C:/work/Assets/Sample.cs",
            Assembly = IrSamples.Assembly,
            Encoding = Encodings.Utf8,
            LineCount = 1,
            ContentHash = "abc"
        };
        Assert.Contains(
            IrValidator.Validate(absolute),
            p => p.Contains("must be project-root relative", StringComparison.Ordinal));

        var backslashes = absolute with { Path = "Assets\\Sample.cs" };
        Assert.Contains(
            IrValidator.Validate(backslashes),
            p => p.Contains("must use forward slashes", StringComparison.Ordinal));

        var sourceWithoutLocation = new SymbolRec
        {
            Id = IrSamples.TypeId,
            Kind = SymbolKinds.Type,
            Name = "Sample",
            FqName = "Fixture.Sample",
            Assembly = IrSamples.Assembly,
            IsSource = true
        };
        var problems = IrValidator.Validate(sourceWithoutLocation);
        Assert.Contains(problems, p => p.Contains("symbol.file is required", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("symbol.span is required", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Task", "T02")]
    public void Ir_ReadLine_RejectsUnknownKindAndMalformedJson()
    {
        var unknownKind = Assert.Throws<IrSchemaException>(
            () => IrJson.ReadLine("""{"k":"widget","v":{}}""", 9));
        Assert.Equal(9, unknownKind.LineNumber);
        Assert.Contains("unknown record kind 'widget'", unknownKind.Message, StringComparison.Ordinal);

        var malformed = Assert.Throws<IrSchemaException>(() => IrJson.ReadLine("{ not json", 3));
        Assert.Equal(3, malformed.LineNumber);
        Assert.Contains("line 3", malformed.Message, StringComparison.Ordinal);

        var missingEnvelope = Assert.Throws<IrSchemaException>(() => IrJson.ReadLine("""{"k":"meta"}"""));
        Assert.Contains("'v'", missingEnvelope.Message, StringComparison.Ordinal);

        // An unrecognised value kind must fail rather than silently become 'unknown'.
        var badValueKind = Assert.Throws<IrSchemaException>(() => IrJson.ReadLine(
            """{"k":"assign","v":{"id":"a_0123456789abcdef0123","methodId":"s_0123456789abcdef0123","target":{"kind":"local","name":"t"},"operator":"=","value":{"kind":"mystery"},"order":0,"span":{"sl":1,"sc":1,"el":1,"ec":2}}}"""));
        Assert.Contains("assign", badValueKind.Message, StringComparison.Ordinal);
    }
}
