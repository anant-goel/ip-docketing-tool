namespace IPDocketing.Core.Models;

/// <summary>
/// Internal team member a Matter or Opposition can be assigned to
/// (docx sections 2 and 3: "tool to assign a particular TM to team member").
/// Deliberately minimal — this is an internal assignee list, not a user
/// account/auth system.
/// </summary>
public class TeamMember
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Role { get; set; }
    /// <summary>
    /// This person's agent registration code, where they are a registered TM or
    /// patent agent. Used to group the firm's filings by whose code they went in
    /// under, which is how the Registry itself identifies them.
    /// </summary>
    public string? AgentCode { get; set; }

    public bool IsActive { get; set; } = true;
}
