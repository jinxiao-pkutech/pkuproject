namespace ScreenFlow.Core.Storage;

/// <summary>
/// Builds into <c>&lt;db&gt;.building</c> and replaces the real file only once everything succeeded
/// (02 §3). A failed build therefore leaves the previous index intact and queryable, which matters
/// because a rebuild over 9,400 files is not something to lose an existing answer to.
/// </summary>
public sealed class AtomicBuild : IDisposable
{
    public const string BuildingSuffix = ".building";

    private bool _committed;
    private bool _disposed;

    private AtomicBuild(string targetPath, string buildingPath, IndexDb db)
    {
        TargetPath = targetPath;
        BuildingPath = buildingPath;
        Db = db;
    }

    public string TargetPath { get; }
    public string BuildingPath { get; }
    public IndexDb Db { get; }

    /// <summary>Creates the scratch database. Any leftover scratch file from a crashed run is removed.</summary>
    public static AtomicBuild Begin(string targetPath)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var building = fullTarget + BuildingSuffix;

        var directory = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        RemoveDatabaseFiles(building);

        var db = IndexDb.Create(building, DbMode.Build);
        return new AtomicBuild(fullTarget, building, db);
    }

    /// <summary>
    /// Seals the scratch database and moves it onto the target path. Called only after every phase
    /// has succeeded.
    /// </summary>
    public void Commit()
    {
        if (_committed) return;

        Db.SealForQuerying();
        Db.Dispose();

        // WAL and shared-memory side files must not outlive the move, or the replaced database would
        // be opened alongside the previous file's journal.
        CheckpointSideFiles(BuildingPath);
        RemoveDatabaseFiles(TargetPath);
        File.Move(BuildingPath, TargetPath, overwrite: true);

        _committed = true;
    }

    /// <summary>Discards the scratch database, leaving any previous index untouched.</summary>
    public void Abandon()
    {
        if (_committed) return;
        Db.Dispose();
        RemoveDatabaseFiles(BuildingPath);
    }

    private static void CheckpointSideFiles(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sideFile = path + suffix;
            if (File.Exists(sideFile)) TryDelete(sideFile);
        }
    }

    private static void RemoveDatabaseFiles(string path)
    {
        TryDelete(path);
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
    }

    private static void TryDelete(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind by another process; the caller finds out when the create fails.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Not committed means the build threw. Keeping the old database is the whole point.
        if (!_committed) Abandon();
    }
}
