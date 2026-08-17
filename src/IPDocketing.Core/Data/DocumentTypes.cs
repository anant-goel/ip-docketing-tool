namespace IPDocketing.Core.Data;

/// <summary>
/// The document categories docx section 5 enumerates for the status tracker
/// ("Examination Reports, Hearing Notices, Orders, Opposition Proceedings,
/// Registration Certificates, all documents available on the TMR Portal").
///
/// Kept as strings rather than an enum on purpose: Document.DocumentType is
/// already a string column with rows written against it by earlier phases, and
/// converting it to an enum would either break those rows or force another
/// destructive schema rebuild. A curated list plus a free-text fallback gets
/// the grouping the spec wants without touching existing data.
/// </summary>
public static class DocumentTypes
{
    public const string ExaminationReport = "Examination Report";
    public const string HearingNotice = "Hearing Notice";
    public const string Order = "Order";
    public const string OppositionProceeding = "Opposition Proceeding";
    public const string RegistrationCertificate = "Registration Certificate";
    public const string TmrPortalDocument = "TMR Portal Document";
    public const string Correspondence = "Correspondence";
    public const string Evidence = "Evidence";
    public const string Draft = "Draft";
    public const string PtoNotice = "PTO Notice";
    public const string General = "General";

    /// <summary>Ordered for the pickers - prosecution documents first, admin last.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        ExaminationReport,
        HearingNotice,
        Order,
        OppositionProceeding,
        RegistrationCertificate,
        TmrPortalDocument,
        Correspondence,
        Evidence,
        Draft,
        PtoNotice,
        General,
    };

    /// <summary>
    /// The subset the status tracker treats as prosecution history rather than
    /// internal working papers - drafts and general files are excluded so the
    /// printed status sheet reads like a register extract, not a file dump.
    /// </summary>
    public static readonly IReadOnlyList<string> ProsecutionRecord = new[]
    {
        ExaminationReport,
        HearingNotice,
        Order,
        OppositionProceeding,
        RegistrationCertificate,
        TmrPortalDocument,
    };

    public static bool IsProsecutionRecord(string? documentType) =>
        documentType is not null &&
        ProsecutionRecord.Contains(documentType, StringComparer.OrdinalIgnoreCase);
}
