using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Extraction;

/// <summary>How to start an out-of-process extractor.</summary>
public sealed record ExternalExtractorOptions
{
    public required string ExtractorId { get; init; }

    /// <summary>Executable to run.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Arguments. <c>{projectRoot}</c> and <c>{profileId}</c> are substituted, which is how the
    /// same configuration serves any project root.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    public string IrVersion { get; init; } = IrValidator.SupportedIrVersion;

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> ResolveArguments(string projectRoot, string profileId)
        => Arguments
            .Select(a => a
                .Replace("{projectRoot}", projectRoot, StringComparison.Ordinal)
                .Replace("{profileId}", profileId, StringComparison.Ordinal))
            .ToArray();
}

/// <summary>
/// Runs a non-.NET extractor and reads NDJSON from its stdout (02 §5.1, T02 §3).
/// <para>
/// Every line is validated before it reaches the core, and an invalid line fails the build with its
/// stdout line number. An extractor that cannot be trusted to emit conforming IR is the one thing
/// that could corrupt the index silently, so it is not given the benefit of the doubt.
/// </para>
/// stderr is passed through as <see cref="DiagRec"/> records rather than discarded.
/// </summary>
public sealed class ExternalProcessExtractor : IFactExtractor
{
    private readonly ExternalExtractorOptions _options;

    public ExternalProcessExtractor(ExternalExtractorOptions options) => _options = options;

    public string ExtractorId => _options.ExtractorId;
    public string IrVersion => _options.IrVersion;

    /// <summary>Diagnostic code carried by a passed-through stderr line.</summary>
    public const string StderrDiagnosticCode = "W_EXTRACTOR_STDERR";

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct)
    {
        if (IrVersion != IrValidator.SupportedIrVersion)
        {
            throw new IrSchemaException(
                $"extractor '{ExtractorId}' declares IR version '{IrVersion}', " +
                $"this core supports '{IrValidator.SupportedIrVersion}'.");
        }

        return Task.FromResult(new ExtractionResult(Stream(request, ct)));
    }

    private async IAsyncEnumerable<IrRecord> Stream(
        ExtractionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FileName,
            WorkingDirectory = _options.WorkingDirectory ?? request.ProjectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in _options.ResolveArguments(request.ProjectRoot, request.Profile.Id))
            startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in _options.Environment)
            startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo };

        var stderr = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.Add(e.Data);
        };

        try
        {
            if (!process.Start())
                throw new IrSchemaException($"extractor '{ExtractorId}' could not be started ({_options.FileName}).");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IrSchemaException(
                $"extractor '{ExtractorId}' could not be started ({_options.FileName}): {ex.Message}", inner: ex);
        }

        process.BeginErrorReadLine();
        request.Progress.Report(new ProgressEvent("extract", ExtractorId));

        var lineNumber = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var record = IrJson.ReadLine(line, lineNumber);
            var problems = IrValidator.Validate(record);
            if (problems.Count > 0)
            {
                throw new IrSchemaException(
                    $"extractor '{ExtractorId}' emitted a record that is not valid for IR version " +
                    $"{IrValidator.SupportedIrVersion}.",
                    lineNumber, problems);
            }

            yield return record;
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        List<string> stderrSnapshot;
        lock (stderr) stderrSnapshot = stderr.ToList();

        foreach (var message in stderrSnapshot)
            yield return DiagRec.Warning(StderrDiagnosticCode, $"[{ExtractorId}] {message}");

        if (process.ExitCode != 0)
        {
            throw new IrSchemaException(
                $"extractor '{ExtractorId}' exited with code {process.ExitCode} after {lineNumber} record(s). " +
                $"Last stderr: {(stderrSnapshot.Count > 0 ? stderrSnapshot[^1] : "(none)")}");
        }
    }
}
