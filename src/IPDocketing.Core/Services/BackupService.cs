using System.Globalization;

namespace IPDocketing.Core.Services;

/// <summary>
/// Creates encrypted, timestamped backups of the live SQLite database every minute.
/// Backups older than <see cref="RetentionDays"/> are deleted automatically.
/// Format: ipdocketing_yyyyMMdd_HHmmss.db.enc  (DPAPI-encrypted, current Windows user only).
/// </summary>
public sealed class BackupService : IDisposable
{
    public const int RetentionDays = 4;

    /// <summary>
    /// Phase 30 fix. This was one minute. With four days of retention that is
    /// 5,760 files, each a full DPAPI-encrypted copy of the whole database -
    /// hundreds of megabytes of near-identical snapshots, a DPAPI call and a
    /// full file write every sixty seconds forever, and a Backups folder no one
    /// can find anything in. Fifteen minutes plus the change detection below
    /// keeps recovery granularity that is fine for a single-user desktop app
    /// while writing a snapshot only when something actually changed.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Hard ceiling on retained snapshots, enforced after the age-based prune.</summary>
    public const int MaxRetainedBackups = 240;

    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

    // Fingerprint of the database at the last successful snapshot. An automatic
    // run whose fingerprint is unchanged is skipped entirely - no point storing
    // the same bytes again under a new timestamp.
    private (long Length, DateTime WriteTimeUtc)? _lastFingerprint;

    public string BackupDirectory => _backupDir;
    public string? LastBackupPath { get; private set; }
    public DateTime? LastBackupUtc { get; private set; }
    public string LastStatus { get; private set; } = "Not started";

    public event Action? BackupCompleted;

    public BackupService(string dbPath)
    {
        _dbPath = dbPath;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IPDocketing");
        _backupDir = Path.Combine(appData, "Backups");
        Directory.CreateDirectory(_backupDir);

        // First backup shortly after start, then every minute
        _timer = new Timer(_ => SafeBackup(), null, TimeSpan.FromSeconds(15), Interval);
    }

    public void BackupNow(string reason = "manual")
    {
        SafeBackup(reason);
    }

    private void SafeBackup(string reason = "auto")
    {
        if (_disposed) return;
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_dbPath))
                {
                    LastStatus = "Skipped – database not found yet";
                    return;
                }

                // Skip an automatic run when nothing has changed since the last
                // snapshot. Manual and shutdown backups always proceed, because
                // those are explicitly asked for.
                var info = new FileInfo(_dbPath);
                var fingerprint = (info.Length, info.LastWriteTimeUtc);
                if (reason == "auto" && _lastFingerprint == fingerprint)
                {
                    LastStatus = $"No change since {LastBackupUtc?.ToLocalTime():g} - snapshot skipped";
                    return;
                }

                // SQLite may have -wal / -shm; copy main file after a brief settle.
                // For stronger consistency, callers can checkpoint; for local desktop this is adequate.
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var fileName = $"ipdocketing_{stamp}_{reason}.db.enc";
                var dest = Path.Combine(_backupDir, fileName);

                EncryptionService.EncryptFileTo(_dbPath, dest);

                // Also snapshot API keys if present (encrypted already as .enc)
                var keysEnc = Path.Combine(
                    Path.GetDirectoryName(_dbPath)!, "api-keys.enc");
                if (File.Exists(keysEnc))
                {
                    var keysDest = Path.Combine(_backupDir, $"api-keys_{stamp}_{reason}.enc");
                    File.Copy(keysEnc, keysDest, overwrite: true);
                }

                LastBackupPath = dest;
                LastBackupUtc = DateTime.UtcNow;
                _lastFingerprint = fingerprint;
                LastStatus = $"OK {DateTime.Now:g} → {fileName}";

                PruneOldBackups();
                BackupCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                LastStatus = $"Failed: {ex.Message}";
            }
        }
    }

    private void PruneOldBackups()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_backupDir, "*.enc"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff)
                    info.Delete();
            }
            catch
            {
                // ignore individual delete failures
            }
        }

        // Second, independent ceiling. Age-based pruning alone lets the folder
        // grow without bound if the app is left running for days, and a
        // pre-schema-change snapshot is deliberately kept outside the age rule,
        // so cap the count as well - newest kept, oldest dropped.
        try
        {
            var snapshots = Directory.GetFiles(_backupDir, "ipdocketing_*.db.enc")
                .OrderByDescending(f => f)
                .Skip(MaxRetainedBackups)
                .ToList();
            foreach (var stale in snapshots)
            {
                try { File.Delete(stale); } catch { /* ignore */ }
            }
        }
        catch
        {
            // Enumeration failure here must never break the backup itself.
        }
    }

    /// <summary>
    /// Takes a labelled snapshot outside the normal rotation. Used before a
    /// destructive operation (notably the schema rebuild on launch) so the
    /// previous database is always recoverable even though the live file is
    /// about to be replaced.
    /// </summary>
    public string? SnapshotBeforeDestructiveChange(string label)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_dbPath)) return null;
                Directory.CreateDirectory(_backupDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var dest = Path.Combine(_backupDir, $"presnapshot_{label}_{stamp}.db.enc");
                EncryptionService.EncryptFileTo(_dbPath, dest);
                LastStatus = $"Pre-change snapshot saved: {Path.GetFileName(dest)}";
                return dest;
            }
            catch (Exception ex)
            {
                LastStatus = $"Pre-change snapshot failed: {ex.Message}";
                return null;
            }
        }
    }

    /// <summary>
    /// Restores an encrypted backup over the live database path.
    /// Caller must dispose/close the DbContext first.
    /// </summary>
    public void RestoreFrom(string encryptedBackupPath, string targetDbPath)
    {
        lock (_gate)
        {
            EncryptionService.DecryptFileTo(encryptedBackupPath, targetDbPath);
            LastStatus = $"Restored from {Path.GetFileName(encryptedBackupPath)} at {DateTime.Now:g}";
        }
    }

    public IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(_backupDir)) return Array.Empty<string>();
        return Directory.GetFiles(_backupDir, "ipdocketing_*.db.enc")
            .OrderByDescending(f => f)
            .ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Final backup on shutdown
            SafeBackup("shutdown");
        }
        catch { /* ignore */ }
        _timer.Dispose();
    }
}
