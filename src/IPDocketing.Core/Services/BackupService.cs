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
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

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
