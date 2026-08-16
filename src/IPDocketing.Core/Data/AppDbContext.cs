using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Data;

public class AppDbContext : DbContext
{
    private readonly string _dbPath;

    public DbSet<Matter> Matters => Set<Matter>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Deadline> Deadlines => Set<Deadline>();
    public DbSet<CountryRule> CountryRules => Set<CountryRule>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<PtoNotice> PtoNotices => Set<PtoNotice>();
    public DbSet<UserAction> UserActions => Set<UserAction>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Opposition> Oppositions => Set<Opposition>();
    public DbSet<JournalIssue> JournalIssues => Set<JournalIssue>();
    public DbSet<WatchAlert> WatchAlerts => Set<WatchAlert>();
    public DbSet<ClientUpdateLog> ClientUpdateLogs => Set<ClientUpdateLog>();

    public AppDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Matter>()
            .HasOne(m => m.ParentMatter)
            .WithMany(m => m.ChildMatters)
            .HasForeignKey(m => m.ParentMatterId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Event>()
            .HasOne(e => e.Matter)
            .WithMany(m => m.Events)
            .HasForeignKey(e => e.MatterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Deadline>()
            .HasOne(d => d.Matter)
            .WithMany(m => m.Deadlines)
            .HasForeignKey(d => d.MatterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Deadline>()
            .HasOne(d => d.Event)
            .WithMany(e => e.Deadlines)
            .HasForeignKey(d => d.EventId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Deadline>()
            .HasOne(d => d.CountryRule)
            .WithMany()
            .HasForeignKey(d => d.CountryRuleId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Document>()
            .HasOne(doc => doc.Matter)
            .WithMany(m => m.Documents)
            .HasForeignKey(doc => doc.MatterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Document>()
            .HasOne(doc => doc.Opposition)
            .WithMany(o => o.Documents)
            .HasForeignKey(doc => doc.OppositionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Matter>()
            .HasOne(m => m.AssignedTo)
            .WithMany()
            .HasForeignKey(m => m.AssignedToId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Opposition>()
            .HasOne(o => o.Matter)
            .WithMany()
            .HasForeignKey(o => o.MatterId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Opposition>()
            .HasOne(o => o.AssignedTo)
            .WithMany()
            .HasForeignKey(o => o.AssignedToId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<WatchAlert>()
            .HasOne(w => w.JournalIssue)
            .WithMany()
            .HasForeignKey(w => w.JournalIssueId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<WatchAlert>()
            .HasOne(w => w.Matter)
            .WithMany()
            .HasForeignKey(w => w.MatterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PtoNotice>()
            .HasOne(p => p.Matter)
            .WithMany()
            .HasForeignKey(p => p.MatterId)
            .OnDelete(DeleteBehavior.SetNull);

        // Store enums as strings for readability of the local SQLite file.
        modelBuilder.Entity<Matter>().Property(m => m.Type).HasConversion<string>();
        modelBuilder.Entity<Matter>().Property(m => m.Status).HasConversion<string>();
        modelBuilder.Entity<Event>().Property(e => e.Type).HasConversion<string>();
        modelBuilder.Entity<Deadline>().Property(d => d.Kind).HasConversion<string>();
        modelBuilder.Entity<Deadline>().Property(d => d.Status).HasConversion<string>();
        modelBuilder.Entity<CountryRule>().Property(c => c.MatterType).HasConversion<string>();
        modelBuilder.Entity<CountryRule>().Property(c => c.TriggerEvent).HasConversion<string>();
        modelBuilder.Entity<CountryRule>().Property(c => c.PeriodUnit).HasConversion<string>();
        modelBuilder.Entity<PtoNotice>().Property(p => p.Source).HasConversion<string>();
        modelBuilder.Entity<Matter>().Property(m => m.MarkType).HasConversion<string>();
        modelBuilder.Entity<Opposition>().Property(o => o.Direction).HasConversion<string>();
        modelBuilder.Entity<Opposition>().Property(o => o.Status).HasConversion<string>();
    }
}
