namespace ScreenFlow.Core.Tests.ConfigTests;

/// <summary>
/// Locates the repository's own <c>config/</c> so the tests check the files that actually ship,
/// not a copy. The walk looks for <c>ScreenFlow.sln</c> rather than counting directory levels, so
/// it survives a change of build output layout.
/// </summary>
internal static class RepoLayout
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ConfigDirectory => Path.Combine(RepoRoot, "config");

    public static string ProfilesPath => Path.Combine(ConfigDirectory, "profiles.json");

    public static string DomainPath => Path.Combine(ConfigDirectory, "unitypos.json");

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ScreenFlow.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException(
            $"Could not find ScreenFlow.sln above {AppContext.BaseDirectory}; the test cannot locate config/.");
    }
}

/// <summary>A throwaway config file, removed when the test finishes.</summary>
internal sealed class TempConfigFile : IDisposable
{
    public TempConfigFile(string json, string extension = ".json")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "screenflow-test-" + Guid.NewGuid().ToString("n") + extension);
        File.WriteAllText(Path, json);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch (IOException)
        {
            // A temp file left behind must not fail a test.
        }
    }
}
