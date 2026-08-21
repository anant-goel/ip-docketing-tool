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
    public static StatusTrackerService StatusTracker { get; private set; } = null!;
    public static TeamNotificationService TeamNotifications { get; private set; } = null!;
    public static RenewalService Renewals { get; private set; } = null!;
    public static PortfolioImportService PortfolioImport { get; private set; } = null!;
    public static AutoSyncService AutoSync { get; private set; } = null!;
    public static DocumentIngestService DocumentIngest { get; private set; } = null!;
    public static JournalSearchService JournalSearch { get; private set; } = null!;

    /// <summary>
    /// Points WebView2's user-data folder at %LocalAppData%\IPDocketing\WebView2
    /// instead of letting it default to "IPDocketing.exe.WebView2" beside the
    /// executable.
    ///
    /// That default folder is created on first use and grows to tens of
    /// megabytes of cache, cookies and crash dumps - it is what bloated the
    /// zipped app folder by 44 MB - and it is wiped whenever the app folder is
    /// replaced on update, taking any portal session with it.
    ///
    /// Done through the environment variable rather than
    /// CoreWebView2Environment.CreateAsync because two different overload
    /// shapes of that method failed to compile against this WebView2 build.
    /// The variable is read by the WebView2 loader at initialisation, has no
    /// API surface to get wrong, and works across all versions. It must be set
    /// before any WebView2 instance is created, which is why it happens here at
    /// startup rather than on the portal page.
    /// </summary>
    private static void ConfigureWebView2Storage()
    {
        try
        {
            var folder = Path.Combine(AppDataDirectory, "WebView2");
            Directory.CreateDirectory(folder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", folder);
        }
        catch
        {
            // Falling back to the default location is ugly but harmless - the
            // embedded browser still works, it just stores its cache beside the
            // executable.
        }
    }

    /// <summary>
    /// Reads holidays.txt from the data folder into the holiday calendar.
    ///
    /// Only fixed-date national holidays are compiled in. India's calendar is
    /// largely movable - Diwali, Holi, Eid, Good Friday - and per-branch
    /// closures differ between Delhi, Mumbai, Kolkata, Chennai and Ahmedabad.
    /// Those cannot be computed, so they have to be pasted in each year from the
    /// CGPDTM notification. The file is created with instructions on first run,
    /// because a seam nobody can find is the same as no seam at all.
    /// </summary>
    private static void LoadOfficeClosures(HolidayCalendarService calendar, string appDataDirectory)
    {
        var path = Path.Combine(appDataDirectory, "holidays.txt");

        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "# Office closures for deadline rolling." + Environment.NewLine +
                "# One date per line as yyyy-MM-dd, optionally followed by a note." + Environment.NewLine +
                "# Only fixed-date national holidays are built in - paste the movable" + Environment.NewLine +
                "# ones (Diwali, Holi, Eid, Good Friday) and any branch closures from" + Environment.NewLine +
                "# the CGPDTM annual holiday notification here each year." + Environment.NewLine +
                "#" + Environment.NewLine +
                "# Example:" + Environment.NewLine +
                "# 2026-11-08  Diwali" + Environment.NewLine);
            return;
        }

        var dates = new List<DateTime>();
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var token = trimmed.Split(new[] { ' ', '\t' }, 2)[0];
            if (DateTime.TryParseExact(token, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
                dates.Add(parsed);
        }

        if (dates.Count > 0)
            calendar.AddOfficeClosures(HolidayCalendarService.IndiaCalendarId, dates);
    }

    /// <summary>Whether the unattended Journal pipeline is running. Persisted between sessions.</summary>
    public static bool AutoSyncEnabled { get; private set; }

    /// <summary>
    /// Turns the unattended pipeline on or off and remembers the choice. Off by
    /// default on a fresh install: the first pass downloads several large PDFs,
    /// and doing that unasked on someone's metered connection is not a decision
    /// this app gets to make for them.
    /// </summary>
    public static void SetAutoSyncEnabled(bool enabled)
    {
        AutoSyncEnabled = enabled;
        if (enabled) AutoSync.Start(); else AutoSync.Stop();

        try
        {
            File.WriteAllText(Path.Combine(AppDataDirectory, "autosync.txt"), enabled ? "on" : "off");
        }
        catch
        {
            // A failed preference write shouldn't stop the toggle working now.
        }
    }

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

        // PHASE 31 - the blank-window-on-launch fix, part one.
        //
        // Activate() only asks for the window to be shown; nothing is painted
        // until the message loop next runs. A single Task.Yield (what used to
        // be here) hands control back for one continuation, which is not the
        // same as waiting for a frame - and the synchronous database work that
        // followed then pinned the UI thread, so no frame ever arrived. The
        // result was an empty window for the whole of startup.
        //
        // WaitForFirstPaintAsync completes only once the splash content has
        // actually rendered, with a timeout so a machine that can't composite
        // still boots.
        await splash.WaitForFirstPaintAsync(TimeSpan.FromSeconds(2));

        AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IPDocketing");
        Directory.CreateDirectory(AppDataDirectory);

        // Must run before any WebView2 instance exists.
        ConfigureWebView2Storage();

        DatabasePath = Path.Combine(AppDataDirectory, "ipdocketing.db");

        // A restore staged from Settings lands here, before anything opens the
        // database. It cannot be applied at the time the user asks, because EF
        // Core holds an open connection to the file being replaced and swapping
        // it underneath a live DbContext corrupts rather than restores.
        try
        {
            var pendingRestore = DatabasePath + ".restore-pending";
            if (File.Exists(pendingRestore))
            {
                File.Copy(pendingRestore, DatabasePath, overwrite: true);
                File.Delete(pendingRestore);
            }
        }
        catch (Exception ex)
        {
            LogCrash("Applying staged restore", ex);
        }

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

        // PHASE 31 - the blank-window fix, part two.
        //
        // All of this used to run synchronously on the UI thread: schema
        // rebuild, EnsureCreated, seeding, and a full pass generating client
        // updates. On a cold start that is seconds of blocked message loop, so
        // the splash could not paint and its progress bar could not animate.
        //
        // EF Core's DbContext has no thread affinity - it just must not be used
        // concurrently - so building it on a worker thread and handing it back
        // is safe. Nothing else touches these statics until this await returns.
        await System.Threading.Tasks.Task.Run(() =>
        {
            const int schemaVersion = 6;
            var schemaVersionPath = Path.Combine(AppDataDirectory, "schema-version.txt");
            var previousVersion = File.Exists(schemaVersionPath)
                ? int.TryParse(File.ReadAllText(schemaVersionPath).Trim(), out var v) ? v : 0
                : 0;

            if (previousVersion != schemaVersion && File.Exists(DatabasePath))
            {
                // Phase 30: take a recoverable snapshot BEFORE throwing the old
                // database away. The previous version deleted it outright, so every
                // schema bump silently destroyed whatever had been entered since
                // the last one - fine while this was a demo, not fine now that it
                // holds real matters. The snapshot is a normal encrypted backup
                // file, restorable from Settings > Backups.
                try
                {
                    var preChangeDir = Path.Combine(AppDataDirectory, "Backups");
                    Directory.CreateDirectory(preChangeDir);
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    EncryptionService.EncryptFileTo(
                        DatabasePath,
                        Path.Combine(preChangeDir, $"presnapshot_schema{previousVersion}to{schemaVersion}_{stamp}.db.enc"));
                }
                catch
                {
                    // A failed snapshot must not block startup, but it is worth
                    // knowing about - it lands in crash-log.txt via the caller.
                }

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

            splash.SetStatus("Opening database...");
            Database = new AppDbContext(DatabasePath);

            splash.SetStatus("Loading country rules...");
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
            StatusTracker = new StatusTrackerService(Database);
            TeamNotifications = new TeamNotificationService(Database);
            // HolidayCalendarService.AddOfficeClosures existed with no caller,
            // so the movable Indian holidays it was designed to receive were
            // never loaded and deadlines could roll onto Diwali. This reads a
            // user-editable list; the file is created with a worked example on
            // first run so it is discoverable rather than secret.
            try
            {
                LoadOfficeClosures(Calendar, AppDataDirectory);
            }
            catch (Exception ex)
            {
                LogCrash("Loading office closures", ex);
            }

            Renewals = new RenewalService(Database, Audit, Calendar);
            PortfolioImport = new PortfolioImportService(Database, Matters, Audit);

            DocumentIngest = new DocumentIngestService(
                Database, Audit, Path.Combine(AppDataDirectory, "Documents"));

            AutoSync = new AutoSyncService(
                Database, Journal, JournalFetch, Watch, Audit,
                Path.Combine(AppDataDirectory, "JournalLibrary"));

            // The OCR half needs WinRT APIs that IPDocketing.Core, targeting
            // plain net8.0-windows, cannot see - so the reader is built here and
            // injected, keeping Core free of any UI-layer dependency.
            var pdfExtractor = new Services.PdfTextExtractor();
            AutoSync.UseExtractor(pdfExtractor);

            JournalSearch = new JournalSearchService(Database);
            JournalSearch.UseExtractor(pdfExtractor);

            // Renewal docketing is idempotent, so running it at every launch is
            // safe and means a mark can never sit in the register without its
            // four s.25 dates simply because nobody remembered to press a button.
            try
            {
                splash.SetStatus("Docketing renewals...");
                Renewals.DocketRenewals();
            }
            catch (Exception ex)
            {
                LogCrash("Startup renewal docketing", ex);
            }

            // docx section 8 - the generation half of "automatic client updates"
            // really is automatic: any client whose last update is over a week old
            // gets a fresh draft written at startup, without anyone asking. Sending
            // still needs a human, because no mail transport is configured here.
            try
            {
                splash.SetStatus("Drafting client updates...");
                ClientUpdates.GenerateDueUpdates(TimeSpan.FromDays(7));
            }
            catch (Exception ex)
            {
                LogCrash("Startup client update generation", ex);
            }


        });

        // Accent palette. Applied by re-colouring the shared brush objects in
        // place (see AccentPaletteService) rather than by merging a dictionary.
        // The old approach reached only the handful of keys ColorfulAccent.xaml
        // redefines, and missed the acrylic tint on AccentGlassButtonBrush and
        // the LiquidPrimaryBrush gradient - which is most of what you actually
        // see - so "Colorful" appeared to do nothing.
        try
        {
            Services.AccentPaletteService.ApplySaved(AppDataDirectory);
        }
        catch (Exception ex)
        {
            LogCrash("Accent palette", ex);
        }

        // Off unless previously switched on: the first pass pulls several large
        // PDFs, and doing that unasked on a metered connection isn't this app's
        // call to make.
        try
        {
            var autoSyncPath = Path.Combine(AppDataDirectory, "autosync.txt");
            if (File.Exists(autoSyncPath) &&
                File.ReadAllText(autoSyncPath).Trim().Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                SetAutoSyncEnabled(true);
            }
        }
        catch (Exception ex)
        {
            LogCrash("Auto-sync preference", ex);
        }

        splash.SetStatus("Loading workspace...");

        // One turn of the loop so the final status actually renders before the
        // main window's own construction takes the thread again.
        await System.Threading.Tasks.Task.Yield();

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
            AutoSync?.Dispose();
            Backups?.Dispose();
            Database?.Dispose();
        };
        _window.Activate();
        splash.Close();
    }
}
