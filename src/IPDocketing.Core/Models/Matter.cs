namespace IPDocketing.Core.Models;

public class Matter
{
    public int Id { get; set; }
    public string MatterNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public MatterType Type { get; set; }
    public string Country { get; set; } = "US";
    public MatterStatus Status { get; set; } = MatterStatus.Pending;

    public DateTime? PriorityDate { get; set; }
    public DateTime? FilingDate { get; set; }
    public string? ApplicationNumber { get; set; }
    public string? PublicationNumber { get; set; }
    public string? GrantNumber { get; set; }

    // --- Trademark Master Database fields (docx section 2) ---
    // Left nullable so Patent/Copyright/TradeSecret matters are unaffected.
    public string? ProprietorName { get; set; }
    public string? AttorneyOfRecord { get; set; }
    public string? State { get; set; }
    public MarkType? MarkType { get; set; }
    public string? NiceClass { get; set; }

    /// <summary>
    /// The agent's / attorney's registration code with the Registry (for an
    /// Indian trademark agent, the TM agent registration number; for patents,
    /// the IN/PA number). Stored separately from AttorneyOfRecord because the
    /// name is what the register displays while the code is what identifies a
    /// firm's filings unambiguously - two agents share a surname far more often
    /// than they share a registration number.
    /// </summary>
    public string? AttorneyCode { get; set; }

    /// <summary>
    /// Free-text copy of any alert banner shown against this mark on the TMR
    /// status page (e.g. "Opposed", "Objected", "Abandoned - no reply to
    /// examination report"). docx section 6 asks for search results to be
    /// filterable on exactly this, so it has to be a stored field rather than
    /// something inferred from Status.
    /// </summary>
    public string? PortalAlert { get; set; }

    /// <summary>Date the mark was entered on the register, where it got that far.</summary>
    public DateTime? RegistrationDate { get; set; }

    /// <summary>Next renewal due date (India: 10 years from registration, Section 25).</summary>
    public DateTime? RenewalDueDate { get; set; }

    // --- Assignment (docx: "tool to assign a particular TM to team member") ---
    public int? AssignedToId { get; set; }
    public TeamMember? AssignedTo { get; set; }

    public int? ParentMatterId { get; set; }
    public Matter? ParentMatter { get; set; }
    public List<Matter> ChildMatters { get; set; } = new();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    public List<Event> Events { get; set; } = new();
    public List<Deadline> Deadlines { get; set; } = new();
    public List<Document> Documents { get; set; } = new();
}
