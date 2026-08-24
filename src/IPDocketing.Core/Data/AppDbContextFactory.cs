using Microsoft.EntityFrameworkCore.Design;

namespace IPDocketing.Core.Data;

/// <summary>
/// Lets the `dotnet ef` tooling construct an AppDbContext at design time.
///
/// WHY THIS FILE IS NECESSARY
///
/// AppDbContext takes a database path in its constructor and has no
/// parameterless one, so `dotnet ef migrations add` cannot create an instance
/// and fails with "Unable to create a DbContext of type 'AppDbContext'". EF
/// looks for exactly this interface before giving up.
///
/// The path below is never opened. Generating a migration compares the C# model
/// against the previous MODEL SNAPSHOT, not against any database - the file only
/// has to be somewhere the SQLite provider will accept as a connection string.
/// It is deliberately a throwaway name in the temp folder so that running the
/// tooling can never touch the real docket.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(Path.Combine(Path.GetTempPath(), "ipdocketing-design-time.db"));
}
