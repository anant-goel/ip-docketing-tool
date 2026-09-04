namespace IPDocketing.Core.Services;

/// <summary>
/// Tries each journal source in order and uses the first that actually
/// produces links.
///
/// Order, and why:
///   1. SESSION CLIENT - cookies plus postback replay, no browser. Fast, needs
///      no UI thread, works in the background sync, cannot paint itself over
///      the app. Handles a real href and a __doPostBack target.
///   2. EMBEDDED BROWSER - executes script, so it handles links whose target is
///      computed at click time, which the session client cannot see.
///
/// The important rule: a source that returns issues but ZERO links is treated
/// as a failure and the next one is tried. That specific outcome is what every
/// version of this has produced while looking superficially successful, so it
/// must not be mistaken for a real answer.
///
/// Which source answered is recorded in <see cref="LastSourceUsed"/> and in the
/// attempt log, so a thin result is always attributable.
/// </summary>
public sealed class ChainedJournalSource : IJournalSource, IDisposable
{
    private readonly List<IJournalSource> _sources;

    public string SourceName => LastSourceUsed ?? "Chained (none tried yet)";

    /// <summary>Name of the source that last produced usable data.</summary>
    public string? LastSourceUsed { get; private set; }

    /// <summary>What each source did on the last attempt, for the self-test.</summary>
    public IReadOnlyList<string> AttemptLog => _attemptLog;
    private readonly List<string> _attemptLog = new();

    /// <summary>The source that answered last, so a download uses the same one that listed.</summary>
    private IJournalSource? _working;

    /// <summary>Sources constructed here, and therefore disposed here.</summary>
    private readonly List<IJournalSource> _owned;

    public ChainedJournalSource(params IJournalSource[] sources)
    {
        _sources = sources.Where(s => s is not null).ToList();
        _owned = _sources.ToList();
    }

    /// <summary>
    /// Adds a source, REPLACING any earlier one with the same name.
    ///
    /// Replacing rather than appending matters because the browser source is
    /// registered by the page that owns it, and that page builds a fresh browser
    /// on each run and disposes the previous one. Appending would leave the
    /// chain holding disposed instances that throw the moment they are tried -
    /// and, worse, throw BEFORE the live one further down the list is reached.
    /// Ownership stays with the caller: this list is not disposed on replace.
    /// </summary>
    public void Add(IJournalSource source)
    {
        if (source is null) return;

        var existing = _sources.FindIndex(s =>
            string.Equals(s.SourceName, source.SourceName, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0) _sources[existing] = source;
        else _sources.Add(source);

        // A replaced source must not stay latched as the one downloads go to.
        if (_working is not null &&
            string.Equals(_working.SourceName, source.SourceName, StringComparison.OrdinalIgnoreCase))
            _working = source;
    }

    public async Task<List<JournalSourceIssue>> ListIssuesAsync(CancellationToken ct = default)
    {
        _attemptLog.Clear();

        List<JournalSourceIssue>? bestSoFar = null;
        IJournalSource? bestSource = null;

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var issues = await source.ListIssuesAsync(ct);
                var links = issues.Sum(i => i.Links.Count);

                _attemptLog.Add($"{source.SourceName}: {issues.Count} issue(s), {links} link(s)");

                if (links > 0)
                {
                    LastSourceUsed = source.SourceName;
                    _working = source;
                    return issues;
                }

                // Issues but no links: keep it only as a last resort, because
                // this is exactly the misleading half-success to avoid.
                if (bestSoFar is null && issues.Count > 0)
                {
                    bestSoFar = issues;
                    bestSource = source;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _attemptLog.Add($"{source.SourceName}: FAILED - {ex.Message}");
            }
        }

        if (bestSoFar is not null)
        {
            LastSourceUsed = (bestSource?.SourceName ?? "unknown") + " (issues only, NO LINKS)";
            _working = bestSource;
            return bestSoFar;
        }

        LastSourceUsed = "none succeeded";
        return new List<JournalSourceIssue>();
    }

    public async Task<JournalDownloadResult> DownloadAsync(
        JournalSourceIssue issue, JournalSourceLink link, string targetPath,
        CancellationToken ct = default)
    {
        // The source that listed is tried first: element indexes and postback
        // targets are only meaningful to the source that produced them.
        var ordered = _working is null
            ? _sources
            : new[] { _working }.Concat(_sources.Where(s => s != _working)).ToList();

        JournalDownloadResult? last = null;

        foreach (var source in ordered)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await source.DownloadAsync(issue, link, targetPath, ct);
                if (result.Saved) return result;
                last = result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = JournalDownloadResult.Failure($"{source.SourceName}: {ex.Message}");
            }
        }

        return last ?? JournalDownloadResult.Failure("No source could download this link.");
    }

    public async Task<List<JournalDownloadResult>> DownloadIssueAsync(
        string journalNumber, string targetDirectory, int maxFiles = 8,
        CancellationToken ct = default)
    {
        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var results = await source.DownloadIssueAsync(journalNumber, targetDirectory, maxFiles, ct);
                if (results.Any(r => r.Saved))
                {
                    LastSourceUsed = source.SourceName;
                    _working = source;
                    return results;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _attemptLog.Add($"{source.SourceName}: FAILED - {ex.Message}");
            }
        }

        return new List<JournalDownloadResult>
        {
            JournalDownloadResult.Failure(
                "No source could obtain this issue. " + string.Join(" | ", _attemptLog))
        };
    }

    /// <summary>
    /// Disposes only the sources this chain created itself.
    ///
    /// Sources handed in through <see cref="Add"/> belong to their caller - the
    /// browser is owned by the page that hosts it and is disposed there - so
    /// disposing them here would tear down a live browser out from under the
    /// page still using it.
    /// </summary>
    public void Dispose()
    {
        foreach (var source in _owned.OfType<IDisposable>())
        {
            try { source.Dispose(); } catch { /* teardown */ }
        }
    }
}
