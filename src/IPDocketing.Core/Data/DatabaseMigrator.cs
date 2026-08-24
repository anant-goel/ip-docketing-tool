using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace IPDocketing.Core.Data;

/// <summary>
/// Brings the on-disk database up to the current schema WITHOUT destroying it.
///
/// WHAT THIS REPLACES, AND WHY IT HAD TO GO
///
/// Startup used to compare a hand-maintained `schemaVersion` constant against a
/// number in schema-version.txt, and where they differed it DELETED the database
/// file and let EnsureCreated() rebuild an empty one. That is not a migration
/// strategy; it is data loss on a timer. Every model change - one new column -
/// cost the user every matter, deadline, document link, watch alert and
/// dismissal they had entered since the previous change. It also made the
/// codebase progressively harder to improve, because any fix that touched an
/// entity carried that price, so the fixes did not get made.
///
/// EF Core migrations replace it. Each schema change becomes a versioned,
/// ordered script that ALTERS the database in place, and the history of what has
/// been applied lives in the file itself, in __EFMigrationsHistory.
///
/// THE ADOPTION PROBLEM
///
/// Databases already in the field were built by EnsureCreated(), so they have
/// the right tables but NO history table - EF cannot tell them apart from an
/// empty database and would try to create every table again, failing with
/// "table Matters already exists". They have to be BASELINED: told, once, that
/// the initial migration is already applied. That is what
/// <see cref="Prepare"/> does, and it is the only reason this class is more
/// than a call to Migrate().
///
/// NOTHING HERE DELETES A DATABASE. If the schema cannot be brought forward,
/// the failure is reported and the file is left exactly as it was, with a
/// timestamped copy beside it.
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>What Prepare actually did, so the caller can log or show it.</summary>
    public sealed record MigrationOutcome(
        bool Succeeded,
        bool DatabaseWasCreated,
        bool WasBaselined,
        IReadOnlyList<string> Applied,
        string? BackupPath,
        string Summary,
        Exception? Failure = null);

    /// <summary>
    /// Ensures the database at <paramref name="databasePath"/> exists and is at
    /// the current schema.
    ///
    /// Safe to call on every start. On an up-to-date database it does nothing
    /// but one cheap query.
    /// </summary>
    public static MigrationOutcome Prepare(
        AppDbContext context, string databasePath, Action<string>? progress = null)
    {
        var existedBefore = File.Exists(databasePath);
        string? backupPath = null;

        try
        {
            var all = context.Database.GetMigrations().ToList();

            // No migrations exist in the assembly yet - i.e. `dotnet ef
            // migrations add Initial` has not been run. Fall back to
            // create-if-missing so the app still starts, and still never delete
            // anything. Once the initial migration is generated this branch
            // stops being taken.
            if (all.Count == 0)
            {
                progress?.Invoke("Preparing database...");
                context.Database.EnsureCreated();

                return new MigrationOutcome(
                    true, !existedBefore, false, Array.Empty<string>(), null,
                    existedBefore
                        ? "No migrations are present, so the existing database was left untouched."
                        : "No migrations are present, so the database was created from the current model.");
            }

            if (!existedBefore)
            {
                progress?.Invoke("Creating database...");
                context.Database.Migrate();

                return new MigrationOutcome(
                    true, true, false, all, null,
                    $"Created a new database at the current schema ({all.Count} migration(s) applied).");
            }

            // From here on there is real data on disk. Copy it first: every
            // path below either succeeds or restores.
            backupPath = BackUp(databasePath);

            var baselined = false;

            if (!HasMigrationsHistory(context))
            {
                // Built by EnsureCreated. Its tables already match the initial
                // migration, so record that migration as applied instead of
                // running it.
                progress?.Invoke("Adopting the existing database into version control...");
                Baseline(context, all[0]);
                baselined = true;
            }

            var pending = context.Database.GetPendingMigrations().ToList();

            if (pending.Count > 0)
            {
                progress?.Invoke($"Updating database schema ({pending.Count} change(s))...");
                context.Database.Migrate();
            }

            var summary = (baselined, pending.Count) switch
            {
                (true, 0) => "The existing database was adopted into migration control. No schema changes were needed.",
                (true, _) => $"The existing database was adopted into migration control and {pending.Count} schema change(s) were applied. No data was removed.",
                (false, 0) => "The database schema is already current.",
                (false, _) => $"{pending.Count} schema change(s) were applied in place. No data was removed.",
            };

            return new MigrationOutcome(true, false, baselined, pending, backupPath, summary);
        }
        catch (Exception ex)
        {
            // Put the database back the way it was. A half-migrated file is
            // worse than an old one, because the next start would see a history
            // table that does not describe what is actually there.
            var restored = TryRestore(backupPath, databasePath);

            return new MigrationOutcome(
                false, false, false, Array.Empty<string>(), backupPath,
                "The database schema could not be updated" +
                (restored ? " and the database was restored from the copy taken beforehand." : ".") +
                $" Nothing was deleted. Error: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// True when the file carries EF's migration history table - i.e. it was
    /// created or updated by migrations rather than by EnsureCreated.
    /// </summary>
    private static bool HasMigrationsHistory(AppDbContext context)
    {
        try
        {
            // Asking the history repository directly is more reliable than
            // querying sqlite_master, because it knows the table name EF is
            // actually configured to use.
            var history = context.GetService<IHistoryRepository>();
            return history.Exists();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Records <paramref name="migrationId"/> as already applied, creating the
    /// history table if necessary. The migration's Up() is deliberately NOT
    /// run - the tables it would create are already there.
    /// </summary>
    private static void Baseline(AppDbContext context, string migrationId)
    {
        var history = context.GetService<IHistoryRepository>();

        if (!history.Exists())
            context.Database.ExecuteSqlRaw(history.GetCreateScript());

        context.Database.ExecuteSqlRaw(
            history.GetInsertScript(new HistoryRow(migrationId, ProductInfo.GetVersion())));
    }

    /// <summary>
    /// A timestamped copy beside the database, kept whatever happens. Distinct
    /// from the app's encrypted backups on purpose: this one has to be readable
    /// by a plain file copy if a migration goes wrong at 6pm on a Friday.
    /// </summary>
    private static string? BackUp(string databasePath)
    {
        try
        {
            var directory = Path.Combine(
                Path.GetDirectoryName(databasePath) ?? ".", "PreMigrationBackups");

            Directory.CreateDirectory(directory);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var target = Path.Combine(
                directory, $"{Path.GetFileNameWithoutExtension(databasePath)}_{stamp}.db");

            File.Copy(databasePath, target, overwrite: true);

            // SQLite keeps recent writes in a side file; a copy without it can
            // be missing the last transactions.
            CopySidecar(databasePath, target, "-wal");
            CopySidecar(databasePath, target, "-shm");

            PruneOldBackups(directory);
            return target;
        }
        catch
        {
            // A failed backup must not stop a legitimate migration, but the
            // caller is told there is no backup path.
            return null;
        }
    }

    private static void CopySidecar(string databasePath, string target, string suffix)
    {
        var source = databasePath + suffix;
        if (File.Exists(source)) File.Copy(source, target + suffix, overwrite: true);
    }

    /// <summary>Keeps the ten most recent pre-migration copies.</summary>
    private static void PruneOldBackups(string directory)
    {
        try
        {
            var stale = new DirectoryInfo(directory)
                .GetFiles("*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(10)
                .ToList();

            foreach (var file in stale)
            {
                foreach (var suffix in new[] { "-wal", "-shm" })
                    if (File.Exists(file.FullName + suffix)) File.Delete(file.FullName + suffix);

                file.Delete();
            }
        }
        catch
        {
            // Housekeeping only.
        }
    }

    private static bool TryRestore(string? backupPath, string databasePath)
    {
        if (backupPath is null || !File.Exists(backupPath)) return false;

        try
        {
            File.Copy(backupPath, databasePath, overwrite: true);

            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var source = backupPath + suffix;
                if (File.Exists(source)) File.Copy(source, databasePath + suffix, overwrite: true);
                else if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
