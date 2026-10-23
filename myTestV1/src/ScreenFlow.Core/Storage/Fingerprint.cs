using System.Globalization;
using ScreenFlow.Core.Ir;

namespace ScreenFlow.Core.Storage;

/// <summary>
/// Staleness detection (02 §7.6). Recorded at build time and recomputed at query time from file
/// metadata only — count, total size, newest modification time, plus a hash of the configuration.
/// <para>
/// Content is deliberately not read: re-hashing thousands of files on every query would make
/// <c>screen get</c> cost as much as a build. The trade-off is that an edit which preserves size
/// and timestamp goes unnoticed, so the result is reported as <c>stale: true</c> or
/// <c>stale: false</c> and never as a guarantee.
/// </para>
/// </summary>
public static class Fingerprint
{
    public sealed record Value(int Count, long TotalBytes, long MaxMtimeTicks, string ConfigHash)
    {
        /// <summary>The stored form: four fields, pipe separated.</summary>
        public override string ToString()
            => string.Join("|",
                Count.ToString(CultureInfo.InvariantCulture),
                TotalBytes.ToString(CultureInfo.InvariantCulture),
                MaxMtimeTicks.ToString(CultureInfo.InvariantCulture),
                ConfigHash);

        public static Value? TryParse(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var parts = text.Split('|');
            if (parts.Length != 4) return null;
            if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var count)) return null;
            if (!long.TryParse(parts[1], CultureInfo.InvariantCulture, out var bytes)) return null;
            if (!long.TryParse(parts[2], CultureInfo.InvariantCulture, out var ticks)) return null;
            return new Value(count, bytes, ticks, parts[3]);
        }
    }

    /// <summary>
    /// Computes the fingerprint of the analysed inputs. <paramref name="files"/> are the source and
    /// resource files the selected response files pulled in; a file that has disappeared is counted
    /// as missing rather than skipped, so a deletion changes the fingerprint.
    /// </summary>
    public static Value Compute(IEnumerable<string> files, string configDirectory)
    {
        var count = 0;
        long totalBytes = 0;
        long maxTicks = 0;

        foreach (var path in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            count++;
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                // A missing input still has to move the fingerprint, so it contributes a marker
                // rather than nothing at all.
                maxTicks = Math.Max(maxTicks, 1);
                continue;
            }

            totalBytes += info.Length;
            maxTicks = Math.Max(maxTicks, info.LastWriteTimeUtc.Ticks);
        }

        return new Value(count, totalBytes, maxTicks, HashConfig(configDirectory));
    }

    /// <summary>
    /// Hash over the configuration files' contents. Configuration is small and changing it changes
    /// every derived fact, so here the content is read.
    /// </summary>
    public static string HashConfig(string configDirectory)
    {
        if (!Directory.Exists(configDirectory)) return Ids.Sha256Hex(string.Empty);

        var builder = new System.Text.StringBuilder();
        foreach (var file in Directory
                     .EnumerateFiles(configDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            builder.Append(Path.GetFileName(file)).Append('=');
            builder.Append(Ids.Sha256Hex(File.ReadAllBytes(file))).Append('\n');
        }

        return Ids.Sha256Hex(builder.ToString());
    }

    /// <summary>True when the two fingerprints describe the same inputs.</summary>
    public static bool Matches(Value? stored, Value current)
        => stored is not null
           && stored.Count == current.Count
           && stored.TotalBytes == current.TotalBytes
           && stored.MaxMtimeTicks == current.MaxMtimeTicks
           && string.Equals(stored.ConfigHash, current.ConfigHash, StringComparison.Ordinal);

    public static bool Matches(string? storedText, Value current)
        => Matches(Value.TryParse(storedText), current);

    /// <summary>
    /// What changed, for the diagnostic that accompanies <c>stale: true</c>. An empty result means
    /// the fingerprints agree.
    /// </summary>
    public static IReadOnlyList<string> Differences(Value? stored, Value current)
    {
        if (stored is null) return new[] { "no fingerprint was recorded at build time" };

        var differences = new List<string>();
        if (stored.Count != current.Count)
            differences.Add($"file count {stored.Count} -> {current.Count}");
        if (stored.TotalBytes != current.TotalBytes)
            differences.Add($"total size {stored.TotalBytes} -> {current.TotalBytes}");
        if (stored.MaxMtimeTicks != current.MaxMtimeTicks)
            differences.Add("newest modification time changed");
        if (!string.Equals(stored.ConfigHash, current.ConfigHash, StringComparison.Ordinal))
            differences.Add("configuration changed");
        return differences;
    }
}
