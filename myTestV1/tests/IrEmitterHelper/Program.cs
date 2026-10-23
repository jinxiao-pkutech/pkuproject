using System.Text;

namespace IrEmitterHelper;

/// <summary>
/// A stand-in for an out-of-process fact extractor. Writes raw NDJSON to stdout and one line to
/// stderr, so <c>ExternalProcessExtractor</c> can be exercised without a Python or Java toolchain.
/// </summary>
public static class Program
{
    /// <summary>Emit a record that the validator must reject, to prove the line number is reported.</summary>
    public const string InvalidFlag = "--invalid";

    /// <summary>Emit a line that is not JSON at all.</summary>
    public const string MalformedFlag = "--malformed";

    /// <summary>Exit non-zero after writing valid records.</summary>
    public const string FailFlag = "--fail";

    private const string ValidMeta =
        """{"k":"meta","v":{"irVersion":"1","extractor":"ir-emitter-helper","profileId":"fixture","derivation":"native","platformDetected":"editor"}}""";

    private const string ValidSymbol =
        """{"k":"symbol","v":{"id":"s_0123456789abcdef0123","kind":"type","name":"Sample","fqName":"Fixture.Sample","assembly":"Assembly-CSharp","isSource":true,"file":"Assets/Fixture/Sample.cs","span":{"sl":1,"sc":1,"el":9,"ec":2},"typeKind":"class","interfaceIds":[],"modifiers":["partial"],"typeParameters":[],"attributes":[],"declarations":[]}}""";

    /// <summary>Invalid on two counts: the id has no <c>s_</c> prefix and the kind is not in the vocabulary.</summary>
    private const string InvalidSymbol =
        """{"k":"symbol","v":{"id":"not-an-id","kind":"widget","name":"Bad","fqName":"Fixture.Bad","assembly":"Assembly-CSharp","isSource":false}}""";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var stdout = Console.Out;
        stdout.WriteLine(ValidMeta);

        if (args.Contains(MalformedFlag, StringComparer.Ordinal))
        {
            stdout.WriteLine("{ this is not json");
            stdout.Flush();
            return 0;
        }

        if (args.Contains(InvalidFlag, StringComparer.Ordinal))
        {
            stdout.WriteLine(InvalidSymbol);
            stdout.Flush();
            return 0;
        }

        stdout.WriteLine(ValidSymbol);
        stdout.Flush();

        // Exercised as a passed-through diagnostic rather than being swallowed.
        Console.Error.WriteLine("helper: emitted 2 record(s)");
        Console.Error.Flush();

        return args.Contains(FailFlag, StringComparer.Ordinal) ? 3 : 0;
    }
}
