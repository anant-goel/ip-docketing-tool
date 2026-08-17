using System.Security.Cryptography;
using IPDocketing.Core.Data;
using IPDocketing.Core.Models;

namespace IPDocketing.Core.Services;

/// <summary>
/// Files documents pulled off the TMR status page into the docket: writes the
/// bytes to the document store, classifies each one, creates the Document row,
/// and refuses to store the same file twice.
///
/// SCOPE. These are documents on your own matters, fetched inside a session you
/// authenticated and a CAPTCHA you solved. Nothing here defeats an access
/// control; it saves you clicking Download forty times and then filing forty
/// PDFs by hand.
///
/// DEDUPLICATION IS BY CONTENT, NOT BY NAME. The Registry serves the same
/// document under different URLs and different display names across visits, and
/// often with a generic filename like "ViewDocument.pdf". Hashing the bytes is
/// the only reliable way to know whether this is genuinely a new filing or the
/// same examination report fetched again. A docket where every refresh adds
/// another copy of the same order is one nobody can read.
/// </summary>
public class DocumentIngestService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly string _storeRoot;

    public DocumentIngestService(AppDbContext db, AuditService audit, string storeRoot)
    {
        _db = db;
        _audit = audit;
        _storeRoot = storeRoot;
        Directory.CreateDirectory(_storeRoot);
    }

    public sealed record IngestResult(
        bool Saved,
        string Reason,
        string? FilePath,
        string DocumentType,
        int? DocumentId)
    {
        public static IngestResult Skipped(string reason, string type = "") =>
            new(false, reason, null, type, null);
    }

    /// <summary>
    /// Stores one fetched file against a matter.
    /// </summary>
    /// <param name="label">The link text from the page - drives classification.</param>
    /// <param name="context">Surrounding row text, used when the label is generic.</param>
    public IngestResult Ingest(int matterId, byte[] content, string label, string? context,
                               string? contentType, DateTime? documentDate)
    {
        if (content.Length < 512)
            return IngestResult.Skipped("The download was empty or truncated.");

        var matter = _db.Matters.FirstOrDefault(m => m.Id == matterId);
        if (matter is null)
            return IngestResult.Skipped("That matter no longer exists.");

        var documentType = Classify(label, context);

        // Content hash before anything is written. Comparing against every
        // document already on this matter catches the same filing arriving
        // under a new URL or a new display name.
        var hash = Convert.ToHexString(SHA256.HashData(content));

        var existingOnMatter = _db.Documents
            .Where(d => d.MatterId == matterId)
            .ToList();

        foreach (var existing in existingOnMatter)
        {
            if (!File.Exists(existing.FilePath)) continue;
            try
            {
                if (new FileInfo(existing.FilePath).Length != content.Length) continue;
                var existingHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(existing.FilePath)));
                if (existingHash == hash)
                    return IngestResult.Skipped($"Already on file as {existing.FileName}.", documentType);
            }
            catch
            {
                // An unreadable existing file shouldn't block a new one.
            }
        }

        var extension = ExtensionFor(contentType, label);
        var matterFolder = Path.Combine(_storeRoot, SafeName(matter.MatterNumber));
        Directory.CreateDirectory(matterFolder);

        // Version by document type: the second examination report on a matter
        // is v2 of that type, not v2 of the matter.
        var version = existingOnMatter.Count(d =>
            string.Equals(d.DocumentType, documentType, StringComparison.OrdinalIgnoreCase)) + 1;

        var stamp = (documentDate ?? DateTime.Today).ToString("yyyy-MM-dd");
        var fileName = $"{stamp}_{SafeName(documentType)}_v{version}{extension}";
        var filePath = Path.Combine(matterFolder, fileName);

        // Never silently overwrite - a name collision here means two genuinely
        // different documents, since identical content was already ruled out.
        var attempt = 1;
        while (File.Exists(filePath))
        {
            fileName = $"{stamp}_{SafeName(documentType)}_v{version}_{++attempt}{extension}";
            filePath = Path.Combine(matterFolder, fileName);
        }

        File.WriteAllBytes(filePath, content);

        var document = new Document
        {
            MatterId = matterId,
            FileName = fileName,
            FilePath = filePath,
            DocumentType = documentType,
            Version = version,
            UploadedDate = documentDate?.ToUniversalTime() ?? DateTime.UtcNow
        };

        _db.Documents.Add(document);
        _db.SaveChanges();

        _audit.Log("Ingest", "Document", document.Id,
            $"Fetched '{label}' from the portal for matter {matter.MatterNumber}; " +
            $"classified as {documentType}, {content.Length} bytes.");

        return new IngestResult(true, "Saved.", filePath, documentType, document.Id);
    }

    /// <summary>
    /// Maps the link label onto one of the categories docx section 5 names.
    /// Ordered most-specific first, because "Reply to Examination Report"
    /// contains "Examination Report" and must not be filed as one.
    /// </summary>
    public static string Classify(string? label, string? context = null)
    {
        var text = ((label ?? "") + " " + (context ?? "")).ToLowerInvariant();

        if (text.Contains("registration certificate") || text.Contains("certificate of registration"))
            return DocumentTypes.RegistrationCertificate;

        if (text.Contains("counter statement") || text.Contains("counter-statement") ||
            text.Contains("notice of opposition") || text.Contains("tm-o") ||
            text.Contains("opposition"))
            return DocumentTypes.OppositionProceeding;

        if (text.Contains("reply to examination") || text.Contains("reply to the examination") ||
            text.Contains("response to examination"))
            return DocumentTypes.Correspondence;

        if (text.Contains("examination report") || text.Contains("exam report") ||
            text.Contains("objection"))
            return DocumentTypes.ExaminationReport;

        if (text.Contains("hearing notice") || text.Contains("notice of hearing") ||
            text.Contains("show cause") || text.Contains("hearing"))
            return DocumentTypes.HearingNotice;

        if (text.Contains("order") || text.Contains("judgement") || text.Contains("judgment") ||
            text.Contains("decision"))
            return DocumentTypes.Order;

        if (text.Contains("affidavit") || text.Contains("evidence"))
            return DocumentTypes.Evidence;

        if (text.Contains("letter") || text.Contains("correspondence") || text.Contains("reply"))
            return DocumentTypes.Correspondence;

        // Everything else off the portal is still portal-sourced, and saying so
        // is more useful than filing it as "General".
        return DocumentTypes.TmrPortalDocument;
    }

    /// <summary>
    /// Applies a status read off the portal to the matter, recording an Event
    /// when it actually changed.
    ///
    /// A status change is the thing the whole docket turns on - Objected starts
    /// a reply clock, Advertised starts the opposition period - so a change is
    /// logged as a real Event rather than quietly overwriting a field. What was
    /// there before stays visible in the prosecution history.
    /// </summary>
    public bool ApplyStatus(int matterId, string? portalStatus, string? alertText)
    {
        var matter = _db.Matters.FirstOrDefault(m => m.Id == matterId);
        if (matter is null || string.IsNullOrWhiteSpace(portalStatus)) return false;

        var changed = false;
        var previousAlert = matter.PortalAlert;

        if (!string.Equals(previousAlert, portalStatus, StringComparison.OrdinalIgnoreCase))
        {
            matter.PortalAlert = portalStatus.Trim();
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(alertText) &&
            !string.Equals(matter.PortalAlert, alertText, StringComparison.OrdinalIgnoreCase))
        {
            matter.PortalAlert = $"{portalStatus.Trim()} — {alertText.Trim()}";
            changed = true;
        }

        var mapped = MapStatus(portalStatus);
        if (mapped is not null && matter.Status != mapped)
        {
            matter.Status = mapped.Value;
            changed = true;
        }

        if (!changed) return false;

        _db.Matters.Update(matter);
        _db.Events.Add(new Event
        {
            MatterId = matterId,
            Type = EventTypeFor(portalStatus),
            EventDate = DateTime.Today,
            Notes = $"Portal status read as '{portalStatus.Trim()}'" +
                    (string.IsNullOrWhiteSpace(previousAlert) ? "." : $" (was '{previousAlert}').")
        });
        _db.SaveChanges();

        _audit.Log("Update", "Matter", matterId,
            $"Status updated from the portal: '{previousAlert ?? "none"}' -> '{matter.PortalAlert}'.");

        return true;
    }

    private static MatterStatus? MapStatus(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("registered")) return MatterStatus.Granted;
        if (t.Contains("abandoned") || t.Contains("withdrawn") || t.Contains("refused")) return MatterStatus.Abandoned;
        if (t.Contains("removed") || t.Contains("expired")) return MatterStatus.Expired;
        if (t.Contains("opposed") || t.Contains("objected") || t.Contains("advertised") ||
            t.Contains("accepted") || t.Contains("examination") || t.Contains("formalities") ||
            t.Contains("send to vienna") || t.Contains("marked for exam")) return MatterStatus.Pending;
        return null;
    }

    private static EventType EventTypeFor(string status)
    {
        var t = status.ToLowerInvariant();
        if (t.Contains("advertised") || t.Contains("published")) return EventType.Publication;
        if (t.Contains("opposed") || t.Contains("opposition")) return EventType.Opposition;
        if (t.Contains("objected") || t.Contains("examination")) return EventType.OfficeAction;
        if (t.Contains("registered")) return EventType.Grant;
        if (t.Contains("abandoned") || t.Contains("withdrawn")) return EventType.Abandonment;
        return EventType.Other;
    }

    private static string ExtensionFor(string? contentType, string label)
    {
        var type = (contentType ?? "").ToLowerInvariant();
        if (type.Contains("pdf")) return ".pdf";
        if (type.Contains("tiff")) return ".tif";
        if (type.Contains("jpeg") || type.Contains("jpg")) return ".jpg";
        if (type.Contains("png")) return ".png";
        if (type.Contains("word") || type.Contains("officedocument.wordprocessing")) return ".docx";
        if (type.Contains("zip")) return ".zip";

        var lower = label.ToLowerInvariant();
        if (lower.EndsWith(".pdf")) return ".pdf";
        if (lower.EndsWith(".tif") || lower.EndsWith(".tiff")) return ".tif";

        // Portal documents are overwhelmingly PDF; guessing that beats an
        // extensionless file Windows cannot open.
        return ".pdf";
    }

    private static string SafeName(string value)
    {
        var cleaned = string.Concat(value.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}
