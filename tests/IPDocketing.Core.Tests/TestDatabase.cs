using IPDocketing.Core.Data;

namespace IPDocketing.Core.Tests;

/// <summary>
/// A throwaway SQLite database on disk, created fresh per test and deleted
/// afterwards.
///
/// Deliberately a REAL SQLite file rather than EF Core's in-memory provider.
/// The in-memory provider does not translate LINQ to SQL — it evaluates against
/// objects — so it happily runs queries that throw against a real database.
/// That distinction is not academic here: the watch was broken for exactly that
/// reason (a projection EF could not translate), and an in-memory test would
/// have passed while the feature crashed in front of the user.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _path;

    public AppDbContext Db { get; }

    public TestDatabase()
    {
        _path = Path.Combine(Path.GetTempPath(), $"ipdocketing_test_{Guid.NewGuid():N}.db");
        Db = new AppDbContext(_path);
        Db.Database.EnsureCreated();
    }

    /// <summary>
    /// A second context over the same file, for asserting that something was
    /// actually persisted rather than merely sitting in the first context's
    /// change tracker.
    /// </summary>
    public AppDbContext Reopen() => new(_path);

    public void Dispose()
    {
        try
        {
            Db.Dispose();
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // A leftover temp file is not worth failing a test run over.
        }
    }
}
