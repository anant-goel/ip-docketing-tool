using IPDocketing.Core.Data;
using IPDocketing.Core.Models;

namespace IPDocketing.Core.Services;

public class TeamMemberService
{
    private readonly AppDbContext _db;

    public TeamMemberService(AppDbContext db)
    {
        _db = db;
    }

    public List<TeamMember> GetAll() =>
        _db.TeamMembers.OrderBy(t => t.Name).ToList();

    public List<TeamMember> GetActive() =>
        _db.TeamMembers.Where(t => t.IsActive).OrderBy(t => t.Name).ToList();

    public TeamMember Add(TeamMember member)
    {
        _db.TeamMembers.Add(member);
        _db.SaveChanges();
        return member;
    }

    public void Update(TeamMember member)
    {
        _db.TeamMembers.Update(member);
        _db.SaveChanges();
    }
}
