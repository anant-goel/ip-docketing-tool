using System.IO;
using System.Windows;
using IPDocketing.Core.Data;
using IPDocketing.Core.Services;

namespace IPDocketing.App;

public partial class App : System.Windows.Application
{
    public static AppDbContext Database { get; private set; } = null!;
    public static AuditService Audit { get; private set; } = null!;
    public static MatterService Matters { get; private set; } = null!;
    public static DeadlineService Deadlines { get; private set; } = null!;
    public static RuleEngineService RuleEngine { get; private set; } = null!;
    public static HolidayCalendarService Calendar { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IPDocketing");
        Directory.CreateDirectory(appDataDir);
        var dbPath = Path.Combine(appDataDir, "ipdocketing.db");

        Database = new AppDbContext(dbPath);
        SeedData.EnsureSeeded(Database);

        Audit = new AuditService(Database);
        Matters = new MatterService(Database, Audit);
        Deadlines = new DeadlineService(Database, Audit);
        Calendar = new HolidayCalendarService();
        RuleEngine = new RuleEngineService(Database, Audit, Calendar);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Database?.Dispose();
        base.OnExit(e);
    }
}
