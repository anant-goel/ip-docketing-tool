using IPDocketing.Core.Data;
using IPDocketing.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IPDocketing.Core.Services;

public class OppositionService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public OppositionService(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public List<Opposition> GetAll() =>
        _db.Oppositions.Include(o => o.Matter).Include(o => o.AssignedTo)
            .OrderByDescending(o => o.CreatedDate).ToList();

    public List<Opposition> GetByDirection(OppositionDirection direction) =>
        GetAll().Where(o => o.Direction == direction).ToList();

    public Opposition? GetById(int id) =>
        _db.Oppositions
            .Include(o => o.Matter)
            .Include(o => o.AssignedTo)
            .Include(o => o.Documents)
            .FirstOrDefault(o => o.Id == id);

    public Opposition Add(Opposition opposition)
    {
        opposition.CreatedDate = DateTime.UtcNow;
        _db.Oppositions.Add(opposition);
        _db.SaveChanges();
        _audit.Log("Create", "Opposition", opposition.Id,
            $"Opposition on {opposition.TrademarkNumber} ({opposition.Direction}) created.");
        return opposition;
    }

    public void Update(Opposition opposition)
    {
        _db.Oppositions.Update(opposition);
        _db.SaveChanges();
        _audit.Log("Update", "Opposition", opposition.Id,
            $"Opposition on {opposition.TrademarkNumber} updated -> {opposition.Status}.");
    }

    public void AssignTo(int oppositionId, int teamMemberId)
    {
        var opposition = _db.Oppositions.Find(oppositionId);
        if (opposition is null) return;
        opposition.AssignedToId = teamMemberId;
        _db.SaveChanges();
        _audit.Log("Assign", "Opposition", oppositionId, $"Assigned to team member {teamMemberId}.");
    }

    public void Delete(int id)
    {
        var opposition = _db.Oppositions.Find(id);
        if (opposition is null) return;
        _db.Oppositions.Remove(opposition);
        _db.SaveChanges();
        _audit.Log("Delete", "Opposition", id, $"Opposition on {opposition.TrademarkNumber} deleted.");
    }
}
