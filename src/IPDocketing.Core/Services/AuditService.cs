using System.Security.Cryptography;
using System.Text;
using IPDocketing.Core.Data;
using IPDocketing.Core.Models;

namespace IPDocketing.Core.Services;

/// <summary>
/// Append-only, hash-chained audit logging. No update/delete methods are
/// exposed on purpose - the audit trail must remain immutable for
/// malpractice / compliance defensibility. Every record's RecordHash is
/// SHA-256(PriorHash + payload), so the full ledger can be re-walked and
/// verified: if any historical row were altered, every hash after it would
/// fail to match.
/// </summary>
public class AuditService
{
    private readonly AppDbContext _db;
    public string CurrentUser { get; set; } = "local.user";

    public AuditService(AppDbContext db)
    {
        _db = db;
    }

    public UserAction Log(string actionType, string entityType, int entityId, string? details = null)
    {
        var priorHash = _db.UserActions
            .OrderByDescending(a => a.Id)
            .Select(a => a.RecordHash)
            .FirstOrDefault();

        var timestamp = DateTime.UtcNow;
        var payload = string.Join('|', CurrentUser, actionType, entityType, entityId,
            timestamp.ToString("O"), details ?? string.Empty);

        var recordHash = ComputeHash((priorHash ?? string.Empty) + payload);

        var entry = new UserAction
        {
            UserName = CurrentUser,
            ActionType = actionType,
            EntityType = entityType,
            EntityId = entityId,
            Timestamp = timestamp,
            Details = details,
            PriorHash = priorHash,
            RecordHash = recordHash
        };

        _db.UserActions.Add(entry);
        _db.SaveChanges();
        return entry;
    }

    public static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public List<UserAction> GetRecent(int count = 50) =>
        _db.UserActions.OrderByDescending(a => a.Timestamp).Take(count).ToList();

    /// <summary>Re-walks the entire ledger and confirms every record's hash still
    /// matches PriorHash + payload, proving no row was altered after insertion.</summary>
    public bool VerifyChainIntegrity()
    {
        var all = _db.UserActions.OrderBy(a => a.Id).ToList();
        string? expectedPrior = null;

        foreach (var entry in all)
        {
            if (entry.PriorHash != expectedPrior) return false;

            var payload = string.Join('|', entry.UserName, entry.ActionType, entry.EntityType,
                entry.EntityId, entry.Timestamp.ToString("O"), entry.Details ?? string.Empty);
            var recomputed = ComputeHash((entry.PriorHash ?? string.Empty) + payload);

            if (recomputed != entry.RecordHash) return false;
            expectedPrior = entry.RecordHash;
        }

        return true;
    }
}
