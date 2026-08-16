using System.IO;
using IPDocketing.Core.Data;
using IPDocketing.Core.Services;
using Microsoft.UI.Xaml;

namespace IPDocketing.WinUI;

public partial class App : Application
{
    public static AppDbContext Database { get; private set; } = null!;
    public static AuditService Audit { get; private set; } = null!;
    public static MatterService Matters { get; private set; } = null!;
    public static DeadlineService Deadlines { get; private set; } = null!;
    public static RuleEngineService RuleEngine { get; private set; } = null!;
    public static HolidayCalendarService Calendar { get; private set; } = null!;
    public static BackupService Backups { get; private set; } = null!;
    public static TeamMemberService Team { get; private set; } = null!;
    public static OppositionService Oppositions { get; private set; } = null!;
    public static JournalService Journal { get; private set; } = null!;
    public static WatchService Watch { get; private set; } = null!;
    public static ClientUpdateService ClientUpdates { get; private set; } = null!;

    public static string AppDataDirectory { get; private set; } = null!;
    public static string DatabasePath { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    private Window? _window;

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            LogCrash("Application.UnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Writes to %LocalAppData%\IPDocketing\crash-log.txt. Exists because the
    /// previous UnhandledException handler set e.Handled = true with no
    /// logging at all -- any crash before this was invisible to us, and a
    /// Windows .wer report strips the actual exception/stack for privacy.
    /// If a startup step throws before AppDataDirectory is set, this falls
    /// back to the temp folder so the log still gets written somewhere.
    /// </summary>
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = string.IsNullOrEmpty(AppDataDirectory)
                ? Path.GetTempPath()
                : AppDataDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash-log.txt");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
            File.AppendAllText(path, entry);
        }
        catch
        {
            // Logging must never itself throw during crash handling.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            OnLaunchedCore(args);
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            throw; // still crashes, but now the log has the real cause
        }
    }

    private void OnLaunchedCore(LaunchActivatedEventArgs args)
    {
        AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IPDocketing");
        Directory.CreateDirectory(AppDataDirectory);
        DatabasePath = Path.Combine(AppDataDirectory, "ipdocketing.db");

        var sealedDb = Path.Combine(AppDataDirectory, "ipdocketing.db.enc");
        if (!File.Exists(DatabasePath) && File.Exists(sealedDb))
        {
            try { EncryptionService.DecryptFileTo(sealedDb, DatabasePath); }
            catch { /* start fresh */ }
        }

        Database = new AppDbContext(DatabasePath);
        SeedData.EnsureSeeded(Database);
        Audit = new AuditService(Database);
        Matters = new MatterService(Database, Audit);
        Deadlines = new DeadlineService(Database, Audit);
        Calendar = new HolidayCalendarService();
        RuleEngine = new RuleEngineService(Database, Audit, Calendar);
        Backups = new BackupService(DatabasePath);
        Team = new TeamMemberService(Database);
        Oppositions = new OppositionService(Database, Audit);
        Journal = new JournalService(Database);
        Watch = new WatchService(Database);
        ClientUpdates = new ClientUpdateService(Database);

        MainWindow = new MainWindow();
        _window = MainWindow;
        _window.Closed += (_, _) =>
        {
            try
            {
                if (File.Exists(DatabasePath))
                    EncryptionService.EncryptFileTo(DatabasePath, sealedDb);
            }
            catch { /* ignore */ }
            Backups?.Dispose();
            Database?.Dispose();
        };
        _window.Activate();
    }
}
