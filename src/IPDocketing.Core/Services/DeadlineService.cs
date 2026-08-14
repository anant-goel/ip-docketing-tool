using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

public class DeadlineService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public DeadlineService(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public List<Deadline> GetAll() =>
        _db.Deadlines.Include(d => d.Matter).Include(d => d.CountryRule)
            .OrderBy(d => d.DueDate).ToList();

    public List<Deadline> GetUpcoming(int days)
    {
        var today = DateTime.Now.Date;
        return GetAll().Where(d =>
        {
            if (d.Status == DeadlineStatus.Completed) return false;
            var daysLeft = (d.DueDate.Date - today).Days;
            return daysLeft >= 0 && daysLeft <= days;
        }).ToList();
    }

    public List<Deadline> GetOverdue() =>
        GetAll().Where(d => d.Status != DeadlineStatus.Completed && d.DueDate.Date < DateTime.Now.Date).ToList();

    public void MarkComplete(int deadlineId)
    {
        var deadline = _db.Deadlines.Find(deadlineId);
        if (deadline is null) return;

        deadline.Status = DeadlineStatus.Completed;
        deadline.CompletedDate = DateTime.UtcNow;
        _db.SaveChanges();

        _audit.Log("Complete", "Deadline", deadline.Id, $"Marked complete on {deadline.CompletedDate:yyyy-MM-dd}.");
    }

    public Deadline AddManual(Deadline deadline)
    {
        _db.Deadlines.Add(deadline);
        _db.SaveChanges();
        _audit.Log("Create", "Deadline", deadline.Id, $"Manually added: {deadline.Description}, due {deadline.DueDate:yyyy-MM-dd}.");
        return deadline;
    }
}
