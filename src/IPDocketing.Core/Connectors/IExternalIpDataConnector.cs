namespace IPDocketing.Core.Connectors;

/// <summary>
/// EXTENSION POINT — not wired to any UI yet.
///
/// This is the shared shape for connecting the app to the three external,
/// Python-based tools discussed:
///   - patent-client-agents  (github.com/parkerhancock/patent-client-agents)
///   - IN_patent_search      (github.com/anukriti-ranjan/IN_patent_search)
///   - ecourts               (github.com/openjustice-in/ecourts)
///
/// None of these are .NET libraries, so there is no "merge the code" option.
/// Each concrete implementation below should call out to the Python tool
/// either as:
///   (a) a short-lived subprocess per call (simplest, slowest), or
///   (b) a small local FastAPI/HTTP wrapper you run once and call over
///       localhost (faster for repeated lookups, more moving parts).
/// That decision is deferred — these interfaces exist so the rest of the
/// app (Matters, Deadlines, PtoSync pages) can be written against a stable
/// contract now, and the real implementation dropped in later without
/// touching UI code.
/// </summary>
public interface IExternalIpDataConnector
{
    /// <summary>Human-readable name shown in Settings > Connectors.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Cheap check the app can call on startup / in Settings to show a
    /// connected/not-connected badge, instead of silently failing later.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>
/// Looks up patent/trademark data from patent-client-agents. Its India
/// coverage today is limited to statute/MPPP text (not live IPO India
/// search), so IndiaLiveSearch will need IN_patent_search or a direct
/// IP-India scrape to actually return live results — see notes on that
/// interface.
/// </summary>
public interface IPatentClientAgentsConnector : IExternalIpDataConnector
{
    Task<string?> GetPatentStatuteTextAsync(string citation, CancellationToken ct = default);
    Task<string?> GetPatentBibliographicDataAsync(string publicationNumber, CancellationToken ct = default);
}

/// <summary>
/// Live IPO India patent/trademark search (fills the gap patent-client-agents
/// leaves for India). Implementation TBD pending review of the
/// IN_patent_search repo's actual capabilities.
/// </summary>
public interface IIndiaIpSearchConnector : IExternalIpDataConnector
{
    Task<string?> SearchPatentAsync(string applicationOrPublicationNumber, CancellationToken ct = default);
}

/// <summary>
/// District/High Court case-status lookups via eCourts — directly relevant
/// to Anant's HP High Court case-file work, separate from the trademark/
/// patent side of the docket.
/// </summary>
public interface IECourtsConnector : IExternalIpDataConnector
{
    Task<string?> GetCaseStatusAsync(string cnrOrCaseNumber, CancellationToken ct = default);
}
