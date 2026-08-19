using System.Text.RegularExpressions;

namespace IPDocketing.Core.Services;

/// <summary>
/// Pulls text out of a PDF. Two implementations exist and the distinction
/// matters:
///
///  - a TEXT LAYER read is exact. The characters come straight out of the file
///    and are the same characters the publisher put there.
///  - an OCR read is a guess. It misreads 0/O, 1/l/I, rn/m, and mangles the
///    stylised type that trademark journals are full of.
///
/// Every result therefore reports which method produced it, and everything
/// downstream treats OCR output with more suspicion - see
/// <see cref="ExtractionResult.IsExact"/>. Silently mixing the two is how a
/// watch service ends up quietly missing a conflicting mark because a single
/// character was misread.
/// </summary>
public interface IDocumentTextExtractor
{
    /// <summary>True when this extractor can also OCR pages that carry no text layer.</summary>
    bool SupportsOcr { get; }

    Task<ExtractionResult> ExtractAsync(string pdfPath, CancellationToken ct = default);

    /// <summary>
    /// Same extraction, but page by page. Needed to answer "which page is it
    /// on?" - a question the concatenated form structurally cannot, and the one
    /// that matters when the answer has to be checked against a 500-page PDF.
    /// </summary>
    Task<PagedExtractionResult> ExtractPagesAsync(string pdfPath, CancellationToken ct = default);
}

public sealed record PagedExtractionResult(
    List<string> Pages,
    string Method,
    string? Error = null)
{
    public bool IsExact => Method == ExtractionResult.TextLayer;
    public int PageCount => Pages.Count;
}

public sealed record ExtractionResult(
    string Text,
    string Method,
    int PageCount,
    int PagesNeedingOcr,
    string? Error = null)
{
    public const string TextLayer = "TextLayer";
    public const string Ocr = "Ocr";
    public const string Mixed = "Mixed";
    public const string Failed = "Failed";

    /// <summary>True only for a pure text-layer read - i.e. characters, not guesses.</summary>
    public bool IsExact => Method == TextLayer;

    public bool Succeeded => Error is null && Text.Length > 0;
}

/// <summary>
/// Parses published trademark entries out of Journal text.
///
/// HONEST SCOPE. The Trade Marks Journal is a typeset publication, not a data
/// feed. Each entry is a block that runs roughly:
///
///     1234567   15/03/2019
///     [device / class line]
///     MARKNAME
///     PROPRIETOR NAME AND ADDRESS
///     ...
///     Used since / Proposed to be used
///     Goods and services text
///
/// The layout drifts between issues and between classes, and OCR makes it
/// drift further. So this parser is built to be *recall-oriented and honestly
/// scored*: it finds application-number anchors reliably (a 6-8 digit number
/// followed by a date is a very strong signal) and then makes a best effort at
/// the mark and proprietor around each anchor, reporting a confidence per
/// entry rather than pretending every field is certain.
///
/// Low-confidence entries are still returned, because for a watch service a
/// possible conflict you review and dismiss costs a minute, while one you never
/// saw costs a mark. They are flagged so the UI can show what needs eyes.
/// </summary>
public class JournalMarkParser
{
    public sealed record ParsedMark(
        string ApplicationNumber,
        string Mark,
        string? Proprietor,
        string? NiceClass,
        DateTime? FilingDate,
        int Confidence)
    {
        public bool NeedsReview => Confidence < 60;
    }

    // A 6-8 digit application number followed by a date is the entry anchor.
    // Anchoring on this rather than on layout is what makes the parser survive
    // both a redesign and OCR noise.
    private static readonly Regex EntryAnchor = new(
        @"(?<app>\b\d{6,8}\b)\s+(?<date>\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4})",
        RegexOptions.Compiled);

    private static readonly Regex ClassLine = new(
        @"\bclass\s*[:\-]?\s*(?<cls>\d{1,2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Boilerplate that appears in every entry and is never a mark.
    private static readonly string[] NoiseMarkers =
    {
        "trade marks journal", "class ", "used since", "proposed to be used",
        "address for service", "advertised before acceptance", "page ",
        "registration of this trade mark", "subject to", "association with",
        "the mark is limited", "no exclusive right", "priority claimed"
    };

    /// <summary>
    /// Parses the whole extracted document. <paramref name="fromOcr"/> lowers
    /// every confidence score, because OCR text genuinely is less trustworthy
    /// and the score should say so rather than flattering the result.
    /// </summary>
    public List<ParsedMark> Parse(string text, bool fromOcr = false)
    {
        var results = new List<ParsedMark>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n')
            .Select(l => l.Trim())
            .ToList();

        // Index every anchor line, then treat the text between one anchor and
        // the next as that entry's block.
        var anchors = new List<(int LineIndex, string App, DateTime? Date)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var match = EntryAnchor.Match(lines[i]);
            if (!match.Success) continue;
            anchors.Add((i, match.Groups["app"].Value, ParseDate(match.Groups["date"].Value)));
        }

        for (var a = 0; a < anchors.Count; a++)
        {
            var (start, app, date) = anchors[a];
            var end = a + 1 < anchors.Count ? anchors[a + 1].LineIndex : Math.Min(lines.Count, start + 30);

            var block = lines.Skip(start).Take(Math.Max(1, end - start)).ToList();

            var mark = PickMark(block);
            if (mark is null) continue;

            var proprietor = PickProprietor(block, mark);
            var niceClass = PickClass(block);

            var confidence = ScoreConfidence(mark, proprietor, niceClass, date, fromOcr);

            results.Add(new ParsedMark(app, mark, proprietor, niceClass, date, confidence));
        }

        // Same application number can appear twice when a block spans a page
        // break; keep the better-scored one.
        return results
            .GroupBy(r => r.ApplicationNumber)
            .Select(g => g.OrderByDescending(r => r.Confidence).First())
            .ToList();
    }

    /// <summary>
    /// The mark is usually the most prominent short line in the block. Heuristic
    /// order: an all-caps line that isn't boilerplate, then the first short
    /// non-boilerplate line.
    /// </summary>
    private static string? PickMark(List<string> block)
    {
        var candidates = block
            .Skip(1) // the anchor line itself is the number and date
            .Where(l => l.Length is >= 2 and <= 60)
            .Where(l => !IsNoise(l))
            .Where(l => l.Any(char.IsLetter))
            .ToList();

        if (candidates.Count == 0) return null;

        var upper = candidates.FirstOrDefault(l =>
            l.Count(char.IsUpper) >= l.Count(char.IsLetter) * 0.7 &&
            l.Count(char.IsDigit) < l.Length / 2);

        var chosen = upper ?? candidates[0];
        return Clean(chosen);
    }

    /// <summary>
    /// The proprietor line typically follows the mark and carries a company
    /// suffix or an address-ish shape. Deliberately conservative - a wrong
    /// proprietor is worse than none, since it would be shown next to a
    /// conflict alert as if it were fact.
    /// </summary>
    private static string? PickProprietor(List<string> block, string mark)
    {
        var markIndex = block.FindIndex(l => Clean(l) == mark);
        var searchFrom = markIndex >= 0 ? markIndex + 1 : 1;

        string[] companyMarkers =
        {
            "pvt", "private", "limited", "ltd", "llp", "inc", "corporation",
            "company", "& co", "enterprises", "industries", "trading", "s/o", "proprietor"
        };

        for (var i = searchFrom; i < block.Count && i < searchFrom + 6; i++)
        {
            var line = block[i];
            if (line.Length is < 4 or > 160) continue;
            if (IsNoise(line)) continue;

            var lower = line.ToLowerInvariant();
            if (companyMarkers.Any(m => lower.Contains(m)))
                return Clean(line);
        }

        return null;
    }

    private static string? PickClass(List<string> block)
    {
        foreach (var line in block)
        {
            var match = ClassLine.Match(line);
            if (!match.Success) continue;
            var value = match.Groups["cls"].Value;
            if (int.TryParse(value, out var n) && n is >= 1 and <= 45) return value;
        }
        return null;
    }

    /// <summary>
    /// Confidence is a deliberately blunt instrument. It exists so a reviewer
    /// knows which entries to check, not so anything can be auto-accepted -
    /// nothing in this app treats a high score as permission to skip a human.
    /// </summary>
    private static int ScoreConfidence(string mark, string? proprietor, string? niceClass,
                                       DateTime? date, bool fromOcr)
    {
        var score = 40;

        if (mark.Length is >= 3 and <= 40) score += 15;
        if (mark.All(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '-' or '&' or '.')) score += 10;
        if (proprietor is not null) score += 15;
        if (niceClass is not null) score += 10;
        if (date is not null) score += 10;

        // A mark full of characters OCR commonly invents is a warning sign.
        if (mark.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)) > mark.Length / 4) score -= 20;

        // OCR text is a guess, and the score should admit it.
        if (fromOcr) score -= 20;

        return Math.Clamp(score, 0, 100);
    }

    private static bool IsNoise(string line)
    {
        var lower = line.ToLowerInvariant();
        if (NoiseMarkers.Any(n => lower.Contains(n))) return true;
        if (line.Count(char.IsDigit) > line.Length * 0.6) return true;
        return false;
    }

    private static string Clean(string line) =>
        Regex.Replace(line, @"\s+", " ").Trim(' ', '.', ',', ';', ':', '-', '_');

    private static DateTime? ParseDate(string text)
    {
        string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yy" };
        foreach (var format in formats)
            if (DateTime.TryParseExact(text, format,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
                return parsed.Date;
        return null;
    }
}
