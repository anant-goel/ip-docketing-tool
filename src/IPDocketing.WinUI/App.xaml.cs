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
    public static IndiaPincodeService Pincode { get; private set; } = null!;
    public static GmailOtpService GmailOtp { get; private set; } = null!;
    public static JournalFetchService JournalFetch { get; private set; } = null!;

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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await OnLaunchedCoreAsync(args);
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            throw; // still crashes, but now the log has the real cause
        }
    }

    private async System.Threading.Tasks.Task OnLaunchedCoreAsync(LaunchActivatedEventArgs args)
    {
        var splash = new SplashWindow();
        splash.Activate();
        // Give the dispatcher a chance to actually paint the splash before
        // the synchronous DB/seed work below blocks the UI thread - without
        // this yield, Activate() only requests the window be shown; nothing
        // paints until the message loop gets a turn.
        await System.Threading.Tasks.Task.Yield();

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

        // EnsureCreated() (used below, and in SeedData) only builds the schema
        // the FIRST time a database file doesn't exist - it never alters an
        // already-existing file's schema. Every model change since this file
        // was first created (new columns, new tables) would otherwise be
        // silently missing, causing "no such column" crashes like the
        // AssignedToId one. Bump SchemaVersion whenever a model changes;
        // a mismatch means the on-disk file predates that change, so it's
        // deleted and rebuilt fresh rather than crashing on first query.
        // This does mean local data doesn't survive a schema change while
        // the app has no formal EF migrations - acceptable for now, but
        // worth switching to real migrations before this holds data anyone
        // depends on keeping.
        splash.SetStatus("Preparing database...");
        const int schemaVersion = 2;
        var schemaVersionPath = Path.Combine(AppDataDirectory, "schema-version.txt");
        var previousVersion = File.Exists(schemaVersionPath)
            ? int.TryParse(File.ReadAllText(schemaVersionPath).Trim(), out var v) ? v : 0
            : 0;

        if (previousVersion != schemaVersion && File.Exists(DatabasePath))
        {
            try
            {
                File.Delete(DatabasePath);
                if (File.Exists(sealedDb)) File.Delete(sealedDb);
            }
            catch
            {
                // If we can't delete it, EnsureCreated below will still no-op
                // against the stale file and the same crash will resurface -
                // but we tried, and this shouldn't be fatal on its own.
            }
        }
        File.WriteAllText(schemaVersionPath, schemaVersion.ToString());

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
        Pincode = new IndiaPincodeService();
        GmailOtp = new GmailOtpService(AppDataDirectory);
        JournalFetch = new JournalFetchService();

        // Applied here, before any window/page exists, so every page's
        // StaticResource lookups pick up the override from the start
        // rather than needing a live re-theme (which StaticResource
        // doesn't support - only ThemeResource does, and these accent
        // colors are plain static values shared across Light/Dark).
        try
        {
            var themeSettingPath = Path.Combine(AppDataDirectory, "theme-preference.txt");
            var savedTheme = File.Exists(themeSettingPath) ? File.ReadAllText(themeSettingPath).Trim() : "Dark";
            if (savedTheme == "Colorful")
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("ms-appx:///Themes/ColorfulAccent.xaml")
                });
            }
        }
        catch
        {
            // Falls back to the default blue accent - never worth crashing over.
        }

        splash.SetStatus("Loading workspace...");

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
        splash.Close();
    }
}
